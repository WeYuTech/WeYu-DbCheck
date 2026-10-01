using System.Drawing.Drawing2D;

namespace DbCheck.Workbench;

internal sealed class GitDiffView : UserControl
{
    private readonly DataGridView left = CreateGrid(false), right = CreateGrid(false), unified = CreateGrid(true);
    private readonly SplitContainer split = new() { Size = new Size(900, 450), Dock = DockStyle.Fill, SplitterDistance = 450, SplitterWidth = 6, Panel1MinSize = 120, Panel2MinSize = 120, BackColor = Ui.Border };
    private readonly Panel body = new() { Dock = DockStyle.Fill };
    private readonly ToolStripComboBox mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
    private readonly ToolStripButton fold = new("摺疊相同") { CheckOnClick = true, Checked = true };
    private readonly ToolStripButton sync = new("同步捲動") { CheckOnClick = true, Checked = true };
    private readonly ToolStripLabel count = new("尚未選取物件");
    private readonly Label note = new() { Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = Ui.Muted, TextAlign = ContentAlignment.MiddleLeft };
    private readonly HashSet<int> expanded = [];
    private DiffDocument document = new([], false, false);
    private IReadOnlyList<DiffRow> rows = [];
    private string currentText = "", standardText = "";
    private CancellationTokenSource? load;
    private int generation;
    private bool scrolling;
    private int changePosition = -1;
    private int[] changes = [];

    public GitDiffView()
    {
        Dock = DockStyle.Fill; BackColor = Color.White;
        var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, BackColor = Color.White, Padding = new Padding(3), Stretch = true, Dock = DockStyle.Fill, RenderMode = ToolStripRenderMode.System };
        mode.Items.AddRange(new object[] { "左右並排", "合併檢視" }); mode.SelectedIndex = 0;
        var previous = new ToolStripButton("上一處"); var next = new ToolStripButton("下一處");
        var copyCurrent = new ToolStripButton("複製目前"); var copyStandard = new ToolStripButton("複製標準");
        toolbar.Items.AddRange([mode, previous, next, new ToolStripSeparator(), fold, sync, new ToolStripSeparator(), copyCurrent, copyStandard, count]);
        mode.SelectedIndexChanged += (_, _) => Render(); fold.CheckedChanged += (_, _) => Render();
        previous.Click += (_, _) => Navigate(-1); next.Click += (_, _) => Navigate(1);
        copyCurrent.Click += (_, _) => Ui.Copy(this, currentText); copyStandard.Click += (_, _) => Ui.Copy(this, standardText);
        left.Columns[1].HeaderText = "檢查 DB（目前）"; right.Columns[1].HeaderText = "標準 DB（基準）";
        split.Panel1.Controls.Add(left); split.Panel2.Controls.Add(right);
        body.Controls.Add(split); body.Controls.Add(unified); unified.Visible = false;
        var layout = Ui.Rows(new(SizeType.Absolute, 36), new(SizeType.Absolute, 28), new(SizeType.Percent, 100));
        layout.Controls.Add(toolbar, 0, 0); layout.Controls.Add(note, 0, 1); layout.Controls.Add(body, 0, 2); Controls.Add(layout);
        Bind(left, false, false); Bind(right, true, false); Bind(unified, false, true);
        left.Scroll += (_, e) => Synchronize(left, right, e); right.Scroll += (_, e) => Synchronize(right, left, e);
        foreach (var grid in new[] { left, right, unified })
        {
            grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && e.RowIndex < rows.Count && rows[e.RowIndex].Kind == DiffKind.Fold) { expanded.Add(rows[e.RowIndex].OriginalIndex); Render(); } };
            grid.KeyDown += (_, e) => { if (e.KeyCode == Keys.F7) { Navigate(e.Shift ? -1 : 1); e.Handled = true; } };
        }
        note.Text = "左邊：檢查 DB 目前內容　｜　右邊：標準 DB 基準　｜　紅色不代表會刪除資料";
    }

    public async Task ShowDiffAsync(string current, string standard, CancellationToken token = default)
    {
        load?.Cancel(); load?.Dispose();
        load = CancellationTokenSource.CreateLinkedTokenSource(token);
        var cancellation = load.Token; var version = ++generation;
        note.Text = "正在計算差異…";
        try
        {
            var result = await Task.Run(() => DiffEngine.Compute(current, standard, cancellation), cancellation);
            if (IsDisposed || cancellation.IsCancellationRequested || version != generation) return;
            currentText = current; standardText = standard; document = result; expanded.Clear(); Render();
        }
        catch (OperationCanceledException) { }
    }

    public void SetDocument(string current, string standard)
    {
        load?.Cancel(); generation++; currentText = current; standardText = standard;
        document = DiffEngine.Compute(current, standard); expanded.Clear(); Render();
    }
    public void Clear() => SetDocument("", "");

    private void Render()
    {
        if (IsDisposed) return;
        var compact = fold.Checked ? DiffEngine.Collapse(document.Rows, 3, expanded) : document.Rows;
        var isUnified = mode.SelectedIndex == 1;
        rows = isUnified ? DiffEngine.Unified(compact) : compact;
        scrolling = true;
        try
        {
            foreach (var grid in new[] { left, right, unified }) { grid.CurrentCell = null; grid.RowCount = rows.Count; grid.ClearSelection(); }
            split.Visible = !isUnified; unified.Visible = isUnified; if (isUnified) unified.BringToFront(); else split.BringToFront();
            var width = Math.Clamp(document.Rows.Select(r => Math.Max(r.Left.Length, r.Right.Length)).DefaultIfEmpty(40).Max() * 8 + 36, 500, 16000);
            left.Columns[1].Width = width; right.Columns[1].Width = width; unified.Columns[2].Width = width;
            changes = rows.Select((row, index) => (row, index)).Where(x => x.row.Changed && (x.index == 0 || !rows[x.index - 1].Changed)).Select(x => x.index).ToArray();
            changePosition = -1; count.Text = $"{changes.Length} 處差異";
            note.Text = document.Truncated ? "僅預覽前 20,000 行／2,000,000 字元；可複製完整原文。比對判定並未截斷。"
                : document.Simplified ? "大型差異區塊已簡化行配對；比對判定不受影響。F7／Shift+F7 導覽。"
                : "左：檢查 DB 目前內容　｜　右：標準 DB 基準　｜　紅色不代表刪除　｜　F7／Shift+F7 導覽";
        }
        finally { scrolling = false; }
        left.Invalidate(); right.Invalidate(); unified.Invalidate();
    }

    private static DataGridView CreateGrid(bool combined)
    {
        var grid = new BufferedGrid
        {
            Dock = DockStyle.Fill, VirtualMode = true, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
            RowHeadersVisible = false, BorderStyle = BorderStyle.None, BackgroundColor = Color.White, Font = new Font("Consolas", 10), AutoGenerateColumns = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None, SelectionMode = DataGridViewSelectionMode.CellSelect, MultiSelect = true,
            EnableHeadersVisualStyles = false, ColumnHeadersHeight = 34, RowTemplate = { Height = 24 }, GridColor = Color.FromArgb(235, 239, 244), ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText
        };
        grid.ColumnHeadersDefaultCellStyle = new() { BackColor = Ui.Background, ForeColor = Ui.Ink, Font = new Font("Microsoft JhengHei UI", 9, FontStyle.Bold), Padding = new Padding(4) };
        grid.DefaultCellStyle = new() { BackColor = Color.White, ForeColor = Ui.Ink, SelectionBackColor = Color.FromArgb(218, 231, 244), SelectionForeColor = Ui.Ink, WrapMode = DataGridViewTriState.False, Padding = new Padding(4, 0, 4, 0) };
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Line", HeaderText = combined ? "舊行" : "行", Width = 68, Frozen = true, SortMode = DataGridViewColumnSortMode.NotSortable });
        if (combined) grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "NewLine", HeaderText = "新行", Width = 68, Frozen = true, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Code", HeaderText = combined ? "檢查 DB → 標準 DB（顯示差異，非執行腳本）" : "定義", Width = 600, SortMode = DataGridViewColumnSortMode.NotSortable });
        return grid;
    }

    private void Bind(DataGridView grid, bool isRight, bool combined)
    {
        grid.CellValueNeeded += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= rows.Count) return;
            var row = rows[e.RowIndex];
            if (combined && e.ColumnIndex < 2) e.Value = e.ColumnIndex == 0 ? row.LeftNumber?.ToString() : row.RightNumber?.ToString();
            else if (!combined && e.ColumnIndex == 0)
            {
                var number = isRight ? row.RightNumber : row.LeftNumber;
                var marker = row.Changed && number.HasValue ? isRight ? "+ " : "− " : "";
                e.Value = number.HasValue ? marker + number : "";
            }
            else
            {
                var text = combined ? row.Kind == DiffKind.Removed ? row.Left : row.Right : isRight ? row.Right : row.Left;
                e.Value = text.Length > 8192 ? text[..8192] + " …（此行過長，請複製原文）" : text;
            }
        };
        grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= rows.Count) return;
            var row = rows[e.RowIndex];
            var green = combined ? row.Kind == DiffKind.Added : isRight && row.Changed && row.RightNumber.HasValue;
            var red = combined ? row.Kind == DiffKind.Removed : !isRight && row.Changed && row.LeftNumber.HasValue;
            var background = row.Kind == DiffKind.Fold ? Color.FromArgb(231, 239, 247) : green ? Color.FromArgb(224, 244, 229) : red ? Color.FromArgb(255, 233, 233) : Color.White;
            if (!combined && row.Kind != DiffKind.Fold && (isRight ? row.RightNumber : row.LeftNumber) is null) background = Ui.Background;
            e.CellStyle!.BackColor = background; e.CellStyle.SelectionBackColor = ControlPaint.Dark(background, 0.04f); e.CellStyle.SelectionForeColor = Ui.Ink;
            if (e.ColumnIndex < (combined ? 2 : 1)) e.CellStyle.ForeColor = Ui.Muted;
        };
        grid.CellPainting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= rows.Count || e.ColumnIndex != (combined ? 2 : 1)) return;
            var row = rows[e.RowIndex];
            var rightSide = combined ? row.Kind != DiffKind.Removed : isRight;
            var spans = rightSide ? row.RightHighlights : row.LeftHighlights;
            if (spans.Count == 0 || e.Graphics is null) return;
            e.Paint(e.ClipBounds, DataGridViewPaintParts.Background | DataGridViewPaintParts.Border | DataGridViewPaintParts.SelectionBackground);
            var text = Convert.ToString(e.FormattedValue) ?? ""; var font = e.CellStyle?.Font ?? grid.Font;
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.ExpandTabs;
            var bounds = new Rectangle(e.CellBounds.X + 4, e.CellBounds.Y + 3, e.CellBounds.Width - 8, e.CellBounds.Height - 5);
            var state = e.Graphics.Save();
            e.Graphics.SetClip(Rectangle.Intersect(e.ClipBounds, e.CellBounds), CombineMode.Intersect);
            using var brush = new SolidBrush(rightSide ? Color.FromArgb(165, 222, 176) : Color.FromArgb(248, 180, 180));
            foreach (var span in spans.Where(s => s.Start < text.Length))
            {
                var start = TextRenderer.MeasureText(text[..span.Start], font, Size.Empty, flags).Width;
                var end = TextRenderer.MeasureText(text[..Math.Min(text.Length, span.Start + span.Length)], font, Size.Empty, flags).Width;
                e.Graphics.FillRectangle(brush, bounds.X + start, bounds.Y, Math.Max(1, end - start), bounds.Height);
            }
            TextRenderer.DrawText(e.Graphics, text, font, bounds, Ui.Ink, flags);
            e.Graphics.Restore(state); e.Handled = true;
        };
    }

    private void Synchronize(DataGridView from, DataGridView to, ScrollEventArgs args)
    {
        if (scrolling || !sync.Checked || rows.Count == 0) return;
        scrolling = true;
        try
        {
            if (args.ScrollOrientation == ScrollOrientation.VerticalScroll && from.FirstDisplayedScrollingRowIndex >= 0)
                to.FirstDisplayedScrollingRowIndex = Math.Min(from.FirstDisplayedScrollingRowIndex, to.RowCount - 1);
            else if (args.ScrollOrientation == ScrollOrientation.HorizontalScroll) to.HorizontalScrollingOffset = from.HorizontalScrollingOffset;
        }
        finally { scrolling = false; }
    }

    private void Navigate(int direction)
    {
        if (changes.Length == 0) return;
        changePosition = (changePosition + direction + changes.Length) % changes.Length;
        var row = changes[changePosition];
        foreach (var grid in mode.SelectedIndex == 1 ? new[] { unified } : new[] { left, right })
        {
            grid.ClearSelection(); grid.CurrentCell = grid[grid.Columns.Count - 1, row]; grid.FirstDisplayedScrollingRowIndex = Math.Max(0, row - 2);
        }
        count.Text = $"{changePosition + 1}/{changes.Length} 處差異";
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { load?.Cancel(); load?.Dispose(); }
        base.Dispose(disposing);
    }
}
