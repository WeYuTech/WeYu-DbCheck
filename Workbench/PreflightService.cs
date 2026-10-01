using Microsoft.Data.SqlClient;

namespace DbCheck.Workbench;

internal sealed class PreflightService
{
    internal sealed record Check(string Name, string Sql, string Failure);

    public async Task<IReadOnlyList<PreflightFinding>> RunAsync(string connectionString, ComparisonSession session, IReadOnlyCollection<ObjectDifference> selected, CancellationToken token, IProgress<string>? progress = null)
    {
        var results = new List<PreflightFinding>();
        progress?.Report("重新讀取目標 metadata，檢查比對結果是否過期…");
        var fresh = await new CatalogService().ReadAsync(connectionString, session.Target.Options, token, progress);
        if (fresh.Label != session.Target.Label || fresh.Fingerprint != session.Target.Fingerprint)
            return [new(new("範圍", "目標已變更"), "metadata 版本", null, RiskLevel.Blocked, "目標連線或結構在比對後已變更；請重新比對，不可使用舊計畫。")];
        results.Add(new(new("範圍", "一致性"), "metadata 版本", 0, RiskLevel.Information, "此次 metadata 與比對基準相符；資料仍可能在預檢後改變，並非跨查詢一致性快照。"));
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        foreach (var difference in selected.Where(d => d.Category == ObjectCategory.Table && d.CanSelect))
        {
            token.ThrowIfCancellationRequested();
            var source = session.Source.Tables.Single(t => t.Key == difference.Key);
            var target = fresh.Tables.SingleOrDefault(t => t.Key == difference.Key);
            if (target is null) { results.Add(new(source.Key, "既有資料", 0, RiskLevel.Information, "目標表尚未存在；沒有既有資料可檢查。")); continue; }
            var checks = BuildChecks(source, target, fresh, session.Report.Options, results);
            if (checks.Count == 0) { results.Add(new(source.Key, "資料預檢", null, RiskLevel.Review, "沒有適用的自動資料檢查；未檢查項目不代表安全。")); continue; }
            progress?.Report($"{target.Key.Sql}：執行 {checks.Count} 個唯讀檢查，可能掃描資料表…");
            try
            {
                // Only generated SELECTs with quoted catalog identifiers and numeric type parameters.
                // Never execute a CHECK/filter/module expression supplied by an imported snapshot.
                using var command = new SqlCommand(string.Join("\r\n", checks.Select(c => c.Sql)), connection) { CommandTimeout = 60 };
                await using var reader = await command.ExecuteReaderAsync(token);
                for (var i = 0; i < checks.Count; i++)
                {
                    if (!await reader.ReadAsync(token) || reader.IsDBNull(0)) throw new InvalidOperationException("預檢沒有傳回完整計數。");
                    var count = reader.GetInt64(0);
                    results.Add(new(source.Key, checks[i].Name, count, count > 0 ? RiskLevel.Blocked : RiskLevel.Information, count > 0 ? checks[i].Failure + $" 影響 {count} 筆／組。" : "本次檢查未發現違反條件的資料。"));
                    if (i + 1 < checks.Count && !await reader.NextResultAsync(token)) throw new InvalidOperationException("預檢結果不完整。");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is SqlException or InvalidOperationException)
            { results.Add(new(source.Key, "預檢失敗", null, RiskLevel.Blocked, SqlText.Failure(ex))); }
        }
        results.Add(new(new("範圍", "人工確認"), "限制", null, RiskLevel.Review, "預檢不是部署保證；轉型後唯一性、CHECK／filtered index 表達式、資料分布、鎖定、磁碟空間、動態 SQL／外部相依及特殊型別仍須人工檢閱。"));
        return results;
    }

    internal static IReadOnlyList<Check> BuildChecks(TableModel source, TableModel target, CatalogSnapshot targetSnapshot, ComparisonOptions options, List<PreflightFinding> findings)
    {
        var lookup = target.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var checks = new List<Check>();
        foreach (var column in source.Columns)
        {
            if (!lookup.TryGetValue(column.Name, out var old))
            {
                if (!column.IsNullable && column.DefaultDefinition is null && !column.IsIdentity && column.ComputedDefinition is null)
                    checks.Add(new("新增 NOT NULL：" + column.Name, $"SELECT COUNT_BIG(*) FROM {target.Key.Sql};", "目標已有資料，需先確定回填值；不會自動代入 0 或空字串。"));
                continue;
            }
            var name = SqlText.Quote(column.Name);
            void Predicate(string label, string condition, string failure) => checks.Add(new(label + "：" + column.Name, $"SELECT COUNT_BIG(*) FROM {target.Key.Sql} WHERE {condition};", failure));
            if (!column.IsNullable && old.IsNullable) Predicate("NULL", name + " IS NULL", "存在 NULL，不能直接改成 NOT NULL。");
            if (column.SqlType == old.SqlType || column.TypeSchema != "sys" || old.TypeSchema != "sys") continue;
            if (column.SystemTypeId is 167 or 175 or 231 or 239 or 165 or 173 && column.MaxLengthBytes >= 0)
            {
                var value = column.SystemTypeId is 231 or 239 ? $"CONVERT(nvarchar(max),{name})" : name;
                Predicate("長度", $"DATALENGTH({value}) > {column.MaxLengthBytes}", "存在超過目標長度的值，可能截斷資料。");
            }
            if (old.SystemTypeId is not (34 or 35 or 99 or 98 or 240 or 241 or 189) && column.SystemTypeId is not (34 or 35 or 99 or 98 or 240 or 241 or 189))
                Predicate("轉型", $"{name} IS NOT NULL AND TRY_CONVERT({column.SqlType},{name}) IS NULL", "存在無法轉型的值。");
            else findings.Add(new(source.Key, "轉型：" + column.Name, null, RiskLevel.Review, "此型別未自動檢查轉型相容性。"));
            if (old.SystemTypeId is 106 or 108 or 41 or 42 or 43 && column.SystemTypeId == old.SystemTypeId && column.Scale < old.Scale)
                Predicate("精度損失", $"{name} IS NOT NULL AND TRY_CONVERT({old.SqlType},TRY_CONVERT({column.SqlType},{name})) <> {name}", "縮減小數／時間精度會改變既有值。");
            if (old.SystemTypeId is 231 or 239 && column.SystemTypeId is 167 or 175)
                findings.Add(new(source.Key, "字元編碼：" + column.Name, null, RiskLevel.Review, "Unicode 轉非 Unicode 的 code page 損失未完整驗證，請人工確認。"));
        }
        foreach (var index in source.Indexes.Where(i => i.IsUnique && i.Type is 1 or 2))
        {
            if (target.Indexes.Any(i => i.Signature == index.Signature)) continue;
            if (index.Filter is not null)
            {
                findings.Add(new(source.Key, "Filtered index：" + index.Name, null, RiskLevel.Review, "不執行來自來源／快照的任意 Filter 表達式，請另行人工預檢。"));
                continue;
            }
            var keys = index.Columns.Where(c => !c.Included).OrderBy(c => c.KeyOrdinal).ToArray();
            if (keys.Length == 0 || keys.Any(c => !lookup.ContainsKey(c.Name)))
            { findings.Add(new(source.Key, "唯一索引：" + index.Name, null, RiskLevel.Review, "索引涉及尚未建立的欄位，無法預檢。")); continue; }
            var columns = string.Join(", ", keys.Select(c => SqlText.Quote(c.Name)));
            checks.Add(new("重複鍵：" + index.Name, $"SELECT COUNT_BIG(*) FROM (SELECT {columns} FROM {target.Key.Sql} GROUP BY {columns} HAVING COUNT_BIG(*)>1) duplicate_keys;", "存在重複鍵，不能建立此唯一索引。"));
        }
        if (options.Scope.HasFlag(ComparisonScope.ForeignKeys))
            foreach (var foreignKey in source.ForeignKeys.Where(f => !f.IsNotTrusted && target.ForeignKeys.All(t => t.Signature != f.Signature)))
            {
                var parent = targetSnapshot.Tables.SingleOrDefault(t => t.Key == foreignKey.ReferencedTable);
                if (parent is null || foreignKey.Columns.Count == 0 || foreignKey.Columns.Any(c => !lookup.ContainsKey(c.Column) || !parent.Columns.Any(p => p.Name == c.ReferencedColumn)))
                { findings.Add(new(source.Key, "外鍵：" + foreignKey.Name, null, RiskLevel.Review, "參照表／欄位未納入或尚未建立，無法預檢。")); continue; }
                var notNull = string.Join(" AND ", foreignKey.Columns.Select(c => "child." + SqlText.Quote(c.Column) + " IS NOT NULL"));
                var match = string.Join(" AND ", foreignKey.Columns.Select(c => "parent." + SqlText.Quote(c.ReferencedColumn) + "=child." + SqlText.Quote(c.Column)));
                checks.Add(new("孤兒資料：" + foreignKey.Name, $"SELECT COUNT_BIG(*) FROM {target.Key.Sql} child WHERE {notNull} AND NOT EXISTS (SELECT 1 FROM {parent.Key.Sql} parent WHERE {match});", "存在違反外鍵的資料。"));
            }
        if (options.Scope.HasFlag(ComparisonScope.Checks))
            foreach (var check in source.Checks.Where(c => target.Checks.All(t => t.Signature != c.Signature)))
                findings.Add(new(source.Key, "CHECK：" + check.Name, null, RiskLevel.Review, "CHECK 定義已比對，但不在程式中執行任意來源／快照表達式；資料符合性需人工檢查。"));
        return checks;
    }
}
