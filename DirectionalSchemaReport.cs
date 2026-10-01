using System.Text;
using System.Text.Json;

internal static class DirectionalSchemaReport
{
    internal static (string Text, int Count) Create(IReadOnlyDictionary<string, string> standard,
        IReadOnlyDictionary<string, string> actual, string standardName, string actualName,
        CancellationToken token, IProgress<string>? progress = null)
    {
        var text = new StringBuilder().AppendLine($"標準：{standardName} → 檢查：{actualName}")
            .AppendLine("列出目標缺少或設定不同的項目；目標獨有項目於反向比對列出。").AppendLine();
        var count = 0; var processed = 0;
        foreach (var item in standard.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var exists = actual.TryGetValue(item.Key, out var value);
            if (!exists || !string.Equals(item.Value, value, StringComparison.Ordinal))
            {
                count++;
                var separator = item.Key.IndexOf(':');
                var kind = item.Key[..separator];
                var names = JsonSerializer.Deserialize<string[]>(item.Key[(separator + 1)..])!;
                string Q(string name) => "[" + name.Replace("]", "]]") + "]";
                text.AppendLine($"TABLE {Q(names[0])}.{Q(names[1])}");
                text.AppendLine($"{kind}{(names.Length > 2 ? " " + Q(names[2]) : "")}：{(exists ? "設定不同" : "缺少")}");
                text.AppendLine($"  標準（{standardName}）：").AppendLine(Describe(kind, item.Value));
                text.AppendLine($"  實際（{actualName}）：").AppendLine(exists ? Describe(kind, value!) : "    （不存在）");
                text.AppendLine();
            }
            if (++processed % 100 == 0) progress?.Report($"{standardName} → {actualName}：已檢查 {processed}/{standard.Count} 項");
        }
        text.AppendLine(count == 0 ? "無差異" : $"合計 {count} 項差異");
        return (text.ToString(), count);
    }

    private static string Describe(string kind, string json)
    {
        using var document = JsonDocument.Parse(json);
        var fields = document.RootElement.EnumerateObject().OrderBy(p => int.Parse(p.Name.Split(':')[0])).ToArray();
        string At(int index) => fields.FirstOrDefault(p => p.Name.StartsWith(index + ":", StringComparison.Ordinal)).Value.ToString();
        var text = new StringBuilder();
        if (kind == "Column")
        {
            var type = At(5);
            var length = At(6);
            var displayLength = length == "-1" ? "MAX" :
                (type is "nvarchar" or "nchar") && int.TryParse(length, out var bytes) ? $"{bytes / 2}（UTF-16 單位；{bytes} bytes）" : length + " bytes";
            text.AppendLine($"    類型={At(4)}.{type}；長度={displayLength}；精度={At(7)}；小數位={At(8)}");
        }
        string[] labels = kind switch
        {
            "Table" => ["Schema", "Table", "Temporal 類型", "記憶體最佳化"],
            "Column" => ["Schema", "Table", "欄位", "欄位順序", "型別 Schema", "型別", "長度(bytes，-1=MAX)", "精度", "小數位", "允許 NULL", "定序", "IDENTITY", "起始值", "增量", "計算式", "Persisted", "預設值", "ROWGUID", "Sparse", "GeneratedAlways"],
            "Index" => ["Schema", "Table", "索引", "索引類型", "Unique", "主鍵", "唯一約束", "篩選條件", "停用", "索引欄位（順序／降冪／Include）"],
            "ForeignKey" => ["Schema", "Table", "外鍵", "參照 Schema", "參照 Table", "刪除行為", "更新行為", "停用", "未信任", "NotForReplication", "外鍵欄位對應"],
            "Check" => ["Schema", "Table", "CHECK", "條件", "停用", "未信任", "NotForReplication"],
            _ => []
        };
        foreach (var field in fields)
        {
            var index = int.Parse(field.Name.Split(':')[0]);
            var label = index < labels.Length ? labels[index] : field.Name;
            text.AppendLine($"    {label}：{(field.Value.ValueKind == JsonValueKind.Null ? "（無）" : field.Value.ToString())}");
        }
        return text.ToString().TrimEnd();
    }
}
