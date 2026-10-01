using System.Text;
using System.Text.RegularExpressions;

namespace DbCheck.Workbench;

internal static class ScriptPlanner
{
    public static ChangePlan Build(ComparisonSession session, IReadOnlyCollection<ObjectDifference> selected, IReadOnlyList<PreflightFinding>? findings = null)
    {
        var steps = new List<PlanStep>();
        var source = session.Source; var target = session.Target; var options = session.Report.Options;
        var chosen = selected.DistinctBy(d => d.Id).ToArray();
        if (chosen.Length == 0) return new(steps, "-- 尚未選取待處理物件。");
        foreach (var item in chosen.Where(d => !d.CanSelect)) steps.Add(new(item.Key, "無法產生", RiskLevel.Blocked, "保留／不可讀取物件不允許產生修改 SQL。"));
        if (findings is not null)
            foreach (var item in findings) steps.Add(new(item.Object, "資料預檢：" + item.Check, item.Risk, item.Message));
        if (!session.Report.Complete) steps.Add(new(new("範圍", "部分完成"), "範圍提示", RiskLevel.Review, "整體比對並未全部成功；腳本僅涵蓋已選且可讀取的物件，未檢查範圍不能視為一致。"));
        if (!source.DependenciesRead || !target.DependenciesRead) steps.Add(new(new("範圍", "相依性"), "相依檢查", RiskLevel.Blocked, "相依資訊未完整取得，請重新比對後再產生 SQL。"));
        var selectedKeys = chosen.Select(d => d.Key).ToHashSet();
        var ordered = Order(chosen, source.Dependencies, steps);
        var deferredConstraints = new List<PlanStep>();
        foreach (var item in ordered)
        {
            if (!item.CanSelect) continue;
            if (!target.Schemas.Contains(item.Key.Schema, StringComparer.Ordinal))
            { steps.Add(new(item.Key, "缺少 schema", RiskLevel.Blocked, "目標 schema 不存在；建立 schema 與授權方式必須先人工確認。")); continue; }
            foreach (var dependency in source.Dependencies.Where(d => d.Owner == item.Key))
            {
                if (dependency.Server is not null || dependency.Database is not null || !dependency.Resolved || dependency.Referenced is null)
                    steps.Add(new(item.Key, "相依檢查", RiskLevel.Review, "跨資料庫、動態或無法解析的相依參照需人工檢查：" + dependency.ReferencedName));
                else if (Exists(source, dependency.Referenced) && !Exists(target, dependency.Referenced) && !selectedKeys.Contains(dependency.Referenced))
                    steps.Add(new(item.Key, "缺少相依物件", RiskLevel.Blocked, "請先選取或建立 " + dependency.Referenced.Sql));
                else if (!Exists(source, dependency.Referenced) && !Exists(target, dependency.Referenced))
                    steps.Add(new(item.Key, "範圍外相依物件", RiskLevel.Review, "相依物件未納入本次範圍或被排除，不能驗證其定義：" + dependency.Referenced.Sql));
            }
            try
            {
                if (item.Category == ObjectCategory.Table)
                {
                    var standard = source.Tables.Single(t => t.Key == item.Key);
                    var current = target.Tables.SingleOrDefault(t => t.Key == item.Key);
                    if (target.Modules.Any(m => m.Key == item.Key)) { steps.Add(new(item.Key, "物件類型衝突", RiskLevel.Blocked, "目標同名物件不是資料表，不能直接 CREATE。")); continue; }
                    BuildTable(standard, current, session, steps);
                    BuildConstraints(standard, current, session, deferredConstraints, selectedKeys);
                }
                else
                {
                    var standard = source.Modules.Single(m => m.Key == item.Key);
                    var current = target.Modules.SingleOrDefault(m => m.Key == item.Key);
                    if (!standard.Readable || (current is not null && !current.Readable) || (current is not null && current.TypeCode != standard.TypeCode) || target.Tables.Any(t => t.Key == item.Key))
                    { steps.Add(new(item.Key, "物件類型／定義", RiskLevel.Blocked, "定義不可讀取或物件類型不同，不能直接 ALTER。")); continue; }
                    if (standard.HasIndexes || current?.HasIndexes == true)
                    { steps.Add(new(item.Key, "索引化 View", RiskLevel.Blocked, "ALTER VIEW 可能移除索引；本版本未產生索引化 View 的索引重建腳本。")); continue; }
                    if (standard.Category == ObjectCategory.Trigger && standard.Parent is { } parent && !Exists(target, parent) && !selectedKeys.Contains(parent))
                    { steps.Add(new(item.Key, "Trigger 相依性", RiskLevel.Blocked, "目標缺少 Trigger 所屬物件 " + parent.Sql)); continue; }
                    var sql = ModuleText.Ddl(standard, current is not null);
                    steps.Add(new(item.Key, current is null ? "CREATE " + ModuleText.Kind(standard) : "ALTER " + ModuleText.Kind(standard), RiskLevel.Review, "保留標準定義及 SET 選項；動態 SQL 相依性仍需人工檢閱。", sql, standard.AnsiNulls, standard.QuotedIdentifier));
                    if (standard.Category == ObjectCategory.Trigger && standard.Parent is { } owner)
                        steps.Add(new(item.Key, "Trigger 狀態", RiskLevel.Review, "保留標準 DB 的啟用／停用狀態。", $"{(standard.IsDisabled ? "DISABLE" : "ENABLE")} TRIGGER {standard.Key.Sql} ON {owner.Sql};"));
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException or RegexMatchTimeoutException)
            { steps.Add(new(item.Key, "無法可靠產生", RiskLevel.Blocked, SqlText.Failure(ex))); }
        }
        steps.AddRange(deferredConstraints); // FK cycles do not force table creation into a cycle.
        if (steps.Any(s => s.Risk == RiskLevel.Blocked)) return new(steps, "-- 未產生可執行腳本：請處理變更計畫中的阻擋項目，或取消勾選相關物件後重新產生。\r\n");
        return new(steps, Wrap(target, steps, "結構變更草稿；未執行。未完成資料預檢時不代表資料相容。"));
    }

    private static bool Exists(CatalogSnapshot snapshot, ObjectKey key) => snapshot.Tables.Any(t => t.Key == key) || snapshot.Modules.Any(m => m.Key == key);

    private static List<ObjectDifference> Order(ObjectDifference[] selected, IReadOnlyList<DependencyModel> dependencies, List<PlanStep> steps)
    {
        var byKey = selected.ToDictionary(d => d.Key);
        var visited = new HashSet<ObjectKey>(); var visiting = new HashSet<ObjectKey>(); var result = new List<ObjectDifference>();
        void Visit(ObjectKey key)
        {
            if (visited.Contains(key)) return;
            if (!visiting.Add(key)) { steps.Add(new(key, "相依循環", RiskLevel.Blocked, "無法決定安全建立順序，請人工拆分部署階段。")); return; }
            foreach (var dependency in dependencies.Where(d => d.Owner == key && d.Server is null && d.Database is null && d.Referenced is not null && d.Referenced != key))
                if (byKey.ContainsKey(dependency.Referenced!)) Visit(dependency.Referenced!);
            visiting.Remove(key); visited.Add(key); result.Add(byKey[key]);
        }
        foreach (var item in selected.OrderBy(d => d.Key.Schema, StringComparer.Ordinal).ThenBy(d => d.Key.Name, StringComparer.Ordinal)) Visit(item.Key);
        return result;
    }

    private static void BuildTable(TableModel standard, TableModel? current, ComparisonSession session, List<PlanStep> steps)
    {
        void Block(string note) => steps.Add(new(standard.Key, "資料表變更", RiskLevel.Blocked, note));
        if (standard.TemporalType != 0 || standard.IsMemoryOptimized || current?.TemporalType != 0 && current is not null || current?.IsMemoryOptimized == true)
        { Block("Temporal／記憶體最佳化資料表需要專用部署流程，未產生一般 DDL。"); return; }
        if (standard.Columns.Any(c => c.IsHidden || c.GeneratedAlwaysType != 0 || c.IsColumnSet || c.EncryptionType != 0 || c.XmlCollectionId != 0))
        { Block("包含 hidden／generated／column-set／加密／typed XML 欄位，必須人工產生 DDL。"); return; }
        var options = session.Report.Options;
        var changes = new StringBuilder(); var before = new StringBuilder(); var after = new StringBuilder();
        var modifiedColumns = new HashSet<string>(StringComparer.Ordinal);
        var lookup = current?.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal) ?? new Dictionary<string, ColumnModel>(StringComparer.Ordinal);
        if (current is null)
            changes.AppendLine($"CREATE TABLE {standard.Key.Sql} (").AppendLine(string.Join(",\r\n", standard.Columns.OrderBy(c => c.Ordinal).Select(c => "    " + ColumnSql(c, true, options.IgnoreCollation ? null : c.Collation)))).AppendLine(");");
        else
        {
            var counter = 0;
            foreach (var column in standard.Columns)
            {
                if (!lookup.TryGetValue(column.Name, out var old))
                {
                    if (column.TypeSchema != "sys") Block("新增欄位使用 UDT，請先確認型別定義：" + column.SqlType);
                    if (!column.IsNullable && column.DefaultDefinition is null && !column.IsIdentity && column.ComputedDefinition is null)
                        steps.Add(new(standard.Key, "新增 NOT NULL 欄位", RiskLevel.Review, SqlText.Quote(column.Name) + "：若目標已有資料，必須先明確決定回填規則。"));
                    changes.AppendLine($"ALTER TABLE {standard.Key.Sql} ADD {ColumnSql(column, true, options.IgnoreCollation ? null : column.Collation)};");
                    continue;
                }
                if (column.IsIdentity != old.IsIdentity || column.IdentitySeed != old.IdentitySeed || column.IdentityIncrement != old.IdentityIncrement || column.ComputedDefinition != old.ComputedDefinition || column.IsPersisted != old.IsPersisted)
                { Block(SqlText.Quote(column.Name) + " 的 IDENTITY／計算欄位變更需人工重建。"); continue; }
                if (column.IsSparse != old.IsSparse || column.IsRowGuid != old.IsRowGuid || column.IsHidden != old.IsHidden || column.GeneratedAlwaysType != old.GeneratedAlwaysType || column.IsColumnSet != old.IsColumnSet || column.EncryptionType != old.EncryptionType || (column.XmlCollectionId != 0) != (old.XmlCollectionId != 0))
                { Block(SqlText.Quote(column.Name) + " 的特殊欄位屬性變更未自動處理。"); continue; }
                if (!options.IgnoreColumnOrder && column.Ordinal != old.Ordinal) Block("欄位順序不同，不能透過 ALTER COLUMN 調整；請人工確認是否真的需要重建。");
                var alter = column.SqlType != old.SqlType || column.IsNullable != old.IsNullable || !options.IgnoreCollation && column.Collation != old.Collation;
                if (alter && (column.IsIdentity || column.ComputedDefinition is not null || column.SystemTypeId == 189 || column.TypeSchema != "sys"))
                { Block(SqlText.Quote(column.Name) + " 為特殊欄位或 UDT，不自動修改型別。"); continue; }
                if (alter || column.DefaultDefinition != old.DefaultDefinition)
                {
                    if (old.DefaultDefinition is not null)
                    {
                        var variable = "@df" + counter++;
                        changes.AppendLine($"DECLARE {variable} sysname;")
                            .AppendLine($"SELECT {variable}=d.name FROM sys.default_constraints d JOIN sys.columns c ON c.object_id=d.parent_object_id AND c.column_id=d.parent_column_id WHERE d.parent_object_id=OBJECT_ID({SqlText.String(standard.Key.Sql)}) AND c.name={SqlText.String(column.Name)};")
                            .AppendLine($"IF {variable} IS NOT NULL EXEC({SqlText.String("ALTER TABLE " + standard.Key.Sql + " DROP CONSTRAINT ")}+QUOTENAME({variable}));");
                    }
                    if (alter)
                    {
                        modifiedColumns.Add(column.Name);
                        changes.AppendLine($"ALTER TABLE {standard.Key.Sql} ALTER COLUMN {ColumnSql(column, false, options.IgnoreCollation ? old.Collation : column.Collation)};");
                        steps.Add(new(standard.Key, "ALTER COLUMN " + SqlText.Quote(column.Name), RiskLevel.Review, old.SqlType + " → " + column.SqlType + "；請檢查 NULL、長度、轉型、精度及相依物件。"));
                    }
                    if (column.DefaultDefinition is not null) changes.AppendLine($"ALTER TABLE {standard.Key.Sql} ADD DEFAULT {column.DefaultDefinition} FOR {SqlText.Quote(column.Name)};");
                }
            }
        }
        if (modifiedColumns.Count > 0 && current is not null)
        {
            if (!session.Target.CompletedScopes.HasFlag(ComparisonScope.ForeignKeys) || !session.Target.CompletedScopes.HasFlag(ComparisonScope.Checks)) Block("修改既有欄位前必須納入外鍵與 CHECK 範圍，避免遺漏阻擋相依物件。");
            if (current.Checks.Count > 0) Block("現有 CHECK 的欄位相依性需要人工確認；本版本不自動拆除重建 CHECK 來配合型別變更。");
            if (current.ForeignKeys.Any(f => f.Columns.Any(c => modifiedColumns.Contains(c.Column))) || session.Target.Tables.Any(t => t.ForeignKeys.Any(f => f.ReferencedTable == standard.Key && f.Columns.Any(c => modifiedColumns.Contains(c.ReferencedColumn))))) Block("修改欄位涉及外鍵，需先規劃相依約束重建順序。");
            if (session.Target.Dependencies.Any(d => d.Referenced == standard.Key && d.SchemaBound && d.Owner != standard.Key)) Block("資料表存在 schema-bound 相依物件；未自動 DROP 相依物件。");
        }
        var unmatched = current?.Indexes.ToList() ?? [];
        foreach (var index in standard.Indexes)
        {
            var match = options.IgnoreIndexNames ? unmatched.FindIndex(i => i.Signature == index.Signature) : -1;
            if (match < 0) match = unmatched.FindIndex(i => i.Name == index.Name);
            if (match < 0 && index.IsPrimaryKey) match = unmatched.FindIndex(i => i.IsPrimaryKey);
            var old = match < 0 ? null : unmatched[match]; if (match >= 0) unmatched.RemoveAt(match);
            var depends = index.Columns.Any(c => modifiedColumns.Contains(c.Name)) || old?.Columns.Any(c => modifiedColumns.Contains(c.Name)) == true;
            if (old is not null && old.Signature == index.Signature && !depends && (options.IgnoreIndexNames || old.Name == index.Name)) continue;
            if (index.Type is not (1 or 2) || old is not null && old.Type is not (1 or 2)) { Block("特殊索引 " + SqlText.Quote(index.Name) + " 未產生 DDL。"); continue; }
            if (old is null && index.Type == 1 && current?.Indexes.Any(i => i.Type == 1) == true) { Block("目標已有不同的 clustered index，不能直接新增第二個。"); continue; }
            if (old is not null)
            {
                if ((old.IsPrimaryKey || old.IsUniqueConstraint) && (!session.Target.CompletedScopes.HasFlag(ComparisonScope.ForeignKeys) || session.Target.Tables.Any(t => t.ForeignKeys.Any(f => f.ReferencedTable == standard.Key)))) Block("主鍵／唯一約束可能被外鍵參照，未自動拆除外鍵。");
                before.AppendLine(old.IsPrimaryKey || old.IsUniqueConstraint ? $"ALTER TABLE {standard.Key.Sql} DROP CONSTRAINT {SqlText.Quote(old.Name)};" : $"DROP INDEX {SqlText.Quote(old.Name)} ON {standard.Key.Sql};");
            }
            after.AppendLine(IndexSql(standard.Key, index));
            steps.Add(new(standard.Key, (old is null ? "新增索引 " : "重建索引 ") + index.Name, RiskLevel.Review, "需檢查重複鍵、鎖定及 IO；額外儲存／壓縮選項未納入。"));
        }
        if (unmatched.Any(i => i.Columns.Any(c => modifiedColumns.Contains(c.Name)))) Block("目標額外索引依賴修改欄位；為保留它的設定，請人工安排重建。");
        var sql = before.ToString() + changes + after;
        if (sql.Length > 0) steps.Add(new(standard.Key, current is null ? "CREATE TABLE" : "ALTER TABLE／INDEX", RiskLevel.Review, "保留目標獨有欄位與索引；不自動回填資料。", sql));
    }

    private static string ColumnSql(ColumnModel column, bool create, string? collation)
    {
        if (column.ComputedDefinition is not null) return SqlText.Quote(column.Name) + " AS " + column.ComputedDefinition + (column.IsPersisted ? " PERSISTED" : "");
        var sql = SqlText.Quote(column.Name) + " " + column.SqlType;
        if (collation is not null && column.SystemTypeId is 35 or 99 or 167 or 175 or 231 or 239) sql += " COLLATE " + SqlText.Quote(collation);
        if (create && column.IsIdentity)
        {
            if (!Regex.IsMatch(column.IdentitySeed ?? "", @"^-?\d+(\.\d+)?$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)) || !Regex.IsMatch(column.IdentityIncrement ?? "", @"^-?\d+(\.\d+)?$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) throw new InvalidOperationException("IDENTITY seed／increment 格式不合法。");
            sql += $" IDENTITY({column.IdentitySeed},{column.IdentityIncrement})";
        }
        if (create && column.IsRowGuid) sql += " ROWGUIDCOL";
        if (create && column.IsSparse) sql += " SPARSE";
        sql += column.IsNullable ? " NULL" : " NOT NULL";
        if (create && column.DefaultDefinition is not null) sql += " DEFAULT " + column.DefaultDefinition;
        return sql;
    }

    private static string IndexSql(ObjectKey table, IndexModel index)
    {
        var keys = string.Join(", ", index.Columns.Where(c => !c.Included).OrderBy(c => c.KeyOrdinal).Select(c => SqlText.Quote(c.Name) + (c.Descending ? " DESC" : " ASC")));
        if (keys.Length == 0) throw new NotSupportedException("索引缺少可重建的鍵欄位。");
        var clustered = index.Type == 1 ? "CLUSTERED" : "NONCLUSTERED";
        var sql = index.IsPrimaryKey || index.IsUniqueConstraint
            ? $"ALTER TABLE {table.Sql} ADD CONSTRAINT {SqlText.Quote(index.Name)} {(index.IsPrimaryKey ? "PRIMARY KEY" : "UNIQUE")} {clustered} ({keys});"
            : $"CREATE {(index.IsUnique ? "UNIQUE " : "")}{clustered} INDEX {SqlText.Quote(index.Name)} ON {table.Sql} ({keys})" + (index.Columns.Any(c => c.Included) ? " INCLUDE (" + string.Join(", ", index.Columns.Where(c => c.Included).Select(c => SqlText.Quote(c.Name))) + ")" : "") + (index.Filter is null ? "" : " WHERE " + index.Filter) + ";";
        return sql + (index.IsDisabled ? $"\r\nALTER INDEX {SqlText.Quote(index.Name)} ON {table.Sql} DISABLE;" : "");
    }

    private static void BuildConstraints(TableModel source, TableModel? target, ComparisonSession session, List<PlanStep> steps, HashSet<ObjectKey> selected)
    {
        if (session.Report.Options.Scope.HasFlag(ComparisonScope.ForeignKeys))
        {
            if (!session.Source.CompletedScopes.HasFlag(ComparisonScope.ForeignKeys) || !session.Target.CompletedScopes.HasFlag(ComparisonScope.ForeignKeys)) steps.Add(new(source.Key, "外鍵", RiskLevel.Blocked, "外鍵資訊未完整取得。"));
            else foreach (var item in source.ForeignKeys)
            {
                if (target?.ForeignKeys.Any(f => f.Signature == item.Signature && (session.Report.Options.IgnoreIndexNames || f.Name == item.Name)) == true) continue;
                if (!Exists(session.Target, item.ReferencedTable) && !selected.Contains(item.ReferencedTable)) { steps.Add(new(source.Key, "外鍵相依性", RiskLevel.Blocked, "參照資料表不存在或未納入範圍：" + item.ReferencedTable.Sql)); continue; }
                var old = target?.ForeignKeys.SingleOrDefault(f => f.Name == item.Name);
                var sql = old is null ? "" : $"ALTER TABLE {source.Key.Sql} DROP CONSTRAINT {SqlText.Quote(old.Name)};\r\n";
                string Action(string action) => action switch { "NO_ACTION" => "NO ACTION", "CASCADE" => "CASCADE", "SET_NULL" => "SET NULL", "SET_DEFAULT" => "SET DEFAULT", _ => throw new NotSupportedException("無法辨识的外鍵動作。") };
                sql += $"ALTER TABLE {source.Key.Sql} WITH {(item.IsNotTrusted ? "NOCHECK" : "CHECK")} ADD CONSTRAINT {SqlText.Quote(item.Name)} FOREIGN KEY ({string.Join(", ", item.Columns.OrderBy(c => c.Ordinal).Select(c => SqlText.Quote(c.Column)))}) REFERENCES {item.ReferencedTable.Sql} ({string.Join(", ", item.Columns.OrderBy(c => c.Ordinal).Select(c => SqlText.Quote(c.ReferencedColumn)))}) ON DELETE {Action(item.DeleteAction)} ON UPDATE {Action(item.UpdateAction)}{(item.NotForReplication ? " NOT FOR REPLICATION" : "")};\r\n";
                sql += $"ALTER TABLE {source.Key.Sql} {(item.IsDisabled ? "NOCHECK" : "CHECK")} CONSTRAINT {SqlText.Quote(item.Name)};";
                steps.Add(new(source.Key, "外鍵 " + item.Name, RiskLevel.Review, "保留標準的 enabled／trusted 狀態；新增約束可能因既有資料不符合而失敗。", sql));
            }
        }
        if (session.Report.Options.Scope.HasFlag(ComparisonScope.Checks))
        {
            if (!session.Source.CompletedScopes.HasFlag(ComparisonScope.Checks) || !session.Target.CompletedScopes.HasFlag(ComparisonScope.Checks)) steps.Add(new(source.Key, "CHECK", RiskLevel.Blocked, "CHECK 資訊未完整取得。"));
            else foreach (var item in source.Checks)
            {
                if (target?.Checks.Any(c => c.Signature == item.Signature && (session.Report.Options.IgnoreIndexNames || c.Name == item.Name)) == true) continue;
                var old = target?.Checks.SingleOrDefault(c => c.Name == item.Name);
                var sql = old is null ? "" : $"ALTER TABLE {source.Key.Sql} DROP CONSTRAINT {SqlText.Quote(old.Name)};\r\n";
                sql += $"ALTER TABLE {source.Key.Sql} WITH {(item.IsNotTrusted ? "NOCHECK" : "CHECK")} ADD CONSTRAINT {SqlText.Quote(item.Name)} CHECK {(item.NotForReplication ? "NOT FOR REPLICATION " : "")}({item.Definition});\r\nALTER TABLE {source.Key.Sql} {(item.IsDisabled ? "NOCHECK" : "CHECK")} CONSTRAINT {SqlText.Quote(item.Name)};";
                steps.Add(new(source.Key, "CHECK " + item.Name, RiskLevel.Review, "保留標準的 enabled／trusted 狀態；需確認現有資料。", sql));
            }
        }
    }

    public static string Wrap(CatalogSnapshot target, IEnumerable<PlanStep> steps, string note)
    {
        var sql = new StringBuilder().AppendLine("-- " + SqlText.Comment(note)).AppendLine("-- 目標：" + SqlText.Comment(target.Label)).AppendLine("-- 比對時間：" + target.CapturedAt.ToString("O"))
            .AppendLine("-- 不含 GO：避免第一批檢查失敗後，後續批次仍被工具繼續執行。")
            .AppendLine("SET XACT_ABORT ON;").AppendLine("IF @@TRANCOUNT <> 0 THROW 51000, N'請在沒有既有交易的獨立連線執行。', 1;")
            .AppendLine("BEGIN TRY")
            .AppendLine($"    IF DB_NAME() <> {SqlText.String(target.Database)} OR CONVERT(nvarchar(128),SERVERPROPERTY('ServerName')) <> {SqlText.String(target.Server)} THROW 51001, N'目標伺服器或資料庫不符，已停止。', 1;")
            .AppendLine("    BEGIN TRANSACTION;");
        foreach (var step in steps.Where(s => !string.IsNullOrWhiteSpace(s.Sql)))
        {
            sql.AppendLine("    -- " + SqlText.Comment(step.Object.Sql + " / " + step.Action))
                .AppendLine($"    SET ANSI_NULLS {(step.AnsiNulls ? "ON" : "OFF")};")
                .AppendLine($"    SET QUOTED_IDENTIFIER {(step.QuotedIdentifier ? "ON" : "OFF")};")
                .AppendLine("    EXEC sys.sp_executesql " + SqlText.String(step.Sql!) + ";");
        }
        return sql.AppendLine("    COMMIT TRANSACTION;").AppendLine("END TRY").AppendLine("BEGIN CATCH").AppendLine("    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;").AppendLine("    THROW;").AppendLine("END CATCH;").ToString();
    }
}
