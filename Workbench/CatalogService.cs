using System.Data;
using Microsoft.Data.SqlClient;

namespace DbCheck.Workbench;

/// <summary>Catalog-only SELECTs. No user data, DDL, transaction or connection string is persisted.</summary>
internal sealed class CatalogService
{
    private readonly int timeout;
    public CatalogService(int commandTimeout = 60)
    {
        if (commandTimeout is < 1 or > 600) throw new ArgumentOutOfRangeException(nameof(commandTimeout));
        timeout = commandTimeout;
    }

    public static async Task<string> TestAsync(string connectionString, CancellationToken token)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        using var command = new SqlCommand("SELECT CONVERT(nvarchar(128),SERVERPROPERTY('ServerName')),DB_NAME(),HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','VIEW DEFINITION');", connection) { CommandTimeout = 15 };
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token) || reader.IsDBNull(2) || reader.GetInt32(2) != 1)
            throw new InvalidOperationException("連線成功，但缺少資料庫 VIEW DEFINITION 權限，無法保證完整讀取。");
        return reader.GetString(0) + " / " + reader.GetString(1);
    }

    public async Task<CatalogSnapshot> ReadAsync(string connectionString, ComparisonOptions options, CancellationToken token, IProgress<string>? progress = null)
    {
        options.Validate();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        string server, database;
        using (var identity = new SqlCommand("SELECT CONVERT(nvarchar(128),SERVERPROPERTY('ServerName')),DB_NAME(),HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','VIEW DEFINITION');", connection) { CommandTimeout = timeout })
        await using (var reader = await identity.ExecuteReaderAsync(token))
        {
            if (!await reader.ReadAsync(token) || reader.IsDBNull(2) || reader.GetInt32(2) != 1)
                throw new InvalidOperationException("需要資料庫 VIEW DEFINITION 權限；未取得的 metadata 不能視為空資料庫。");
            server = reader.GetString(0); database = reader.GetString(1);
        }
        var snapshot = new CatalogSnapshot { Server = server, Database = database, Options = options };
        using (var schemas = new SqlCommand("SELECT name FROM sys.schemas ORDER BY name;", connection) { CommandTimeout = timeout })
        await using (var reader = await schemas.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token)) snapshot.Schemas.Add(reader.GetString(0));

        async Task ReadPart(ComparisonScope scope, Func<Task> read)
        {
            if (!options.Scope.HasFlag(scope)) return;
            token.ThrowIfCancellationRequested();
            progress?.Report($"{snapshot.Label}：讀取 {scope}…");
            try { await read(); snapshot.CompletedScopes |= scope; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is SqlException or InvalidOperationException or InvalidCastException)
            { snapshot.Issues.Add(new(scope, null, SqlText.Failure(ex))); }
        }

        SqlCommand Command(string query, bool view = false)
        {
            var command = new SqlCommand(query.Replace("/*FILTER*/", BuildFilter(options, view)), connection) { CommandTimeout = timeout };
            command.Parameters.Add("@ExcludeZZ", SqlDbType.Bit).Value = options.ExcludeZZ;
            for (var i = 0; i < options.ExcludedPrefixes.Length; i++)
                command.Parameters.Add("@Prefix" + i, SqlDbType.NVarChar, 128).Value = options.ExcludedPrefixes[i];
            return command;
        }

        await ReadPart(ComparisonScope.Tables, async () =>
        {
            var tables = new Dictionary<ObjectKey, TableModel>();
            using var command = Command(TableQuery);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var key = Key(reader);
                tables.Add(key, new TableModel { Key = key, TemporalType = Int(reader, "temporal_type"), IsMemoryOptimized = Bool(reader, "is_memory_optimized") });
            }
            await reader.NextResultAsync(token);
            while (await reader.ReadAsync(token))
            {
                if (!tables.TryGetValue(Key(reader), out var table)) continue;
                var systemType = (byte)Int(reader, "system_type_id");
                var maxLength = Int(reader, "max_length");
                table.Columns.Add(new ColumnModel
                {
                    Name = Text(reader, "column_name"), TypeSchema = Text(reader, "type_schema"), DataType = Text(reader, "data_type"), SystemTypeId = systemType,
                    Length = maxLength < 0 ? -1 : systemType is 231 or 239 ? maxLength / 2 : maxLength, MaxLengthBytes = maxLength,
                    Precision = (byte)Int(reader, "precision"), Scale = (byte)Int(reader, "scale"), IsNullable = Bool(reader, "is_nullable"), Ordinal = Int(reader, "column_id"),
                    IsIdentity = Bool(reader, "is_identity"), IdentitySeed = NullableText(reader, "identity_seed"), IdentityIncrement = NullableText(reader, "identity_increment"),
                    Collation = NullableText(reader, "collation_name"), DefaultDefinition = NullableText(reader, "default_definition"), ComputedDefinition = NullableText(reader, "computed_definition"),
                    IsPersisted = Bool(reader, "is_persisted"), IsHidden = Bool(reader, "is_hidden"), GeneratedAlwaysType = Int(reader, "generated_always_type"), IsSparse = Bool(reader, "is_sparse"),
                    IsRowGuid = Bool(reader, "is_rowguidcol"), IsColumnSet = Bool(reader, "is_column_set"), EncryptionType = Int(reader, "encryption_type"), XmlCollectionId = Int(reader, "xml_collection_id")
                });
            }
            var indexes = new Dictionary<(ObjectKey, string), IndexModel>();
            await reader.NextResultAsync(token);
            while (await reader.ReadAsync(token))
            {
                var key = Key(reader);
                if (!tables.TryGetValue(key, out var table)) continue;
                var index = new IndexModel { Name = Text(reader, "index_name"), Type = Int(reader, "type"), IsUnique = Bool(reader, "is_unique"), IsPrimaryKey = Bool(reader, "is_primary_key"), IsUniqueConstraint = Bool(reader, "is_unique_constraint"), IsDisabled = Bool(reader, "is_disabled"), Filter = NullableText(reader, "filter_definition") };
                indexes.Add((key, index.Name), index); table.Indexes.Add(index);
            }
            await reader.NextResultAsync(token);
            while (await reader.ReadAsync(token))
                if (indexes.TryGetValue((Key(reader), Text(reader, "index_name")), out var index))
                    index.Columns.Add(new(Text(reader, "column_name"), Int(reader, "key_ordinal"), Bool(reader, "is_descending_key"), Bool(reader, "is_included_column")));
            // Publish the category only after all result sets completed successfully.
            snapshot.Tables.AddRange(tables.Values.OrderBy(t => t.Key.Schema, StringComparer.Ordinal).ThenBy(t => t.Key.Name, StringComparer.Ordinal));
        });

        var tableLookup = snapshot.Tables.ToDictionary(t => t.Key);
        await ReadPart(ComparisonScope.ForeignKeys, async () =>
        {
            if (!snapshot.CompletedScopes.HasFlag(ComparisonScope.Tables)) throw new InvalidOperationException("資料表讀取失敗，未能比對外鍵。");
            var items = new Dictionary<(ObjectKey, string), ForeignKeyModel>();
            using var command = Command(ForeignKeyQuery);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var key = Key(reader); if (!tableLookup.ContainsKey(key)) continue;
                var foreignKey = new ForeignKeyModel
                {
                    Name = Text(reader, "constraint_name"), ReferencedTable = new(Text(reader, "referenced_schema"), Text(reader, "referenced_table")),
                    DeleteAction = Text(reader, "delete_referential_action_desc"), UpdateAction = Text(reader, "update_referential_action_desc"),
                    IsDisabled = Bool(reader, "is_disabled"), IsNotTrusted = Bool(reader, "is_not_trusted"), NotForReplication = Bool(reader, "is_not_for_replication")
                };
                items.Add((key, foreignKey.Name), foreignKey);
            }
            await reader.NextResultAsync(token);
            while (await reader.ReadAsync(token))
                if (items.TryGetValue((Key(reader), Text(reader, "constraint_name")), out var item))
                    item.Columns.Add(new(Text(reader, "column_name"), Text(reader, "referenced_column"), Int(reader, "constraint_column_id")));
            foreach (var pair in items) tableLookup[pair.Key.Item1].ForeignKeys.Add(pair.Value);
        });

        await ReadPart(ComparisonScope.Checks, async () =>
        {
            if (!snapshot.CompletedScopes.HasFlag(ComparisonScope.Tables)) throw new InvalidOperationException("資料表讀取失敗，未能比對 CHECK。");
            var items = new List<(ObjectKey Key, CheckModel Check)>();
            using var command = Command(CheckQuery);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                if (tableLookup.ContainsKey(Key(reader))) items.Add((Key(reader), new(Text(reader, "constraint_name"), Text(reader, "definition"), Bool(reader, "is_disabled"), Bool(reader, "is_not_trusted"), Bool(reader, "is_not_for_replication"))));
            foreach (var item in items) tableLookup[item.Key].Checks.Add(item.Check);
        });

        async Task ReadModules(ComparisonScope scope, string types)
        {
            var modules = new List<ModuleModel>();
            var issues = new List<CatalogIssue>();
            using var command = Command(ModuleQuery.Replace("/*TYPES*/", types), scope == ComparisonScope.Views);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var type = Text(reader, "type").Trim();
                var item = new ModuleModel
                {
                    Key = Key(reader), TypeCode = type,
                    Category = type == "V" ? ObjectCategory.View : type is "P" or "PC" ? ObjectCategory.Procedure : type is "TR" or "TA" ? ObjectCategory.Trigger : ObjectCategory.Function,
                    Definition = NullableText(reader, "definition"), AnsiNulls = Bool(reader, "uses_ansi_nulls"), QuotedIdentifier = Bool(reader, "uses_quoted_identifier"),
                    IsSchemaBound = Bool(reader, "is_schema_bound"), IsDisabled = Bool(reader, "is_disabled"), HasIndexes = Bool(reader, "has_indexes"),
                    Parent = NullableText(reader, "parent_name") is { } parent ? new(Text(reader, "parent_schema"), parent) : null
                };
                modules.Add(item);
                if (!item.Readable) issues.Add(new(scope, item.Key, type is "FS" or "FT" or "PC" or "TA" ? "CLR 物件不支援文字比對。" : Bool(reader, "is_encrypted") ? "物件定義已加密。" : "定義無法讀取；不能視為不存在或一致。"));
            }
            snapshot.Modules.AddRange(modules); snapshot.Issues.AddRange(issues);
        }
        await ReadPart(ComparisonScope.Views, () => ReadModules(ComparisonScope.Views, "'V'"));
        await ReadPart(ComparisonScope.Routines, () => ReadModules(ComparisonScope.Routines, "'FN','IF','TF','P','FS','FT','PC'"));
        await ReadPart(ComparisonScope.Triggers, () => ReadModules(ComparisonScope.Triggers, "'TR','TA'"));

        try
        {
            using var command = new SqlCommand(DependencyQuery, connection) { CommandTimeout = timeout };
            await using var reader = await command.ExecuteReaderAsync(token);
            var dependencies = new List<DependencyModel>();
            while (await reader.ReadAsync(token))
            {
                var referencedSchema = NullableText(reader, "referenced_schema");
                var referencedName = Text(reader, "referenced_entity_name");
                dependencies.Add(new(new(Text(reader, "owner_schema"), Text(reader, "owner_name")), referencedSchema is null ? null : new(referencedSchema, referencedName),
                    NullableText(reader, "referenced_server_name"), NullableText(reader, "referenced_database_name"), referencedName,
                    Bool(reader, "resolved"), Bool(reader, "is_schema_bound_reference")));
            }
            snapshot.Dependencies.AddRange(dependencies); snapshot.DependenciesRead = true;
        }
        catch (OperationCanceledException) { throw; }
        catch (SqlException ex) { snapshot.Issues.Add(new(ComparisonScope.None, null, "相依資訊未完整讀取。" + SqlText.Failure(ex))); }
        progress?.Report($"{snapshot.Label}：{snapshot.Tables.Count} 個資料表、{snapshot.Modules.Count} 個程式物件；{snapshot.Issues.Count} 個問題。");
        return snapshot;
    }

    private static string BuildFilter(ComparisonOptions options, bool view)
    {
        // Names remain metadata, prefixes remain parameters. No SQL expression is accepted from the user.
        var filter = $" AND (@ExcludeZZ=0 OR LEFT(t.name,{(view ? 4 : 2)}) COLLATE Latin1_General_100_CI_AS <> N'{(view ? "V_ZZ" : "ZZ")}')";
        for (var i = 0; i < options.ExcludedPrefixes.Length; i++)
            if (options.ExcludedPrefixes[i].Length > 0) filter += $" AND LEFT(t.name,LEN(@Prefix{i})) COLLATE Latin1_General_100_CI_AS <> @Prefix{i} COLLATE Latin1_General_100_CI_AS";
        return filter;
    }

    private static string Text(SqlDataReader reader, string column) => reader.GetString(reader.GetOrdinal(column));
    private static string? NullableText(SqlDataReader reader, string column) => reader.IsDBNull(reader.GetOrdinal(column)) ? null : Text(reader, column);
    private static int Int(SqlDataReader reader, string column) => reader.IsDBNull(reader.GetOrdinal(column)) ? 0 : Convert.ToInt32(reader[column]);
    private static bool Bool(SqlDataReader reader, string column) => !reader.IsDBNull(reader.GetOrdinal(column)) && Convert.ToBoolean(reader[column]);
    private static ObjectKey Key(SqlDataReader reader) => new(Text(reader, "schema_name"), Text(reader, "object_name"));

    private const string TableQuery = """
        SELECT s.name AS schema_name,t.name AS object_name,t.temporal_type,t.is_memory_optimized
        FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE t.is_ms_shipped=0 /*FILTER*/ ORDER BY s.name,t.name;
        SELECT s.name AS schema_name,t.name AS object_name,c.name AS column_name,ts.name AS type_schema,ty.name AS data_type,
            c.system_type_id,c.max_length,c.precision,c.scale,c.is_nullable,c.column_id,c.is_identity,
            CONVERT(nvarchar(100),ic.seed_value) AS identity_seed,CONVERT(nvarchar(100),ic.increment_value) AS identity_increment,
            c.collation_name,dc.definition AS default_definition,cc.definition AS computed_definition,cc.is_persisted,
            c.is_hidden,c.generated_always_type,c.is_sparse,c.is_rowguidcol,c.is_column_set,c.encryption_type,c.xml_collection_id
        FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id JOIN sys.columns c ON c.object_id=t.object_id
        JOIN sys.types ty ON ty.user_type_id=c.user_type_id JOIN sys.schemas ts ON ts.schema_id=ty.schema_id
        LEFT JOIN sys.identity_columns ic ON ic.object_id=c.object_id AND ic.column_id=c.column_id
        LEFT JOIN sys.default_constraints dc ON dc.object_id=c.default_object_id
        LEFT JOIN sys.computed_columns cc ON cc.object_id=c.object_id AND cc.column_id=c.column_id
        WHERE t.is_ms_shipped=0 /*FILTER*/ ORDER BY s.name,t.name,c.column_id;
        SELECT s.name AS schema_name,t.name AS object_name,i.name AS index_name,i.type,i.is_unique,i.is_primary_key,i.is_unique_constraint,i.is_disabled,i.filter_definition
        FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id JOIN sys.indexes i ON i.object_id=t.object_id
        WHERE t.is_ms_shipped=0 AND i.index_id>0 AND i.is_hypothetical=0 /*FILTER*/ ORDER BY s.name,t.name,i.name;
        SELECT s.name AS schema_name,t.name AS object_name,i.name AS index_name,c.name AS column_name,ic.key_ordinal,ic.is_descending_key,ic.is_included_column
        FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id JOIN sys.indexes i ON i.object_id=t.object_id
        JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
        JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
        WHERE t.is_ms_shipped=0 AND i.index_id>0 AND i.is_hypothetical=0 /*FILTER*/ ORDER BY s.name,t.name,i.name,ic.index_column_id;
        """;
    private const string ForeignKeyQuery = """
        SELECT s.name AS schema_name,t.name AS object_name,f.name AS constraint_name,rs.name AS referenced_schema,rt.name AS referenced_table,
            f.delete_referential_action_desc,f.update_referential_action_desc,f.is_disabled,f.is_not_trusted,f.is_not_for_replication
        FROM sys.foreign_keys f JOIN sys.tables t ON t.object_id=f.parent_object_id JOIN sys.schemas s ON s.schema_id=t.schema_id
        JOIN sys.tables rt ON rt.object_id=f.referenced_object_id JOIN sys.schemas rs ON rs.schema_id=rt.schema_id
        WHERE t.is_ms_shipped=0 /*FILTER*/ ORDER BY s.name,t.name,f.name;
        SELECT s.name AS schema_name,t.name AS object_name,f.name AS constraint_name,pc.name AS column_name,rc.name AS referenced_column,fc.constraint_column_id
        FROM sys.foreign_keys f JOIN sys.tables t ON t.object_id=f.parent_object_id JOIN sys.schemas s ON s.schema_id=t.schema_id
        JOIN sys.foreign_key_columns fc ON fc.constraint_object_id=f.object_id
        JOIN sys.columns pc ON pc.object_id=fc.parent_object_id AND pc.column_id=fc.parent_column_id
        JOIN sys.columns rc ON rc.object_id=fc.referenced_object_id AND rc.column_id=fc.referenced_column_id
        WHERE t.is_ms_shipped=0 /*FILTER*/ ORDER BY s.name,t.name,f.name,fc.constraint_column_id;
        """;
    private const string CheckQuery = """
        SELECT s.name AS schema_name,t.name AS object_name,ck.name AS constraint_name,ck.definition,ck.is_disabled,ck.is_not_trusted,ck.is_not_for_replication
        FROM sys.check_constraints ck JOIN sys.tables t ON t.object_id=ck.parent_object_id JOIN sys.schemas s ON s.schema_id=t.schema_id
        WHERE t.is_ms_shipped=0 /*FILTER*/ ORDER BY s.name,t.name,ck.name;
        """;
    private const string ModuleQuery = """
        SELECT s.name AS schema_name,t.name AS object_name,t.type,m.definition,m.uses_ansi_nulls,m.uses_quoted_identifier,m.is_schema_bound,
            tr.is_disabled,OBJECTPROPERTY(t.object_id,'IsEncrypted') AS is_encrypted,
            CAST(CASE WHEN EXISTS(SELECT 1 FROM sys.indexes i WHERE i.object_id=t.object_id AND i.index_id>0) THEN 1 ELSE 0 END AS bit) AS has_indexes,
            ps.name AS parent_schema,p.name AS parent_name
        FROM sys.objects t JOIN sys.schemas s ON s.schema_id=t.schema_id
        LEFT JOIN sys.sql_modules m ON m.object_id=t.object_id LEFT JOIN sys.triggers tr ON tr.object_id=t.object_id
        LEFT JOIN sys.objects p ON p.object_id=t.parent_object_id LEFT JOIN sys.schemas ps ON ps.schema_id=p.schema_id
        WHERE t.is_ms_shipped=0 AND t.type IN (/*TYPES*/) /*FILTER*/ ORDER BY s.name,t.name;
        """;
    private const string DependencyQuery = """
        SELECT DISTINCT os.name AS owner_schema,o.name AS owner_name,
            COALESCE(rs.name,d.referenced_schema_name) AS referenced_schema,d.referenced_entity_name,d.referenced_server_name,d.referenced_database_name,
            CAST(CASE WHEN d.referenced_id IS NOT NULL THEN 1 ELSE 0 END AS bit) AS resolved,d.is_schema_bound_reference
        FROM sys.sql_expression_dependencies d JOIN sys.objects o ON o.object_id=d.referencing_id
        JOIN sys.schemas os ON os.schema_id=o.schema_id LEFT JOIN sys.objects r ON r.object_id=d.referenced_id LEFT JOIN sys.schemas rs ON rs.schema_id=r.schema_id
        WHERE o.is_ms_shipped=0 AND d.referenced_entity_name IS NOT NULL
        ORDER BY os.name,o.name,d.referenced_entity_name;
        """;
}
