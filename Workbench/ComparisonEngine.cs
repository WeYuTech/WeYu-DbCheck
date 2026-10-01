using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DbCheck.Workbench;

internal static class ComparisonEngine
{
    public static ComparisonReport Compare(CatalogSnapshot standard, CatalogSnapshot current, ComparisonOptions options, CancellationToken token = default)
    {
        options.Validate();
        var differences = new List<ObjectDifference>();
        var issues = new List<string>();
        CheckSnapshot(standard, "標準 DB", options, issues);
        CheckSnapshot(current, "檢查 DB", options, issues);
        issues.AddRange(standard.Issues.Select(i => $"標準 DB／{i.Scope}／{i.Object}：{i.Message}"));
        issues.AddRange(current.Issues.Select(i => $"檢查 DB／{i.Scope}／{i.Object}：{i.Message}"));

        bool Ready(ComparisonScope scope)
        {
            if (!options.Scope.HasFlag(scope)) return false;
            if (standard.CompletedScopes.HasFlag(scope) && current.CompletedScopes.HasFlag(scope)) return true;
            differences.Add(new ObjectDifference { Key = new("範圍", scope.ToString()), Category = ObjectCategory.Scope, State = DifferenceState.Unverifiable, Message = "至少一側未完整讀取此範圍，不能推論物件不存在或一致。" });
            return false;
        }

        // A snapshot made with a different inventory filter must not be treated as complete inventory.
        if (issues.Any(i => i.StartsWith("快照設定不相容", StringComparison.Ordinal)))
            return Report();

        if (Ready(ComparisonScope.Tables))
        {
            var sourceTables = standard.Tables.ToDictionary(t => t.Key);
            var targetTables = current.Tables.ToDictionary(t => t.Key);
            var foreignKeysReady = !options.Scope.HasFlag(ComparisonScope.ForeignKeys) || Ready(ComparisonScope.ForeignKeys);
            var checksReady = !options.Scope.HasFlag(ComparisonScope.Checks) || Ready(ComparisonScope.Checks);
            foreach (var source in sourceTables.Values)
            {
                token.ThrowIfCancellationRequested();
                targetTables.TryGetValue(source.Key, out var target);
                var changes = new List<PropertyDifference>();
                if (target is null) changes.Add(new("資料表", "存在", null, source.Key.Sql));
                else
                {
                    Add(changes, "資料表", "TemporalType", target.TemporalType, source.TemporalType);
                    Add(changes, "資料表", "MemoryOptimized", target.IsMemoryOptimized, source.IsMemoryOptimized);
                    CompareColumns(source, target, options, changes);
                    CompareCollections(source.Indexes, target.Indexes, i => i.Name, i => i.Signature, i => i.Display, "索引", options.IgnoreIndexNames, changes);
                    if (options.Scope.HasFlag(ComparisonScope.ForeignKeys) && foreignKeysReady)
                        CompareCollections(source.ForeignKeys, target.ForeignKeys, i => i.Name, i => i.Signature, i => i.Display, "外鍵", options.IgnoreIndexNames, changes);
                    if (options.Scope.HasFlag(ComparisonScope.Checks) && checksReady)
                        CompareCollections(source.Checks, target.Checks, i => i.Name, i => i.Signature, i => i.Display, "CHECK", options.IgnoreIndexNames, changes);
                }
                if (changes.Count == 0) continue;
                differences.Add(new ObjectDifference
                {
                    Key = source.Key, Category = ObjectCategory.Table,
                    State = target is null ? DifferenceState.Missing : changes.All(c => c.Retained) ? DifferenceState.Retained : DifferenceState.Changed,
                    Properties = changes, CurrentText = target is null ? "" : DescribeTable(target, options), StandardText = DescribeTable(source, options),
                    Message = "此為結構顯示，不是部署 SQL。紅色不代表會刪除；目標額外欄位、索引及約束保留。"
                });
            }
            foreach (var target in targetTables.Values.Where(t => !sourceTables.ContainsKey(t.Key)))
                differences.Add(new ObjectDifference { Key = target.Key, Category = ObjectCategory.Table, State = DifferenceState.Retained, CurrentText = DescribeTable(target, options), Message = "檢查 DB 獨有資料表：保留，不產生 DROP。" });
        }
        foreach (var scope in new[] { ComparisonScope.Views, ComparisonScope.Routines, ComparisonScope.Triggers })
        {
            if (!Ready(scope)) continue;
            var sources = standard.Modules.Where(m => ScopeOf(m.Category) == scope).ToDictionary(m => m.Key);
            var targets = current.Modules.Where(m => ScopeOf(m.Category) == scope).ToDictionary(m => m.Key);
            foreach (var source in sources.Values)
            {
                token.ThrowIfCancellationRequested();
                targets.TryGetValue(source.Key, out var target);
                if (!source.Readable || (target is not null && !target.Readable))
                {
                    differences.Add(new ObjectDifference { Key = source.Key, Category = source.Category, State = DifferenceState.Unverifiable, CurrentText = target?.Definition ?? "（不可讀取）", StandardText = source.Definition ?? "（不可讀取）", Message = "至少一側為 CLR、加密或不可見定義；不能判斷一致，也不能產生修改腳本。" });
                    continue;
                }
                var changes = new List<PropertyDifference>();
                if (target is null) changes.Add(new("物件", "存在", null, source.Key.Sql));
                else
                {
                    Add(changes, "設定", "物件類型", target.TypeCode, source.TypeCode);
                    Add(changes, "設定", "ANSI_NULLS", target.AnsiNulls, source.AnsiNulls);
                    Add(changes, "設定", "QUOTED_IDENTIFIER", target.QuotedIdentifier, source.QuotedIdentifier);
                    Add(changes, "設定", "SCHEMABINDING", target.IsSchemaBound, source.IsSchemaBound);
                    Add(changes, "設定", "Disabled", target.IsDisabled, source.IsDisabled);
                    Add(changes, "定義", "SQL", ModuleText.Comparable(target), ModuleText.Comparable(source));
                }
                if (changes.Count == 0) continue;
                differences.Add(new ObjectDifference { Key = source.Key, Category = source.Category, State = target is null ? DifferenceState.Missing : DifferenceState.Changed, Properties = changes, CurrentText = target is null ? "" : ModuleText.Display(target), StandardText = ModuleText.Display(source), Message = "文字比對，不推論 SQL 語意等價；CREATE／ALTER 標頭差異不列入。" });
            }
            foreach (var target in targets.Values.Where(t => !sources.ContainsKey(t.Key)))
                differences.Add(new ObjectDifference { Key = target.Key, Category = target.Category, State = target.Readable ? DifferenceState.Retained : DifferenceState.Unverifiable, CurrentText = target.Definition ?? "（不可讀取）", Message = target.Readable ? "檢查 DB 獨有物件：保留。" : "目標獨有物件定義不可讀取：保留，但不能完成定義檢查。" });
        }
        return Report();

        ComparisonReport Report() => new()
        {
            Source = standard.Label, Target = current.Label, SourceCapturedAt = standard.CapturedAt, TargetCapturedAt = current.CapturedAt, Options = options,
            Differences = differences.OrderBy(d => d.Category).ThenBy(d => d.Key.Schema, StringComparer.Ordinal).ThenBy(d => d.Key.Name, StringComparer.Ordinal).ToList(), Issues = issues.Distinct(StringComparer.Ordinal).ToList(),
            ConsistencyNote = "各查詢與兩個資料庫不是同一個一致性快照。只比對已選範圍；資料內容、權限、UDT／XML schema collection 定義、資料庫 DDL Trigger、儲存／分割／壓縮設定未納入。索引的額外儲存選項未納入。"
        };
    }

    private static void CheckSnapshot(CatalogSnapshot snapshot, string side, ComparisonOptions options, List<string> issues)
    {
        if (snapshot.FormatVersion != 1) throw new NotSupportedException("不支援此快照版本。");
        if ((snapshot.Options.Scope & options.Scope) != options.Scope || snapshot.Options.ExcludeZZ != options.ExcludeZZ
            || !snapshot.Options.ExcludedPrefixes.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(options.ExcludedPrefixes.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
            issues.Add($"快照設定不相容：{side} 的採集範圍或排除規則不同；請重新採集或使用快照原設定。");
    }

    public static ComparisonScope ScopeOf(ObjectCategory category) => category switch
    {
        ObjectCategory.Table => ComparisonScope.Tables, ObjectCategory.View => ComparisonScope.Views,
        ObjectCategory.Function or ObjectCategory.Procedure => ComparisonScope.Routines,
        ObjectCategory.Trigger => ComparisonScope.Triggers, _ => ComparisonScope.None
    };

    private static void CompareColumns(TableModel source, TableModel target, ComparisonOptions options, List<PropertyDifference> changes)
    {
        var lookup = target.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var sourceNames = source.Columns.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var column in source.Columns)
        {
            var subject = "欄位 " + SqlText.Quote(column.Name);
            if (!lookup.TryGetValue(column.Name, out var current)) { changes.Add(new(subject, "存在", null, DescribeColumn(column, options))); continue; }
            Add(changes, subject, "型別", current.SqlType, column.SqlType);
            Add(changes, subject, "允許 NULL", current.IsNullable, column.IsNullable);
            Add(changes, subject, "IDENTITY", Identity(current), Identity(column));
            Add(changes, subject, "預設值", current.DefaultDefinition, column.DefaultDefinition);
            Add(changes, subject, "計算式", current.ComputedDefinition, column.ComputedDefinition);
            Add(changes, subject, "PERSISTED", current.IsPersisted, column.IsPersisted);
            Add(changes, subject, "GeneratedAlwaysType", current.GeneratedAlwaysType, column.GeneratedAlwaysType);
            Add(changes, subject, "Hidden", current.IsHidden, column.IsHidden);
            Add(changes, subject, "Sparse", current.IsSparse, column.IsSparse);
            Add(changes, subject, "RowGuid", current.IsRowGuid, column.IsRowGuid);
            Add(changes, subject, "ColumnSet", current.IsColumnSet, column.IsColumnSet);
            Add(changes, subject, "EncryptionType", current.EncryptionType, column.EncryptionType);
            Add(changes, subject, "TypedXML", current.XmlCollectionId != 0, column.XmlCollectionId != 0);
            if (!options.IgnoreCollation) Add(changes, subject, "定序", current.Collation, column.Collation);
            if (!options.IgnoreColumnOrder) Add(changes, subject, "欄位順序", current.Ordinal, column.Ordinal);
        }
        foreach (var column in target.Columns.Where(c => !sourceNames.Contains(c.Name))) changes.Add(new("欄位 " + SqlText.Quote(column.Name), "目標額外欄位", DescribeColumn(column, options), null, true));
    }

    private static void CompareCollections<T>(IEnumerable<T> source, IEnumerable<T> target, Func<T, string> name, Func<T, string> signature, Func<T, string> display, string kind, bool ignoreNames, List<PropertyDifference> changes)
    {
        var unmatched = target.ToList();
        foreach (var standard in source)
        {
            var index = ignoreNames ? unmatched.FindIndex(t => signature(t) == signature(standard)) : -1;
            if (index < 0) index = unmatched.FindIndex(t => name(t) == name(standard));
            if (index < 0) { changes.Add(new(kind + " " + SqlText.Quote(name(standard)), "存在", null, display(standard))); continue; }
            var current = unmatched[index]; unmatched.RemoveAt(index);
            if (signature(current) != signature(standard)) changes.Add(new(kind + " " + SqlText.Quote(name(standard)), "定義", display(current), display(standard)));
        }
        foreach (var current in unmatched) changes.Add(new(kind + " " + SqlText.Quote(name(current)), "目標額外物件", display(current), null, true));
    }

    private static string Identity(ColumnModel column) => column.IsIdentity ? $"IDENTITY({column.IdentitySeed},{column.IdentityIncrement})" : "無";
    private static void Add<T>(List<PropertyDifference> changes, string subject, string property, T current, T standard)
    {
        var a = Convert.ToString(current, CultureInfo.InvariantCulture); var b = Convert.ToString(standard, CultureInfo.InvariantCulture);
        if (!string.Equals(a, b, StringComparison.Ordinal)) changes.Add(new(subject, property, a, b));
    }

    public static string DescribeColumn(ColumnModel column, ComparisonOptions options)
    {
        var text = SqlText.Quote(column.Name) + " " + (column.ComputedDefinition is not null ? "AS " + column.ComputedDefinition + (column.IsPersisted ? " PERSISTED" : "") : column.SqlType + (column.IsNullable ? " NULL" : " NOT NULL"));
        if (column.IsIdentity) text += " " + Identity(column);
        if (column.DefaultDefinition is not null) text += " DEFAULT " + column.DefaultDefinition;
        if (!options.IgnoreCollation && column.Collation is not null) text += " COLLATE " + column.Collation;
        if (!options.IgnoreColumnOrder) text += " /* ordinal=" + column.Ordinal + " */";
        if (column.IsHidden || column.GeneratedAlwaysType != 0 || column.IsSparse || column.IsColumnSet || column.IsRowGuid || column.EncryptionType != 0 || column.XmlCollectionId != 0)
            text += $" /* hidden={column.IsHidden}, generated={column.GeneratedAlwaysType}, sparse={column.IsSparse}, columnSet={column.IsColumnSet}, rowGuid={column.IsRowGuid}, encryption={column.EncryptionType}, typedXml={column.XmlCollectionId != 0} */";
        return text;
    }

    public static string DescribeTable(TableModel table, ComparisonOptions options)
    {
        var text = new StringBuilder().AppendLine("TABLE " + table.Key.Sql).AppendLine($"/* temporal={table.TemporalType}, memoryOptimized={table.IsMemoryOptimized} */");
        var columns = options.IgnoreColumnOrder ? table.Columns.OrderBy(c => c.Name, StringComparer.Ordinal) : table.Columns.OrderBy(c => c.Ordinal);
        foreach (var column in columns) text.AppendLine("  " + DescribeColumn(column, options));
        foreach (var index in table.Indexes.OrderBy(i => options.IgnoreIndexNames ? i.Signature : i.Name, StringComparer.Ordinal))
            text.AppendLine("INDEX " + (options.IgnoreIndexNames ? "" : SqlText.Quote(index.Name) + " ") + index.Display);
        if (options.Scope.HasFlag(ComparisonScope.ForeignKeys))
            foreach (var item in table.ForeignKeys.OrderBy(i => options.IgnoreIndexNames ? i.Signature : i.Name, StringComparer.Ordinal)) text.AppendLine("FOREIGN KEY " + (options.IgnoreIndexNames ? "" : SqlText.Quote(item.Name) + " ") + item.Display);
        if (options.Scope.HasFlag(ComparisonScope.Checks))
            foreach (var item in table.Checks.OrderBy(i => options.IgnoreIndexNames ? i.Signature : i.Name, StringComparer.Ordinal)) text.AppendLine("CHECK " + (options.IgnoreIndexNames ? "" : SqlText.Quote(item.Name) + " ") + item.Display);
        return text.ToString();
    }
}

internal static class ModuleText
{
    private const string Identifier = "(?:\\[(?:[^\\]]|\\]\\])*\\]|\"(?:[^\"]|\"\")*\"|[\\p{L}_#@][\\p{L}\\p{N}_$#@]*)";
    private static readonly Regex Header = new(@"\A(?<prefix>(?:\s|--[^\r\n]*(?:\r?\n|$)|/\*[\s\S]*?\*/)*)" + @"(?:CREATE\s+OR\s+ALTER|CREATE|ALTER)\s+(?:VIEW|FUNCTION|PROCEDURE|PROC|TRIGGER)\s+" + Identifier + @"(?:\s*\.\s*" + Identifier + @")?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    public static string Kind(ModuleModel module) => module.Category switch { ObjectCategory.View => "VIEW", ObjectCategory.Procedure => "PROCEDURE", ObjectCategory.Trigger => "TRIGGER", _ => "FUNCTION" };
    public static string Comparable(ModuleModel module)
    {
        if (module.Definition is null) return "";
        var match = Header.Match(module.Definition);
        return SqlText.NormalizeLines(match.Success ? Kind(module) + " " + module.Key.Sql + module.Definition[match.Length..] : module.Definition).Trim();
    }
    public static string Display(ModuleModel module) => $"SET ANSI_NULLS {(module.AnsiNulls ? "ON" : "OFF")};\nSET QUOTED_IDENTIFIER {(module.QuotedIdentifier ? "ON" : "OFF")};\n/* type={module.TypeCode}; disabled={module.IsDisabled} */\n" + Comparable(module);
    public static string Ddl(ModuleModel module, bool exists)
    {
        if (!module.Readable) throw new NotSupportedException("定義不可讀取，無法產生 SQL。");
        var match = Header.Match(module.Definition!);
        if (!match.Success) throw new NotSupportedException("無法可靠解析 SQL 標頭，請人工檢阅原始定義。");
        return module.Definition![..match.Groups["prefix"].Length] + (exists ? "ALTER " : "CREATE ") + Kind(module) + " " + module.Key.Sql + module.Definition[match.Length..];
    }
}
