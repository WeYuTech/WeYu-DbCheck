using System.Text.RegularExpressions;

namespace DbCheck.Workbench;

internal enum DiffKind { Equal, Added, Removed, Modified, Fold }
internal sealed record TextSpan(int Start, int Length);
internal sealed record DiffRow(DiffKind Kind, int? LeftNumber, string Left, int? RightNumber, string Right)
{
    public IReadOnlyList<TextSpan> LeftHighlights { get; init; } = [];
    public IReadOnlyList<TextSpan> RightHighlights { get; init; } = [];
    public int OriginalIndex { get; init; }
    public int HiddenCount { get; init; }
    public bool Changed => Kind is DiffKind.Added or DiffKind.Removed or DiffKind.Modified;
}
internal sealed record DiffDocument(IReadOnlyList<DiffRow> Rows, bool Simplified, bool Truncated);

/// <summary>Display-only diff: exact comparison decisions live in ComparisonEngine, never here.</summary>
internal static class DiffEngine
{
    private const int MaximumPreviewCharacters = 2_000_000;
    private const int MaximumPreviewLines = 20_000;
    private const long CellBudget = 4_000_000;
    private static readonly Regex Tokens = new(@"\w+|\s+|[^\w\s]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private sealed record Edit(DiffKind Kind, int? Left, int? Right);

    public static DiffDocument Compute(string before, string after, CancellationToken token = default)
    {
        var truncated = false;
        string[] Lines(string text)
        {
            text = SqlText.NormalizeLines(text);
            if (text.Length > MaximumPreviewCharacters) { text = text[..MaximumPreviewCharacters]; truncated = true; }
            var lines = text.Length == 0 ? Array.Empty<string>() : text.Split('\n');
            if (lines.Length > MaximumPreviewLines) { lines = lines[..MaximumPreviewLines]; truncated = true; }
            return lines;
        }
        var left = Lines(before); var right = Lines(after);
        var edits = new List<Edit>();
        long remaining = CellBudget;
        var simplified = false;
        CompareRange(0, left.Length, 0, right.Length, 0);
        var rows = new List<DiffRow>();
        long wordBudget = 1_000_000;
        for (var e = 0; e < edits.Count;)
        {
            token.ThrowIfCancellationRequested();
            if (edits[e].Kind == DiffKind.Equal)
            {
                var item = edits[e++];
                rows.Add(new(DiffKind.Equal, item.Left!.Value + 1, left[item.Left.Value], item.Right!.Value + 1, right[item.Right.Value]));
                continue;
            }
            var removed = new List<int>(); var added = new List<int>();
            while (e < edits.Count && edits[e].Kind != DiffKind.Equal)
            {
                if (edits[e].Left is { } l) removed.Add(l);
                if (edits[e].Right is { } r) added.Add(r);
                e++;
            }
            for (var i = 0; i < Math.Max(removed.Count, added.Count); i++)
            {
                int? l = i < removed.Count ? removed[i] : null;
                int? r = i < added.Count ? added[i] : null;
                var kind = l.HasValue && r.HasValue ? DiffKind.Modified : l.HasValue ? DiffKind.Removed : DiffKind.Added;
                var row = new DiffRow(kind, l + 1, l.HasValue ? left[l.Value] : "", r + 1, r.HasValue ? right[r.Value] : "");
                if (kind == DiffKind.Modified)
                {
                    var spans = Highlights(row.Left, row.Right, ref wordBudget);
                    row = row with { LeftHighlights = spans.Left, RightHighlights = spans.Right };
                }
                rows.Add(row);
            }
        }
        return new(rows.Select((row, index) => row with { OriginalIndex = index }).ToArray(), simplified, truncated);

        void CompareRange(int aStart, int aEnd, int bStart, int bEnd, int depth)
        {
            token.ThrowIfCancellationRequested();
            while (aStart < aEnd && bStart < bEnd && left[aStart] == right[bStart]) edits.Add(new(DiffKind.Equal, aStart++, bStart++));
            var suffix = 0;
            while (aStart < aEnd - suffix && bStart < bEnd - suffix && left[aEnd - suffix - 1] == right[bEnd - suffix - 1]) suffix++;
            var aStop = aEnd - suffix; var bStop = bEnd - suffix;
            var n = aStop - aStart; var m = bStop - bStart;
            var cells = ((long)n + 1) * ((long)m + 1);
            if (n == 0)
                for (var b = bStart; b < bStop; b++) edits.Add(new(DiffKind.Added, null, b));
            else if (m == 0)
                for (var a = aStart; a < aStop; a++) edits.Add(new(DiffKind.Removed, a, null));
            else if (cells <= remaining)
            {
                remaining -= cells;
                var width = m + 1;
                var matrix = new int[(int)cells];
                for (var a = n - 1; a >= 0; a--)
                {
                    token.ThrowIfCancellationRequested();
                    for (var b = m - 1; b >= 0; b--)
                        matrix[a * width + b] = left[aStart + a] == right[bStart + b] ? matrix[(a + 1) * width + b + 1] + 1 : Math.Max(matrix[(a + 1) * width + b], matrix[a * width + b + 1]);
                }
                var x = 0; var y = 0;
                while (x < n || y < m)
                {
                    if (x < n && y < m && left[aStart + x] == right[bStart + y]) edits.Add(new(DiffKind.Equal, aStart + x++, bStart + y++));
                    else if (x < n && (y == m || matrix[(x + 1) * width + y] >= matrix[x * width + y + 1])) edits.Add(new(DiffKind.Removed, aStart + x++, null));
                    else edits.Add(new(DiffKind.Added, null, bStart + y++));
                }
            }
            else
            {
                var anchors = depth < 32 ? UniqueAnchors(left, aStart, aStop, right, bStart, bStop) : [];
                if (anchors.Count == 0)
                {
                    simplified = true;
                    for (var a = aStart; a < aStop; a++) edits.Add(new(DiffKind.Removed, a, null));
                    for (var b = bStart; b < bStop; b++) edits.Add(new(DiffKind.Added, null, b));
                }
                else
                {
                    var a = aStart; var b = bStart;
                    foreach (var anchor in anchors)
                    {
                        CompareRange(a, anchor.Left, b, anchor.Right, depth + 1);
                        edits.Add(new(DiffKind.Equal, anchor.Left, anchor.Right));
                        a = anchor.Left + 1; b = anchor.Right + 1;
                    }
                    CompareRange(a, aStop, b, bStop, depth + 1);
                }
            }
            for (var i = 0; i < suffix; i++) edits.Add(new(DiffKind.Equal, aStop + i, bStop + i));
        }
    }

    private static List<(int Left, int Right)> UniqueAnchors(string[] left, int aStart, int aEnd, string[] right, int bStart, int bEnd)
    {
        static Dictionary<string, int> Unique(string[] lines, int start, int end)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = start; i < end; i++) { if (!map.TryAdd(lines[i], i)) map[lines[i]] = -1; }
            return map;
        }
        var a = Unique(left, aStart, aEnd); var b = Unique(right, bStart, bEnd);
        var matches = a.Where(p => p.Value >= 0 && b.TryGetValue(p.Key, out var n) && n >= 0).Select(p => (Left: p.Value, Right: b[p.Key])).OrderBy(p => p.Left).ToArray();
        if (matches.Length == 0) return [];
        var tails = new List<int>(); var previous = new int[matches.Length]; Array.Fill(previous, -1);
        for (var i = 0; i < matches.Length; i++)
        {
            var low = 0; var high = tails.Count;
            while (low < high) { var middle = (low + high) / 2; if (matches[tails[middle]].Right < matches[i].Right) low = middle + 1; else high = middle; }
            if (low > 0) previous[i] = tails[low - 1];
            if (low == tails.Count) tails.Add(i); else tails[low] = i;
        }
        var result = new List<(int, int)>();
        for (var i = tails[^1]; i >= 0; i = previous[i]) result.Add(matches[i]);
        result.Reverse(); return result;
    }

    private static (IReadOnlyList<TextSpan> Left, IReadOnlyList<TextSpan> Right) Highlights(string left, string right, ref long budget)
    {
        if (left.Length > 4096 || right.Length > 4096) return SimpleHighlights(left, right);
        var a = Tokens.Matches(left).Cast<Match>().ToArray(); var b = Tokens.Matches(right).Cast<Match>().ToArray();
        var cells = ((long)a.Length + 1) * (b.Length + 1);
        if (a.Length > 256 || b.Length > 256 || cells > budget) return SimpleHighlights(left, right);
        budget -= cells;
        var width = b.Length + 1; var matrix = new int[(int)cells];
        for (var x = a.Length - 1; x >= 0; x--)
            for (var y = b.Length - 1; y >= 0; y--)
                matrix[x * width + y] = a[x].Value == b[y].Value ? matrix[(x + 1) * width + y + 1] + 1 : Math.Max(matrix[(x + 1) * width + y], matrix[x * width + y + 1]);
        var al = Enumerable.Repeat(true, a.Length).ToArray(); var bl = Enumerable.Repeat(true, b.Length).ToArray();
        var i = 0; var j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (a[i].Value == b[j].Value) { al[i++] = false; bl[j++] = false; }
            else if (matrix[(i + 1) * width + j] >= matrix[i * width + j + 1]) i++; else j++;
        }
        static List<TextSpan> Spans(Match[] tokens, bool[] changed)
        {
            var spans = new List<TextSpan>();
            for (var t = 0; t < tokens.Length; t++)
            {
                if (!changed[t]) continue;
                var start = tokens[t].Index; var end = start + tokens[t].Length;
                while (t + 1 < tokens.Length && changed[t + 1]) { t++; end = tokens[t].Index + tokens[t].Length; }
                spans.Add(new(start, end - start));
            }
            return spans;
        }
        return (Spans(a, al), Spans(b, bl));
    }

    private static (IReadOnlyList<TextSpan> Left, IReadOnlyList<TextSpan> Right) SimpleHighlights(string left, string right)
    {
        var start = 0;
        while (start < left.Length && start < right.Length && left[start] == right[start]) start++;
        var end = 0;
        while (end < left.Length - start && end < right.Length - start && left[^(end + 1)] == right[^(end + 1)]) end++;
        return (left.Length - start - end > 0 ? [new TextSpan(start, left.Length - start - end)] : [], right.Length - start - end > 0 ? [new TextSpan(start, right.Length - start - end)] : []);
    }

    public static IReadOnlyList<DiffRow> Collapse(IReadOnlyList<DiffRow> rows, int context = 3, ISet<int>? expanded = null)
    {
        if (context < 0) throw new ArgumentOutOfRangeException(nameof(context));
        var keep = new bool[rows.Count];
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].Changed) for (var j = Math.Max(0, i - context); j <= Math.Min(rows.Count - 1, i + context); j++) keep[j] = true;
        var result = new List<DiffRow>();
        for (var i = 0; i < rows.Count;)
        {
            if (keep[i]) { result.Add(rows[i++]); continue; }
            var start = i; while (i < rows.Count && !keep[i]) i++;
            if (expanded?.Contains(start) == true || i - start <= context * 2 + 1)
                for (var j = start; j < i; j++) result.Add(rows[j]);
            else result.Add(new(DiffKind.Fold, null, $"⋯ {i - start} 行相同內容（雙擊展開）", null, $"⋯ {i - start} 行相同內容（雙擊展開）") { OriginalIndex = start, HiddenCount = i - start });
        }
        return result;
    }

    public static IReadOnlyList<DiffRow> Unified(IReadOnlyList<DiffRow> rows)
    {
        var result = new List<DiffRow>();
        foreach (var row in rows)
        {
            if (row.Kind == DiffKind.Modified)
            {
                result.Add(row with { Kind = DiffKind.Removed, RightNumber = null, Right = "", RightHighlights = [] });
                result.Add(row with { Kind = DiffKind.Added, LeftNumber = null, Left = "", LeftHighlights = [] });
            }
            else result.Add(row);
        }
        return result;
    }
}
