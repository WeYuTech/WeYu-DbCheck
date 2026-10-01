using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DbCheck.Workbench;

[Flags]
internal enum ComparisonScope { None = 0, Tables = 1, Views = 2, Routines = 4, ForeignKeys = 8, Checks = 16, Triggers = 32, All = 63 }
internal enum ObjectCategory { Table, View, Function, Procedure, Trigger, Scope }
internal enum DifferenceState { Missing, Changed, Retained, Unverifiable }
internal enum RiskLevel { Information, Review, Blocked }

internal sealed record ObjectKey(string Schema, string Name)
{
    [JsonIgnore] public string Sql => SqlText.Quote(Schema) + "." + SqlText.Quote(Name);
    public override string ToString() => Sql;
}

internal sealed record ComparisonOptions
{
    public ComparisonScope Scope { get; init; } = ComparisonScope.Tables | ComparisonScope.Views | ComparisonScope.Routines | ComparisonScope.ForeignKeys | ComparisonScope.Checks;
    public bool IgnoreCollation { get; init; } = true;
    public bool IgnoreColumnOrder { get; init; } = true;
    public bool IgnoreIndexNames { get; init; } = true;
    public bool ExcludeZZ { get; init; }
    public string[] ExcludedPrefixes { get; init; } = [];

    public bool Includes(ObjectKey key, ObjectCategory category)
    {
        var builtIn = category == ObjectCategory.View ? "V_ZZ" : "ZZ";
        return !(ExcludeZZ && key.Name.StartsWith(builtIn, StringComparison.OrdinalIgnoreCase))
            && !ExcludedPrefixes.Any(p => p.Length > 0 && key.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    public void Validate()
    {
        if (Scope == ComparisonScope.None || (Scope & ~ComparisonScope.All) != 0)
            throw new ArgumentException("請至少選擇一種有效的比對範圍。");
        if ((Scope & (ComparisonScope.ForeignKeys | ComparisonScope.Checks)) != 0 && !Scope.HasFlag(ComparisonScope.Tables))
            throw new ArgumentException("比對外鍵或 CHECK 時也必須選擇資料表。");
        if (ExcludedPrefixes is null || ExcludedPrefixes.Any(p => p is null || p.Length > 128))
            throw new ArgumentException("排除前綴不可超過 128 個字元。");
    }

    [JsonIgnore] public string Description => $"範圍：{Scope}；忽略定序：{IgnoreCollation}；忽略欄位順序：{IgnoreColumnOrder}；忽略索引名稱：{IgnoreIndexNames}；排除 ZZ：{ExcludeZZ}；其他排除：{string.Join(", ", ExcludedPrefixes)}；目標獨有物件一律保留。";
}

internal sealed record ColumnModel
{
    public string Name { get; init; } = "";
    public string TypeSchema { get; init; } = "sys";
    public string DataType { get; init; } = "";
    public byte SystemTypeId { get; init; }
    public int Length { get; init; }
    public int MaxLengthBytes { get; init; }
    public byte Precision { get; init; }
    public byte Scale { get; init; }
    public bool IsNullable { get; init; }
    public int Ordinal { get; init; }
    public bool IsIdentity { get; init; }
    public string? IdentitySeed { get; init; }
    public string? IdentityIncrement { get; init; }
    public string? Collation { get; init; }
    public string? DefaultDefinition { get; init; }
    public string? ComputedDefinition { get; init; }
    public bool IsPersisted { get; init; }
    public bool IsHidden { get; init; }
    public int GeneratedAlwaysType { get; init; }
    public bool IsSparse { get; init; }
    public bool IsRowGuid { get; init; }
    public bool IsColumnSet { get; init; }
    public int EncryptionType { get; init; }
    public int XmlCollectionId { get; init; }
    [JsonIgnore] public bool IsWritable => ComputedDefinition is null && SystemTypeId != 189 && !IsHidden && GeneratedAlwaysType == 0 && !IsColumnSet && EncryptionType == 0;
    [JsonIgnore] public string SqlType => TypeSchema != "sys" ? SqlText.Quote(TypeSchema) + "." + SqlText.Quote(DataType) : SqlText.Quote(DataType) + (DataType switch
    {
        "varchar" or "nvarchar" or "char" or "nchar" or "binary" or "varbinary" => "(" + (Length == -1 ? "MAX" : Length.ToString(CultureInfo.InvariantCulture)) + ")",
        "decimal" or "numeric" => $"({Precision},{Scale})",
        "datetime2" or "datetimeoffset" or "time" => $"({Scale})",
        "float" => $"({Precision})",
        _ => ""
    });
}

internal sealed record IndexColumnModel(string Name, int KeyOrdinal, bool Descending, bool Included);
internal sealed record IndexModel
{
    public string Name { get; init; } = "";
    public int Type { get; init; }
    public bool IsUnique { get; init; }
    public bool IsPrimaryKey { get; init; }
    public bool IsUniqueConstraint { get; init; }
    public bool IsDisabled { get; init; }
    public string? Filter { get; init; }
    public List<IndexColumnModel> Columns { get; init; } = [];
    [JsonIgnore] public string Signature => JsonSerializer.Serialize(new { Type, IsUnique, IsPrimaryKey, IsUniqueConstraint, IsDisabled, Filter = SqlText.NormalizeLines(Filter ?? "").Trim(), Keys = Columns.Where(c => !c.Included).OrderBy(c => c.KeyOrdinal), Includes = Columns.Where(c => c.Included).Select(c => c.Name).Order(StringComparer.Ordinal) });
    [JsonIgnore] public string Display => $"{(IsPrimaryKey ? "PRIMARY KEY " : "")}{(IsUnique ? "UNIQUE " : "")}{(Type == 1 ? "CLUSTERED" : Type == 2 ? "NONCLUSTERED" : "TYPE " + Type)} ({string.Join(", ", Columns.Where(c => !c.Included).OrderBy(c => c.KeyOrdinal).Select(c => SqlText.Quote(c.Name) + (c.Descending ? " DESC" : " ASC")))})" + (Columns.Any(c => c.Included) ? " INCLUDE (" + string.Join(", ", Columns.Where(c => c.Included).Select(c => c.Name).Order(StringComparer.Ordinal).Select(SqlText.Quote)) + ")" : "") + (Filter is null ? "" : " WHERE " + Filter) + (IsDisabled ? " DISABLED" : "");
}

internal sealed record ForeignKeyColumnModel(string Column, string ReferencedColumn, int Ordinal);
internal sealed record ForeignKeyModel
{
    public string Name { get; init; } = "";
    public ObjectKey ReferencedTable { get; init; } = new("", "");
    public string DeleteAction { get; init; } = "NO_ACTION";
    public string UpdateAction { get; init; } = "NO_ACTION";
    public bool IsDisabled { get; init; }
    public bool IsNotTrusted { get; init; }
    public bool NotForReplication { get; init; }
    public List<ForeignKeyColumnModel> Columns { get; init; } = [];
    [JsonIgnore] public string Signature => JsonSerializer.Serialize(new { ReferencedTable, DeleteAction, UpdateAction, IsDisabled, IsNotTrusted, NotForReplication, Columns = Columns.OrderBy(c => c.Ordinal) });
    [JsonIgnore] public string Display => $"({string.Join(", ", Columns.OrderBy(c => c.Ordinal).Select(c => SqlText.Quote(c.Column)))}) REFERENCES {ReferencedTable.Sql} ({string.Join(", ", Columns.OrderBy(c => c.Ordinal).Select(c => SqlText.Quote(c.ReferencedColumn)))}) ON DELETE {DeleteAction} ON UPDATE {UpdateAction}; disabled={IsDisabled}; untrusted={IsNotTrusted}; not-for-replication={NotForReplication}";
}

internal sealed record CheckModel(string Name, string Definition, bool IsDisabled, bool IsNotTrusted, bool NotForReplication)
{
    [JsonIgnore] public string Signature => JsonSerializer.Serialize(new { Definition = SqlText.NormalizeLines(Definition).Trim(), IsDisabled, IsNotTrusted, NotForReplication });
    [JsonIgnore] public string Display => Definition + $"; disabled={IsDisabled}; untrusted={IsNotTrusted}; not-for-replication={NotForReplication}";
}

internal sealed record TableModel
{
    public ObjectKey Key { get; init; } = new("", "");
    public int TemporalType { get; init; }
    public bool IsMemoryOptimized { get; init; }
    public List<ColumnModel> Columns { get; init; } = [];
    public List<IndexModel> Indexes { get; init; } = [];
    public List<ForeignKeyModel> ForeignKeys { get; init; } = [];
    public List<CheckModel> Checks { get; init; } = [];
}

internal sealed record ModuleModel
{
    public ObjectKey Key { get; init; } = new("", "");
    public ObjectCategory Category { get; init; }
    public string TypeCode { get; init; } = "";
    public string? Definition { get; init; }
    public bool AnsiNulls { get; init; }
    public bool QuotedIdentifier { get; init; }
    public bool IsSchemaBound { get; init; }
    public bool IsDisabled { get; init; }
    public bool HasIndexes { get; init; }
    public ObjectKey? Parent { get; init; }
    [JsonIgnore] public bool Readable => Definition is not null && TypeCode is "V" or "P" or "FN" or "IF" or "TF" or "TR";
}

internal sealed record DependencyModel(ObjectKey Owner, ObjectKey? Referenced, string? Server, string? Database, string ReferencedName, bool Resolved, bool SchemaBound);
internal sealed record CatalogIssue(ComparisonScope Scope, ObjectKey? Object, string Message);
internal sealed record CatalogSnapshot
{
    public int FormatVersion { get; init; } = 1;
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.Now;
    public string Server { get; init; } = "";
    public string Database { get; init; } = "";
    public ComparisonOptions Options { get; init; } = new();
    public ComparisonScope CompletedScopes { get; set; }
    public bool DependenciesRead { get; set; }
    public List<string> Schemas { get; init; } = [];
    public List<TableModel> Tables { get; init; } = [];
    public List<ModuleModel> Modules { get; init; } = [];
    public List<DependencyModel> Dependencies { get; init; } = [];
    public List<CatalogIssue> Issues { get; init; } = [];
    [JsonIgnore] public string Label => Server + " / " + Database;
    [JsonIgnore] public bool Complete => (CompletedScopes & Options.Scope) == Options.Scope && Issues.Count == 0;
    [JsonIgnore] public string Fingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Tables, Modules, Schemas, Dependencies, CompletedScopes, DependenciesRead }))));
}

internal sealed record PropertyDifference(string Subject, string Property, string? Current, string? Standard, bool Retained = false);
internal sealed record ObjectDifference
{
    public ObjectKey Key { get; init; } = new("", "");
    public ObjectCategory Category { get; init; }
    public DifferenceState State { get; init; }
    public List<PropertyDifference> Properties { get; init; } = [];
    public string CurrentText { get; init; } = "";
    public string StandardText { get; init; } = "";
    public string? Message { get; init; }
    [JsonIgnore] public bool CanSelect => State is DifferenceState.Missing or DifferenceState.Changed;
    [JsonIgnore] public string Id => Category + ":" + JsonSerializer.Serialize(Key);
}

internal sealed record ComparisonReport
{
    public int FormatVersion { get; init; } = 1;
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.Now;
    public string Source { get; init; } = "";
    public string Target { get; init; } = "";
    public DateTimeOffset SourceCapturedAt { get; init; }
    public DateTimeOffset TargetCapturedAt { get; init; }
    public ComparisonOptions Options { get; init; } = new();
    public List<ObjectDifference> Differences { get; init; } = [];
    public List<string> Issues { get; init; } = [];
    public string ConsistencyNote { get; init; } = "各查詢及兩個資料庫不是同一個一致性快照；未檢查資料內容、權限、儲存配置及未選取範圍。";
    [JsonIgnore] public bool Complete => Issues.Count == 0 && Differences.All(d => d.State != DifferenceState.Unverifiable);
    [JsonIgnore] public int RequiredCount => Differences.Count(d => d.CanSelect);
    [JsonIgnore] public string Summary => !Complete ? $"部分完成：{RequiredCount} 個待處理物件；{Issues.Count} 個讀取／範圍問題。" : RequiredCount == 0 ? "已選範圍內無待處理差異；未選範圍不代表一致。" : $"已選範圍內有 {RequiredCount} 個待處理物件。";
}

internal sealed record ComparisonSession(CatalogSnapshot Source, CatalogSnapshot Target, ComparisonReport Report);
internal sealed record PlanStep(ObjectKey Object, string Action, RiskLevel Risk, string Note, string? Sql = null, bool AnsiNulls = true, bool QuotedIdentifier = true);
internal sealed record ChangePlan(List<PlanStep> Steps, string Sql)
{
    [JsonIgnore] public bool Blocked => Steps.Any(s => s.Risk == RiskLevel.Blocked);
}
internal sealed record PreflightFinding(ObjectKey Object, string Check, long? AffectedRows, RiskLevel Risk, string Message);

internal static class SqlText
{
    public static string Quote(string name) => "[" + name.Replace("]", "]]") + "]";
    public static string String(string value) => "N'" + value.Replace("'", "''") + "'";
    public static string NormalizeLines(string value) => value.Replace("\r\n", "\n").Replace('\r', '\n');
    public static string Comment(string value) => value.Replace('\r', ' ').Replace('\n', ' ');
    public static string Failure(Exception error) => error switch
    {
        Microsoft.Data.SqlClient.SqlException sql => $"SQL {sql.Number}：請檢查連線、權限或逾時；未輸出 SQL 與帳密。",
        OperationCanceledException => "工作已取消。",
        ArgumentException or InvalidOperationException or NotSupportedException or FormatException => error.Message,
        _ => "作業失敗（" + error.GetType().Name + "）；未輸出敏感資訊。"
    };
}
