using System.Text.Json;
using System.Data;
using Microsoft.Data.SqlClient;

internal static class SchemaReader
{
    /// <summary>唯讀取得資料庫欄位結構，每個欄位一列；同欄位的多個索引彙整於 IndexInfo。</summary>
    internal static async Task<DataTable> GetTableStructureAsync(string connectionString, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        using var permission = new SqlCommand("SELECT HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DEFINITION')", connection);
        if (Convert.ToInt32(await permission.ExecuteScalarAsync(token)) != 1)
            throw new InvalidOperationException("需要資料庫 VIEW DEFINITION 權限，避免取得不完整的結構。");
        const string sql = """
            SELECT s.name AS SchemaName, t.name AS TableName, c.name AS ColumnName,
                ts.name AS TypeSchema, ty.name AS DataType,
                CASE WHEN c.max_length = -1 THEN -1
                     WHEN c.system_type_id IN (231,239) THEN c.max_length / 2
                     ELSE c.max_length END AS [Length],
                c.max_length AS MaxLengthBytes,
                c.precision AS [Precision], c.scale AS Scale,
                c.is_nullable AS IsNullable, c.column_id AS ColumnOrdinal,
                c.is_identity AS IsIdentity,
                CONVERT(nvarchar(100), id.seed_value) AS IdentitySeed,
                CONVERT(nvarchar(100), id.increment_value) AS IdentityIncrement,
                c.collation_name AS CollationName,
                dc.definition AS DefaultDefinition,
                cc.definition AS ComputedDefinition, cc.is_persisted AS IsPersisted,
                CAST(CASE WHEN EXISTS (
                    SELECT 1 FROM sys.indexes i JOIN sys.index_columns ic
                    ON ic.object_id=i.object_id AND ic.index_id=i.index_id
                    WHERE i.object_id=t.object_id AND ic.column_id=c.column_id AND i.is_primary_key=1
                ) THEN 1 ELSE 0 END AS bit) AS IsPrimaryKey,
                CAST(CASE WHEN EXISTS (
                    SELECT 1 FROM sys.indexes i JOIN sys.index_columns ic
                    ON ic.object_id=i.object_id AND ic.index_id=i.index_id
                    WHERE i.object_id=t.object_id AND ic.column_id=c.column_id
                    AND i.index_id>0 AND i.is_hypothetical=0
                ) THEN 1 ELSE 0 END AS bit) AS HasIndex,
                (SELECT i.name,i.type,i.is_unique,i.is_primary_key,i.is_unique_constraint,i.is_disabled,i.filter_definition,
                    JSON_QUERY((SELECT col.name,ixc.key_ordinal,ixc.is_descending_key,ixc.is_included_column
                        FROM sys.index_columns ixc JOIN sys.columns col ON col.object_id=ixc.object_id AND col.column_id=ixc.column_id
                        WHERE ixc.object_id=i.object_id AND ixc.index_id=i.index_id
                        ORDER BY ixc.index_column_id FOR JSON PATH)) AS columns
                 FROM sys.indexes i WHERE i.object_id=t.object_id AND i.index_id>0 AND i.is_hypothetical=0
                 ORDER BY i.name FOR JSON PATH) AS IndexDefinitions,
                ISNULL(STUFF((
                    SELECT N'; ' + QUOTENAME(i.name) + N' [' + i.type_desc
                        + CASE WHEN i.is_primary_key=1 THEN N', PRIMARY KEY' ELSE N'' END
                        + CASE WHEN i.is_unique=1 THEN N', UNIQUE' ELSE N'' END
                        + CASE WHEN i.is_unique_constraint=1 THEN N', UNIQUE CONSTRAINT' ELSE N'' END
                        + CASE WHEN ic.is_included_column=1 THEN N', INCLUDE'
                               ELSE N', KEY #' + CONVERT(nvarchar(10),ic.key_ordinal)
                                    + CASE WHEN ic.is_descending_key=1 THEN N' DESC' ELSE N' ASC' END END
                        + CASE WHEN i.is_disabled=1 THEN N', DISABLED' ELSE N'' END
                        + CASE WHEN i.has_filter=1 THEN N', FILTER=' + ISNULL(i.filter_definition,N'') ELSE N'' END
                        + N']'
                    FROM sys.indexes i JOIN sys.index_columns ic
                    ON ic.object_id=i.object_id AND ic.index_id=i.index_id
                    WHERE i.object_id=t.object_id AND ic.column_id=c.column_id
                    AND i.index_id>0 AND i.is_hypothetical=0
                    ORDER BY i.name,ic.index_column_id
                    FOR XML PATH(''),TYPE
                ).value('.', 'nvarchar(max)'),1,2,N''),N'') AS IndexInfo
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id=t.schema_id
            JOIN sys.columns c ON c.object_id=t.object_id
            JOIN sys.types ty ON ty.user_type_id=c.user_type_id
            JOIN sys.schemas ts ON ts.schema_id=ty.schema_id
            LEFT JOIN sys.identity_columns id ON id.object_id=c.object_id AND id.column_id=c.column_id
            LEFT JOIN sys.default_constraints dc ON dc.object_id=c.default_object_id
            LEFT JOIN sys.computed_columns cc ON cc.object_id=c.object_id AND cc.column_id=c.column_id
            WHERE t.is_ms_shipped=0
            ORDER BY t.name,c.name,s.name;
            """;
        using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        await using var reader = await command.ExecuteReaderAsync(token);
        var table = new DataTable("TableStructure");
        for (var i = 0; i < reader.FieldCount; i++)
            table.Columns.Add(reader.GetName(i), reader.GetFieldType(i));
        while (await reader.ReadAsync(token))
        {
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            table.Rows.Add(values);
        }
        return table;
    }

    // Only catalog SELECTs are executed; no schema or data changes.
    private static readonly (string Kind, string Query, int KeyColumns)[] Queries =
    [
        ("Table", """
            SELECT s.name,t.name,t.temporal_type,t.is_memory_optimized
            FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
            WHERE t.is_ms_shipped=0
            """, 2),
        ("Column", """
            SELECT s.name,t.name,c.name,c.column_id,ts.name,ty.name,c.max_length,c.precision,c.scale,
                c.is_nullable,c.collation_name,c.is_identity,
                CONVERT(nvarchar(100),ic.seed_value),CONVERT(nvarchar(100),ic.increment_value),
                cc.definition,cc.is_persisted,dc.definition,c.is_rowguidcol,c.is_sparse,c.generated_always_type
            FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
            JOIN sys.columns c ON c.object_id=t.object_id
            JOIN sys.types ty ON ty.user_type_id=c.user_type_id
            JOIN sys.schemas ts ON ts.schema_id=ty.schema_id
            LEFT JOIN sys.identity_columns ic ON ic.object_id=c.object_id AND ic.column_id=c.column_id
            LEFT JOIN sys.computed_columns cc ON cc.object_id=c.object_id AND cc.column_id=c.column_id
            LEFT JOIN sys.default_constraints dc ON dc.object_id=c.default_object_id
            WHERE t.is_ms_shipped=0
            """, 3),
        ("Index", """
            SELECT s.name,t.name,i.name,i.type_desc,i.is_unique,i.is_primary_key,i.is_unique_constraint,
                i.filter_definition,i.is_disabled,
                (SELECT c.name AS [column],ic.key_ordinal,ic.is_descending_key,ic.is_included_column
                 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
                 WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id
                 ORDER BY ic.index_column_id FOR JSON PATH)
            FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
            JOIN sys.indexes i ON i.object_id=t.object_id
            WHERE t.is_ms_shipped=0 AND i.index_id>0 AND i.is_hypothetical=0
            """, 3),
        ("ForeignKey", """
            SELECT s.name,t.name,f.name,rs.name,rt.name,f.delete_referential_action_desc,
                f.update_referential_action_desc,f.is_disabled,f.is_not_trusted,f.is_not_for_replication,
                (SELECT pc.name AS [column],rc.name AS referencedColumn
                 FROM sys.foreign_key_columns fc
                 JOIN sys.columns pc ON pc.object_id=fc.parent_object_id AND pc.column_id=fc.parent_column_id
                 JOIN sys.columns rc ON rc.object_id=fc.referenced_object_id AND rc.column_id=fc.referenced_column_id
                 WHERE fc.constraint_object_id=f.object_id ORDER BY fc.constraint_column_id FOR JSON PATH)
            FROM sys.foreign_keys f JOIN sys.tables t ON t.object_id=f.parent_object_id
            JOIN sys.schemas s ON s.schema_id=t.schema_id
            JOIN sys.tables rt ON rt.object_id=f.referenced_object_id
            JOIN sys.schemas rs ON rs.schema_id=rt.schema_id WHERE t.is_ms_shipped=0
            """, 3),
        ("Check", """
            SELECT s.name,t.name,ck.name,ck.definition,ck.is_disabled,ck.is_not_trusted,ck.is_not_for_replication
            FROM sys.check_constraints ck JOIN sys.tables t ON t.object_id=ck.parent_object_id
            JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE t.is_ms_shipped=0
            """, 3)
    ];

    internal static async Task<Dictionary<string, string>> ReadAsync(string connectionString, CancellationToken token, IProgress<string>? progress = null)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        using var permission = new SqlCommand("SELECT HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DEFINITION')", connection);
        if (Convert.ToInt32(await permission.ExecuteScalarAsync(token)) != 1)
            throw new InvalidOperationException("需要資料庫 VIEW DEFINITION 權限，避免不完整比對。");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (kind, query, keyColumns) in Queries)
        {
            progress?.Report($"讀取 {kind}…");
            var count = 0;
            using var command = new SqlCommand(query, connection) { CommandTimeout = 60 };
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var key = kind + ":" + JsonSerializer.Serialize(Enumerable.Range(0, keyColumns).Select(reader.GetString));
                var values = new SortedDictionary<string, object?>(StringComparer.Ordinal);
                for (var i = keyColumns; i < reader.FieldCount; i++)
                {
                    if (string.Equals(reader.GetName(i), "collation_name", StringComparison.OrdinalIgnoreCase)) continue;
                    values[$"{i}:{reader.GetName(i)}"] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                result.Add(key, JsonSerializer.Serialize(values));
                if (++count % 100 == 0) progress?.Report($"{kind} 已讀取 {count} 筆");
            }
            progress?.Report($"{kind} 讀取完成：{count} 筆");
        }
        return result;
    }
}
