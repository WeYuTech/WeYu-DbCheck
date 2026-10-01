using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;

internal static class DataInsertSqlGenerator
{
    internal static string Quote(string name) => "[" + name.Replace("]", "]]") + "]";

    internal static string Literal(object? value) => value switch
    {
        null or DBNull => "NULL",
        string text => "N'" + text.Replace("'", "''") + "'",
        DateTime date => "'" + date.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "'",
        DateTimeOffset date => "'" + date.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture) + "'",
        TimeSpan time => "'" + time.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture) + "'",
        bool flag => flag ? "1" : "0",
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        Guid guid => "'" + guid.ToString("D") + "'",
        System.Data.SqlTypes.SqlDecimal number => number.IsNull ? "NULL" : number.ToString().Replace(CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator, "."),
        byte or short or int or long or decimal => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        float number when float.IsFinite(number) => number.ToString("R", CultureInfo.InvariantCulture),
        double number when double.IsFinite(number) => number.ToString("R", CultureInfo.InvariantCulture),
        _ => throw new NotSupportedException("此資料型別不支援轉換 INSERT SQL：" + value.GetType().Name)
    };

    internal static string BuildSelect(string schema, string table, IEnumerable<string> columns, IReadOnlyList<string> keys, bool descending)
    {
        if (keys.Count == 0) throw new InvalidOperationException("此資料表沒有主鍵，無法保證順排／逆排；請先提供可排序的主鍵。");
        return $"SELECT TOP (@RowCount) {string.Join(", ", columns.Select(Quote))} FROM {Quote(schema)}.{Quote(table)} ORDER BY "
            + string.Join(", ", keys.Select(key => Quote(key) + (descending ? " DESC" : " ASC"))) + ";";
    }

    internal static async Task<string> GenerateAsync(string connectionString, string schema, string table, CancellationToken token, int rowCount = 100, bool descending = false)
    {
        if (rowCount <= 0) throw new ArgumentOutOfRangeException(nameof(rowCount), "資料筆數必須大於 0。");
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        using var metadata = new SqlCommand("""
            SELECT c.name,c.is_identity,c.system_type_id
            FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
            JOIN sys.columns c ON c.object_id=t.object_id
            WHERE s.name=@Schema AND t.name=@Table AND c.is_computed=0
                AND c.system_type_id<>189 AND c.generated_always_type=0 AND c.is_hidden=0
            ORDER BY c.column_id;
            SELECT c.name FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
            JOIN sys.indexes i ON i.object_id=t.object_id AND i.is_primary_key=1
            JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
            JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
            WHERE s.name=@Schema AND t.name=@Table AND ic.key_ordinal>0 ORDER BY ic.key_ordinal;
            """, connection) { CommandTimeout = 60 };
        metadata.Parameters.Add("@Schema", System.Data.SqlDbType.NVarChar,128).Value = schema;
        metadata.Parameters.Add("@Table", System.Data.SqlDbType.NVarChar,128).Value = table;
        var columns = new List<(string Name, bool Identity, byte Type)>();
        var keys = new List<string>();
        await using (var reader = await metadata.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token)) columns.Add((reader.GetString(0), reader.GetBoolean(1), reader.GetByte(2)));
            await reader.NextResultAsync(token);
            while (await reader.ReadAsync(token)) keys.Add(reader.GetString(0));
        }
        if (columns.Count == 0) throw new InvalidOperationException("所選資料表不存在、沒有可寫入欄位或沒有讀取權限。");
        if (columns.Any(c => c.Type is 240 or 98))
            throw new NotSupportedException("此表含 CLR／空間／sql_variant 型別，無法可靠產生 INSERT SQL。");
        var full = Quote(schema) + "." + Quote(table);
        var names = string.Join(", ", columns.Select(c => Quote(c.Name)));
        using var query = new SqlCommand(BuildSelect(schema, table, columns.Select(c => c.Name), keys, descending), connection) { CommandTimeout = 60 };
        query.Parameters.Add("@RowCount", System.Data.SqlDbType.Int).Value = rowCount;
        var statements = new StringBuilder();
        var count = 0;
        await using (var reader = await query.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                var values = new string[columns.Count];
                for (var i = 0; i < columns.Count; i++)
                    values[i] = Literal(reader.IsDBNull(i) ? DBNull.Value : columns[i].Type is 106 or 108 ? reader.GetSqlDecimal(i) : reader.GetValue(i));
                statements.AppendLine($"INSERT INTO {full} ({names}) VALUES ({string.Join(", ", values)});");
                count++;
            }
        }
        var sql = new StringBuilder().AppendLine($"-- 來源：標準 DB；目標：檢查 DB；TABLE {full}；筆數：{count}（最多 {rowCount}）")
            .AppendLine($"-- 依主鍵{(descending ? "逆排 DESC" : "順排 ASC")}取前 {rowCount} 筆。")
            .AppendLine("-- 僅供預覽，未執行。請確認目標資料庫、鍵值衝突及欄位相容性。")
            .AppendLine("-- 計算欄位、rowversion 與系統產生欄位不寫入；日期時間輸出毫秒精度，datetimeoffset 保留時區。")
            .AppendLine();
        if (count == 0) return sql.AppendLine("-- 標準資料表沒有資料。").ToString();
        if (columns.Any(c => c.Identity))
            sql.AppendLine($"SET IDENTITY_INSERT {full} ON;")
                .AppendLine("BEGIN TRY").Append(statements).AppendLine($"SET IDENTITY_INSERT {full} OFF;")
                .AppendLine("END TRY").AppendLine("BEGIN CATCH")
                .AppendLine($"SET IDENTITY_INSERT {full} OFF;").AppendLine("THROW;").AppendLine("END CATCH;");
        else sql.Append(statements);
        return sql.ToString();
    }
}
