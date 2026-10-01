using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

internal static class ViewSchemaService
{
    internal sealed record ViewDefinition(string Definition, bool AnsiNulls, bool QuotedIdentifier, string Type = "V")
    {
        internal string Kind => Type == "V" ? "VIEW" : Type is "P" or "PC" ? "PROCEDURE" : "FUNCTION";
    }
    private const string Identifier = "(?:\\[(?:[^\\]]|\\]\\])*\\]|\"(?:[^\"]|\"\")*\"|[\\p{L}_#@][\\p{L}\\p{N}_$#@]*)";
    private static readonly Regex Header = new(@"\A(?<prefix>(?:\s|--[^\r\n]*(?:\r?\n|$)|/\*[\s\S]*?\*/)*)" +
        @"(?:CREATE\s+OR\s+ALTER|CREATE|ALTER)\s+(?:VIEW|FUNCTION|PROCEDURE|PROC)\s+" + Identifier + @"(?:\s*\.\s*" + Identifier + @")?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    internal static string Generate(DataTableSchemaReport.TableDifference key, ViewDefinition standard, bool exists, string? actualType = null)
    {
        if (exists && actualType is not null && actualType != standard.Type)
            throw new NotSupportedException($"{key} 物件類型不同（{standard.Type}／{actualType}），需人工檢閱重建，不能直接 ALTER。");
        var match = Header.Match(standard.Definition);
        if (!match.Success) throw new NotSupportedException($"無法解析 {key} 的 {standard.Kind} 定義標頭，請人工檢閱。");
        // Use catalog identity; sp_rename may leave the old name in sys.sql_modules.
        var ddl = standard.Definition[..match.Groups["prefix"].Length] + (exists ? "ALTER " : "CREATE ") + standard.Kind + " "
            + key + standard.Definition[match.Length..];
        return $"-- 標準 DB → 檢查 DB：{key}；僅預覽，未執行。\r\n"
            + "-- 請先確認目標 schema 與相依物件；ALTER VIEW 可能移除索引化 View 的索引，需另行檢閱。\r\n"
            + $"SET ANSI_NULLS {(standard.AnsiNulls ? "ON" : "OFF")};\r\nGO\r\n"
            + $"SET QUOTED_IDENTIFIER {(standard.QuotedIdentifier ? "ON" : "OFF")};\r\nGO\r\n" + ddl;
    }

    private static string Comparable(DataTableSchemaReport.TableDifference key, ViewDefinition definition)
    {
        var match = Header.Match(definition.Definition);
        var body = match.Success ? definition.Kind + " " + key + definition.Definition[match.Length..] : definition.Definition;
        return body.Replace("\r\n", "\n").Trim();
    }

    internal static DataTableSchemaReport.Report Compare(
        IReadOnlyDictionary<DataTableSchemaReport.TableDifference, ViewDefinition> standard,
        IReadOnlyDictionary<DataTableSchemaReport.TableDifference, ViewDefinition> actual)
    {
        var details = new Dictionary<DataTableSchemaReport.TableDifference, string>();
        foreach (var (key, view) in standard.OrderBy(p => p.Key.Schema, StringComparer.Ordinal).ThenBy(p => p.Key.Name, StringComparer.Ordinal))
        {
            var exists = actual.TryGetValue(key, out var other);
            if (exists && view.Type == other!.Type && view.AnsiNulls == other.AnsiNulls && view.QuotedIdentifier == other.QuotedIdentifier
                && Comparable(key, view) == Comparable(key, other)) continue;
            string Describe(ViewDefinition v) => $"類型={v.Kind} ({v.Type})；ANSI_NULLS={v.AnsiNulls}；QUOTED_IDENTIFIER={v.QuotedIdentifier}\r\n{v.Definition}";
            details[key] = $"{view.Kind} {key}：{(exists ? "定義不同" : "檢查 DB 不存在")}\r\n\r\n標準 DB：\r\n{Describe(view)}\r\n\r\n檢查 DB：\r\n{(exists ? Describe(other!) : "（不存在）")}";
        }
        return new(string.Join("\r\n\r\n", details.Values), details.Count, details.Keys.ToArray(), details);
    }

    internal static async Task<Dictionary<DataTableSchemaReport.TableDifference, ViewDefinition>> ReadAsync(string connectionString, CancellationToken token, bool routines = false)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        using var permission = new SqlCommand("SELECT HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DEFINITION')", connection);
        if (Convert.ToInt32(await permission.ExecuteScalarAsync(token)) != 1) throw new InvalidOperationException("需要 VIEW DEFINITION 權限。");
        using var command = new SqlCommand("""
            SELECT s.name,v.name,m.definition,m.uses_ansi_nulls,m.uses_quoted_identifier,v.type
            FROM sys.objects v JOIN sys.schemas s ON s.schema_id=v.schema_id
            LEFT JOIN sys.sql_modules m ON m.object_id=v.object_id
            WHERE v.is_ms_shipped=0 AND ((@Routines=0 AND v.type='V')
                OR (@Routines=1 AND v.type IN ('FN','IF','TF','P','FS','FT','PC')))
            ORDER BY s.name,v.name
            """, connection) { CommandTimeout = 60 };
        command.Parameters.Add("@Routines", System.Data.SqlDbType.Bit).Value = routines;
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new Dictionary<DataTableSchemaReport.TableDifference, ViewDefinition>();
        while (await reader.ReadAsync(token))
        {
            if (reader.IsDBNull(2)) throw new InvalidOperationException("物件為 CLR、已加密或無法讀取定義，不能完成比對。");
            result.Add(new(reader.GetString(0),reader.GetString(1)),new(reader.GetString(2),reader.GetBoolean(3),reader.GetBoolean(4),reader.GetString(5).Trim()));
        }
        return result;
    }
}
