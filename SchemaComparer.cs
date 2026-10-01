internal sealed record Difference(string Object, string Status, string? Source, string? Target);

internal static class SchemaComparer
{
    internal static List<Difference> Compare(IReadOnlyDictionary<string, string> source, IReadOnlyDictionary<string, string> target)
    {
        var differences = new List<Difference>();
        foreach (var key in source.Keys.Union(target.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var inSource = source.TryGetValue(key, out var left);
            var inTarget = target.TryGetValue(key, out var right);
            if (!inSource) differences.Add(new(key, "OnlyInTarget", null, right));
            else if (!inTarget) differences.Add(new(key, "OnlyInSource", left, null));
            else if (!string.Equals(left, right, StringComparison.Ordinal)) differences.Add(new(key, "Changed", left, right));
        }
        return differences;
    }
}
