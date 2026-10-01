using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class DataTableSchemaReport
{
    internal sealed record TableDifference(string Schema, string Name)
    {
        public override string ToString() => $"[{Schema.Replace("]", "]]")}].[{Name.Replace("]", "]]")}]";
    }
    internal sealed record Report(string Text, int Count, IReadOnlyList<TableDifference> Tables,
        IReadOnlyDictionary<TableDifference, string> TableTexts);

    internal static string IndexSignature(JsonElement index) => JsonSerializer.Serialize(
        index.EnumerateObject().Where(p => p.Name != "name").OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToDictionary(p => p.Name, p => p.Value));

    private static bool Same(DataRow a, DataRow b, string property)
    {
        if (property == "IndexDefinitions")
        {
            string Normalize(object value)
            {
                using var doc = JsonDocument.Parse(value is DBNull || string.IsNullOrWhiteSpace(value.ToString()) ? "[]" : value.ToString()!);
                return string.Join("\n", doc.RootElement.EnumerateArray().Select(IndexSignature).Order(StringComparer.Ordinal));
            }
            return Normalize(a[property]) == Normalize(b[property]);
        }
        if (property == "IndexInfo")
        {
            string Normalize(object value) => Regex.Replace(value.ToString() ?? "", @"(^|; )\[(?:[^\]]|\]\])*\](?= \[)", "$1");
            return Normalize(a[property]) == Normalize(b[property]);
        }
        return Equals(a[property], b[property]);
    }
    internal static Report Create(DataTable standard, DataTable actual,
        CancellationToken token = default, IProgress<string>? progress = null)
    {
        var keys = new[] { "SchemaName", "TableName", "ColumnName" };
        foreach (DataColumn column in standard.Columns)
            if (!actual.Columns.Contains(column.ColumnName))
                throw new ArgumentException("目標結構 DataTable 缺少欄位：" + column.ColumnName);
        foreach (var key in keys)
            if (!standard.Columns.Contains(key)) throw new ArgumentException("缺少結構鍵：" + key);
        (string, string, string) Key(DataRow row) =>
            ((string)row["SchemaName"], (string)row["TableName"], (string)row["ColumnName"]);
        var lookup = actual.AsEnumerable().ToDictionary(Key);
        var tables = actual.AsEnumerable().Select(r => ((string)r["SchemaName"], (string)r["TableName"])).ToHashSet();
        var properties = standard.Columns.Cast<DataColumn>().Select(c => c.ColumnName).Except(keys)
            .Where(name => !string.Equals(name, "CollationName", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(name, "ColumnOrdinal", StringComparison.OrdinalIgnoreCase)
                && !(name == "IndexInfo" && standard.Columns.Contains("IndexDefinitions"))).ToArray();
        var report = new StringBuilder().AppendLine($"標準：{standard.TableName} → 目標：{actual.TableName}")
            .AppendLine("僅列出檢查 DB 相對標準 DB 缺少或設定不同的欄位；檢查 DB 獨有項目不列入。").AppendLine();
        var count = 0; var processed = 0;
        var differentTables = new HashSet<TableDifference>();
        var tableTexts = new Dictionary<TableDifference, StringBuilder>();
        foreach (var row in standard.AsEnumerable().OrderBy(r => (string)r["TableName"], StringComparer.Ordinal)
            .ThenBy(r => (string)r["ColumnName"], StringComparer.Ordinal).ThenBy(r => (string)r["SchemaName"], StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var key = Key(row);
            var exists = lookup.TryGetValue(key, out var target);
            var changed = exists ? properties.Where(p => !Same(row, target!, p)).ToArray() : properties;
            if (!exists || changed.Length > 0)
            {
                count++;
                var tableKey = new TableDifference(key.Item1, key.Item2);
                differentTables.Add(tableKey);
                var start = report.Length;
                string Q(string value) => "[" + value.Replace("]", "]]") + "]";
                report.AppendLine($"TABLE {Q(key.Item1)}.{Q(key.Item2)} / 欄位 {Q(key.Item3)}");
                report.AppendLine(!tables.Contains((key.Item1, key.Item2)) ? "狀態：目標缺少資料表" : !exists ? "狀態：目標缺少欄位" : "狀態：欄位設定不同");
                // Always show type, length and index context alongside changed properties.
                foreach (var property in new[] { "DataType", "Length", "IsNullable", "IndexInfo" }.Concat(changed).Distinct())
                {
                    if (!standard.Columns.Contains(property)) continue;
                    report.AppendLine($"  {property}{(changed.Contains(property) ? " *" : "")}：標準={Display(row[property])}；目標={(exists ? Display(target![property]) : "（不存在）")}");
                }
                report.AppendLine();
                if (!tableTexts.TryGetValue(tableKey, out var detail)) tableTexts[tableKey] = detail = new StringBuilder();
                detail.Append(report.ToString(start, report.Length - start));
            }
            if (++processed % 100 == 0) progress?.Report($"{standard.TableName} → {actual.TableName}：{processed}/{standard.Rows.Count} 欄位");
        }
        report.Insert(0, $"差異 TABLE 數量：{differentTables.Count}\r\n");
        report.AppendLine(count == 0 ? "無差異" : $"合計 {differentTables.Count} 個 TABLE、{count} 個欄位有差異（* 為不同屬性）。");
        return new(report.ToString(), count, differentTables.OrderBy(t => t.Name, StringComparer.Ordinal).ThenBy(t => t.Schema, StringComparer.Ordinal).ToArray(),
            tableTexts.ToDictionary(p => p.Key, p => $"標準：{standard.TableName} → 檢查：{actual.TableName}\r\n{p.Value}"));
    }

    private static string Display(object value) => value is DBNull ? "（無）"
        : value is bool flag ? (flag ? "是" : "否")
        : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
}
