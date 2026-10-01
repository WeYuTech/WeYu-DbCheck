using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;

namespace DbCheck.Workbench;

internal sealed record ConnectionProfile(string Name, string ConnectionString)
{
    public override string ToString() => Name;
}
internal sealed record HistoryEntry(DateTimeOffset Time, string Source, string Target, bool Complete, int Required, int Retained, int Issues, ComparisonOptions Options, string[] RequiredObjects);

internal static class WorkspaceStore
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() }, MaxDepth = 64 };
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WeYu", "DbCheck");
    private static readonly SemaphoreSlim SettingsLock = new(1, 1);
    private const long MaximumInputBytes = 64 * 1024 * 1024;

    public static string SanitizeConnection(string connectionString)
    {
        var original = new SqlConnectionStringBuilder(connectionString);
        var sanitized = new SqlConnectionStringBuilder();
        // Deliberately whitelist settings: neither user ID/password nor arbitrary URL/token fields are saved.
        foreach (var key in new[] { "Data Source", "Initial Catalog", "Integrated Security", "Encrypt", "Trust Server Certificate", "Connect Timeout", "Application Intent", "Multi Subnet Failover" })
            if (original.ContainsKey(key)) sanitized[key] = original[key];
        return sanitized.ConnectionString;
    }

    public static async Task<IReadOnlyList<ConnectionProfile>> LoadProfilesAsync(CancellationToken token = default)
    {
        var path = Path.Combine(Root, "profiles.json");
        if (!File.Exists(path)) return [];
        var profiles = await ReadJsonAsync<List<ConnectionProfile>>(path, token);
        if (profiles.Count > 100 || profiles.Any(p => p is null || string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 80)) throw new InvalidOperationException("環境設定檔內容不合法。");
        return profiles.Select(p => new ConnectionProfile(p.Name, SanitizeConnection(p.ConnectionString))).ToArray();
    }

    public static async Task SaveProfileAsync(string name, string connectionString, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 80) throw new ArgumentException("環境名稱必須為 1～80 個字元。");
        var profile = new ConnectionProfile(name.Trim(), SanitizeConnection(connectionString));
        await SettingsLock.WaitAsync(token);
        try
        {
            var profiles = (await LoadProfilesAsync(token)).Where(p => !p.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase)).Append(profile).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            if (profiles.Length > 100) throw new InvalidOperationException("最多保存 100 個環境。");
            await WriteTextAsync(Path.Combine(Root, "profiles.json"), JsonSerializer.Serialize(profiles, JsonOptions), token);
        }
        finally { SettingsLock.Release(); }
    }

    public static async Task DeleteProfileAsync(string name, CancellationToken token = default)
    {
        await SettingsLock.WaitAsync(token);
        try { await WriteTextAsync(Path.Combine(Root, "profiles.json"), JsonSerializer.Serialize((await LoadProfilesAsync(token)).Where(p => p.Name != name), JsonOptions), token); }
        finally { SettingsLock.Release(); }
    }

    public static Task SaveSnapshotAsync(string path, CatalogSnapshot snapshot, CancellationToken token = default) => WriteTextAsync(path, JsonSerializer.Serialize(snapshot, JsonOptions), token);
    public static async Task<CatalogSnapshot> LoadSnapshotAsync(string path, CancellationToken token = default)
    {
        var snapshot = await ReadJsonAsync<CatalogSnapshot>(path, token);
        ValidateSnapshot(snapshot);
        return snapshot;
    }

    internal static void ValidateSnapshot(CatalogSnapshot snapshot)
    {
        if (snapshot.FormatVersion != 1) throw new NotSupportedException("不支援的快照格式版本。");
        if (string.IsNullOrWhiteSpace(snapshot.Server) || string.IsNullOrWhiteSpace(snapshot.Database) || snapshot.Options is null || snapshot.Tables is null || snapshot.Modules is null || snapshot.Schemas is null || snapshot.Dependencies is null || snapshot.Issues is null)
            throw new InvalidOperationException("快照缺少必要欄位。");
        snapshot.Options.Validate();
        if ((snapshot.CompletedScopes & ~ComparisonScope.All) != 0) throw new InvalidOperationException("快照含無效範圍。");
        static void Key(ObjectKey? key)
        {
            if (key is null || string.IsNullOrEmpty(key.Schema) || string.IsNullOrEmpty(key.Name) || key.Schema.Length > 128 || key.Name.Length > 128) throw new InvalidOperationException("快照物件名稱不合法。");
        }
        if (snapshot.Tables.Any(t => t is null) || snapshot.Modules.Any(m => m is null) || snapshot.Dependencies.Any(d => d is null) || snapshot.Issues.Any(i => i is null)) throw new InvalidOperationException("快照不可含空項目。");
        if (snapshot.Tables.Select(t => t.Key).Distinct().Count() != snapshot.Tables.Count || snapshot.Modules.Select(m => m.Key).Distinct().Count() != snapshot.Modules.Count) throw new InvalidOperationException("快照有重複物件鍵。");
        foreach (var table in snapshot.Tables)
        {
            Key(table.Key);
            if (table.Columns is null || table.Indexes is null || table.ForeignKeys is null || table.Checks is null || table.Columns.Any(c => c is null) || table.Indexes.Any(i => i is null) || table.ForeignKeys.Any(f => f is null) || table.Checks.Any(c => c is null)) throw new InvalidOperationException("快照的資料表 metadata 不完整。");
            if (table.Columns.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count() != table.Columns.Count || table.Indexes.Select(i => i.Name).Distinct(StringComparer.Ordinal).Count() != table.Indexes.Count) throw new InvalidOperationException("快照有重複欄位或索引。");
            foreach (var column in table.Columns)
                if (string.IsNullOrEmpty(column.Name) || column.Name.Length > 128 || string.IsNullOrEmpty(column.DataType) || string.IsNullOrEmpty(column.TypeSchema) || column.Length < -1 || column.Scale > 38 || column.Precision > 38) throw new InvalidOperationException("快照欄位資料不合法。");
            foreach (var index in table.Indexes)
                if (string.IsNullOrEmpty(index.Name) || index.Columns is null || index.Columns.Any(c => c is null || string.IsNullOrEmpty(c.Name))) throw new InvalidOperationException("快照索引 metadata 不完整。");
            foreach (var foreignKey in table.ForeignKeys)
            {
                Key(foreignKey.ReferencedTable);
                if (string.IsNullOrEmpty(foreignKey.Name) || foreignKey.Columns is null || foreignKey.Columns.Any(c => c is null || string.IsNullOrEmpty(c.Column) || string.IsNullOrEmpty(c.ReferencedColumn))) throw new InvalidOperationException("快照外鍵 metadata 不完整。");
            }
        }
        foreach (var module in snapshot.Modules) { Key(module.Key); if (!Enum.IsDefined(module.Category) || string.IsNullOrEmpty(module.TypeCode)) throw new InvalidOperationException("快照程式物件類型無效。"); }
        foreach (var dependency in snapshot.Dependencies) { Key(dependency.Owner); if (dependency.Referenced is not null) Key(dependency.Referenced); }
    }

    public static async Task SaveHistoryAsync(ComparisonReport report, CancellationToken token = default)
    {
        var entry = new HistoryEntry(report.GeneratedAt, report.Source, report.Target, report.Complete, report.RequiredCount, report.Differences.Count(d => d.State == DifferenceState.Retained), report.Issues.Count, report.Options, report.Differences.Where(d => d.CanSelect).Select(d => d.Id).ToArray());
        // Summary only: never automatically persist raw definitions, data rows, SQL or credentials.
        var directory = Path.Combine(Root, "history");
        await WriteTextAsync(Path.Combine(directory, report.GeneratedAt.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N") + ".json"), JsonSerializer.Serialize(entry, JsonOptions), token);
        foreach (var old in new DirectoryInfo(directory).EnumerateFiles("*.json").OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(100)) old.Delete();
    }

    public static async Task<IReadOnlyList<HistoryEntry>> LoadHistoryAsync(CancellationToken token = default)
    {
        var directory = Path.Combine(Root, "history");
        if (!Directory.Exists(directory)) return [];
        var result = new List<HistoryEntry>();
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.json").OrderByDescending(f => f.Name, StringComparer.Ordinal).Take(100))
        {
            token.ThrowIfCancellationRequested();
            try { result.Add(await ReadJsonAsync<HistoryEntry>(file.FullName, token)); }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException) { /* A corrupt summary must not prevent comparing databases. */ }
        }
        return result;
    }

    public static (int Added, int Resolved)? HistoryDelta(HistoryEntry current, HistoryEntry previous)
    {
        if (!current.Complete || !previous.Complete || current.Source != previous.Source || current.Target != previous.Target || JsonSerializer.Serialize(current.Options) != JsonSerializer.Serialize(previous.Options)) return null;
        return (current.RequiredObjects.Except(previous.RequiredObjects, StringComparer.Ordinal).Count(), previous.RequiredObjects.Except(current.RequiredObjects, StringComparer.Ordinal).Count());
    }

    public static Task SaveReportAsync(string path, ComparisonReport report, CancellationToken token = default)
    {
        var content = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".json" => JsonSerializer.Serialize(report, JsonOptions),
            ".html" => Html(report),
            ".csv" => Csv(report),
            _ => throw new ArgumentException("報告格式只支援 JSON、HTML 或 CSV。")
        };
        return WriteTextAsync(path, content, token);
    }

    internal static string CsvCell(string? text)
    {
        text ??= "";
        var first = text.AsSpan().TrimStart();
        if (!first.IsEmpty && first[0] is '=' or '+' or '-' or '@') text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    private static string Csv(ComparisonReport report)
    {
        var text = new StringBuilder("\uFEFF").AppendLine("\"物件\",\"類型\",\"狀態\",\"項目\",\"屬性\",\"檢查 DB\",\"標準 DB\",\"保留\"");
        foreach (var item in report.Differences)
        {
            var details = item.Properties.Count > 0 ? item.Properties : [new PropertyDifference("物件", "狀態", item.Message, null)];
            foreach (var detail in details) text.AppendLine(string.Join(",", new[] { item.Key.Sql, item.Category.ToString(), item.State.ToString(), detail.Subject, detail.Property, detail.Current, detail.Standard, detail.Retained.ToString() }.Select(CsvCell)));
        }
        foreach (var issue in report.Issues) text.AppendLine(string.Join(",", new[] { "範圍問題", "", "Unverifiable", "", "", issue, "", "" }.Select(CsvCell)));
        return text.ToString();
    }

    internal static string Html(ComparisonReport report)
    {
        static string H(string? value) => WebUtility.HtmlEncode(value ?? "");
        var html = new StringBuilder("<!doctype html><html lang=\"zh-Hant\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>WEYU DBCheck 比對報告</title><style>body{font:15px system-ui,sans-serif;margin:32px;color:#172638;background:#f4f6f9}article{background:white;padding:24px;margin:20px 0;border:1px solid #dce3eb;border-radius:10px}table{border-collapse:collapse;width:100%;table-layout:fixed}th,td{padding:12px;border:1px solid #dce3eb;vertical-align:top;overflow-wrap:anywhere}pre{white-space:pre-wrap;overflow-wrap:anywhere;font:13px Consolas,monospace}.warning{color:#9a4700}h1{margin-bottom:8px}</style><body>");
        html.Append("<h1>WEYU DBCheck 比對報告</h1><p>").Append(H(report.GeneratedAt.ToString("O"))).Append("</p><p>").Append(H(report.Source)).Append(" → ").Append(H(report.Target)).Append("</p><strong>").Append(H(report.Summary)).Append("</strong><p>").Append(H(report.Options.Description)).Append("</p><p class=warning>").Append(H(report.ConsistencyNote)).Append("</p>");
        foreach (var issue in report.Issues) html.Append("<p class=warning>").Append(H(issue)).Append("</p>");
        foreach (var item in report.Differences)
        {
            html.Append("<article><h2>").Append(H(item.Key.Sql)).Append(" · ").Append(H(item.State.ToString())).Append("</h2><p>").Append(H(item.Message)).Append("</p><table><tr><th>檢查 DB（目前）</th><th>標準 DB（基準）</th></tr><tr><td><pre>").Append(H(item.CurrentText)).Append("</pre></td><td><pre>").Append(H(item.StandardText)).Append("</pre></td></tr></table></article>");
        }
        return html.Append("</body></html>").ToString();
    }

    public static async Task WriteTextAsync(string path, string content, CancellationToken token = default)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task<T> ReadJsonAsync<T>(string path, CancellationToken token)
    {
        if (new FileInfo(path).Length > MaximumInputBytes) throw new InvalidOperationException("檔案超過 64 MiB，請縮小快照範圍。");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, token) ?? throw new InvalidOperationException("JSON 檔案不可為空。");
    }
}
