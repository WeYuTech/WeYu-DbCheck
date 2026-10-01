using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;

internal static class TableSqlGenerator
{
    private static string Q(string value) => "[" + value.Replace("]", "]]") + "]";
    private static string S(DataRow row, string key) => row.Table.Columns.Contains(key) && row[key] is not DBNull ? Convert.ToString(row[key], CultureInfo.InvariantCulture)! : "";
    private static bool B(DataRow row, string key) => S(row, key) == "True";
    private static bool Different(DataRow a, DataRow b, params string[] keys) => keys.Any(k => S(a, k) != S(b, k));

    internal static string Generate(DataTable standard, DataTable actual, DataTableSchemaReport.TableDifference table)
    {
        DataRow[] Rows(DataTable data) => data.AsEnumerable().Where(r => S(r,"SchemaName") == table.Schema && S(r,"TableName") == table.Name)
            .OrderBy(r => int.TryParse(S(r,"ColumnOrdinal"),out var n) ? n : 0).ToArray();
        var rows = Rows(standard); var target = Rows(actual);
        if (rows.Length == 0) throw new ArgumentException("標準資料表不存在。");
        var full = Q(table.Schema) + "." + Q(table.Name);
        var notes = new List<string>();
        var changes = new StringBuilder();
        var lookup = target.ToDictionary(r => S(r,"ColumnName"), StringComparer.Ordinal);
        var changedColumns = new HashSet<string>(StringComparer.Ordinal);
        if (target.Length == 0)
        {
            notes.Add("目標 schema 必須存在；CREATE TABLE 僅含已讀取的欄位與索引，未含外鍵、CHECK、觸發器及儲存配置。");
            changes.AppendLine($"CREATE TABLE {full} (")
                .AppendLine(string.Join(",\r\n", rows.Select(r => "    " + Column(r, true))))
                .AppendLine(");");
        }
        else foreach (var row in rows)
        {
            var name = S(row,"ColumnName");
            if (!lookup.TryGetValue(name, out var existing))
            {
                if (!B(row,"IsNullable") && S(row,"DefaultDefinition")=="" && !B(row,"IsIdentity") && S(row,"ComputedDefinition")=="")
                    notes.Add($"{Q(name)} 新增為 NOT NULL；目標若已有資料，必須先決定回填值再執行。");
                changes.AppendLine($"ALTER TABLE {full} ADD {Column(row,true)};");
                continue;
            }
            if (Different(row,existing,"IsIdentity","IdentitySeed","IdentityIncrement","ComputedDefinition","IsPersisted"))
            {
                notes.Add($"{Q(name)} 的 IDENTITY／計算欄位設定不同：需人工重建，未產生欄位修改 SQL。");
                continue;
            }
            if (Different(row,existing,"DataType","TypeSchema","Length","Precision","Scale","IsNullable"))
            {
                if (B(row,"IsIdentity") || S(row,"ComputedDefinition")!="" || S(row,"DataType") is "timestamp" or "rowversion")
                    notes.Add($"{Q(name)} 為特殊欄位，型別修改需人工處理。");
                else
                {
                    changedColumns.Add(name);
                    changes.AppendLine($"ALTER TABLE {full} ALTER COLUMN {Column(row,false)};");
                }
            }
            if (Different(row,existing,"DefaultDefinition"))
            {
                var columnLiteral = name.Replace("'", "''");
                var tableLiteral = full.Replace("'", "''");
                changes.AppendLine($"DECLARE @df{Array.IndexOf(rows,row)} sysname;")
                    .AppendLine($"SELECT @df{Array.IndexOf(rows,row)}=d.name FROM sys.default_constraints d JOIN sys.columns c ON c.object_id=d.parent_object_id AND c.column_id=d.parent_column_id WHERE d.parent_object_id=OBJECT_ID(N'{tableLiteral}') AND c.name=N'{columnLiteral}';")
                    .AppendLine($"IF @df{Array.IndexOf(rows,row)} IS NOT NULL EXEC(N'ALTER TABLE {tableLiteral} DROP CONSTRAINT '+QUOTENAME(@df{Array.IndexOf(rows,row)}));");
                if (S(row,"DefaultDefinition")!="") changes.AppendLine($"ALTER TABLE {full} ADD DEFAULT {S(row,"DefaultDefinition")} FOR {Q(name)};");
            }
        }
        var before = new StringBuilder(); var after = new StringBuilder();
        var sourceIndexes = Indexes(rows[0]);
        var targetIndexes = target.Length == 0 ? new Dictionary<string,JsonElement>(StringComparer.Ordinal) : Indexes(target[0]);
        var matchedIndexes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, index) in sourceIndexes)
        {
            var equivalent = targetIndexes.FirstOrDefault(p => !matchedIndexes.Contains(p.Key) && DataTableSchemaReport.IndexSignature(p.Value) == DataTableSchemaReport.IndexSignature(index));
            var actualName = equivalent.Key ?? name;
            var exists = targetIndexes.TryGetValue(actualName,out var old) && !matchedIndexes.Contains(actualName);
            if (exists) matchedIndexes.Add(actualName);
            var depends = index.GetProperty("columns").EnumerateArray().Any(c => changedColumns.Contains(c.GetProperty("name").GetString()!));
            if (exists && DataTableSchemaReport.IndexSignature(index)==DataTableSchemaReport.IndexSignature(old) && !depends) continue;
            if (index.GetProperty("type").GetInt32() is not (1 or 2) || (exists && old.GetProperty("type").GetInt32() is not (1 or 2)))
            { notes.Add($"索引 {Q(name)} 非一般 rowstore 索引，需人工產生 DDL。"); continue; }
            bool Flag(JsonElement e,string p) => e.TryGetProperty(p,out var v) && v.GetBoolean();
            if (exists) before.AppendLine(Flag(old,"is_primary_key") || Flag(old,"is_unique_constraint")
                ? $"ALTER TABLE {full} DROP CONSTRAINT {Q(actualName)};" : $"DROP INDEX {Q(actualName)} ON {full};");
            var cols = index.GetProperty("columns").EnumerateArray().ToArray();
            var keys = string.Join(", ",cols.Where(c => !Flag(c,"is_included_column")).OrderBy(c=>c.GetProperty("key_ordinal").GetInt32())
                .Select(c=> Q(c.GetProperty("name").GetString()!) + (Flag(c,"is_descending_key") ? " DESC" : " ASC")));
            var included = string.Join(", ",cols.Where(c=>Flag(c,"is_included_column")).Select(c=>Q(c.GetProperty("name").GetString()!)));
            var clustered = index.GetProperty("type").GetInt32()==1 ? "CLUSTERED" : "NONCLUSTERED";
            if (Flag(index,"is_primary_key") || Flag(index,"is_unique_constraint"))
                after.AppendLine($"ALTER TABLE {full} ADD CONSTRAINT {Q(name)} {(Flag(index,"is_primary_key") ? "PRIMARY KEY" : "UNIQUE")} {clustered} ({keys});");
            else after.AppendLine($"CREATE {(Flag(index,"is_unique") ? "UNIQUE " : "")}{clustered} INDEX {Q(name)} ON {full} ({keys}){(included.Length>0 ? " INCLUDE ("+included+")" : "")}{(index.TryGetProperty("filter_definition",out var filter) ? " WHERE "+filter.GetString() : "")};");
            if (Flag(index,"is_disabled")) after.AppendLine($"ALTER INDEX {Q(name)} ON {full} DISABLE;");
        }
        if (!rows[0].Table.Columns.Contains("IndexDefinitions")) notes.Add("缺少完整索引 metadata，未產生索引 DDL；請重新比對。");
        foreach(var index in targetIndexes.Keys.Except(matchedIndexes)) notes.Add($"目標獨有索引 {Q(index)} 保留；若依賴修改欄位，需人工處理。");
        var result = new StringBuilder().AppendLine($"-- 標準：{standard.TableName}；目標：{actual.TableName}；TABLE {full}")
            .AppendLine("-- SQL 預覽草稿：未執行。請在正確目標資料庫檢閱執行。")
            .AppendLine("-- 請確認現有資料可轉型、縮短長度與 NOT NULL；外鍵/索引/其他依賴可能阻止 DDL。")
            .AppendLine("-- 僅涵蓋已讀取的欄位與一般索引；不移除目標獨有欄位。索引重建不保留額外儲存選項。");
        foreach (var note in notes) result.AppendLine("-- 待確認：" + note.Replace("\r"," ").Replace("\n"," "));
        result.AppendLine().Append(before).Append(changes).Append(after);
        if (before.Length+changes.Length+after.Length==0) result.AppendLine("-- 無可自動產生的修改語句，請參考差異與上述待確認事項。");
        return result.ToString();
    }

    private static Dictionary<string,JsonElement> Indexes(DataRow row)
    {
        var json=S(row,"IndexDefinitions");
        if(json.Length==0) return new(StringComparer.Ordinal);
        using var doc=JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().ToDictionary(i=>i.GetProperty("name").GetString()!,i=>i.Clone(),StringComparer.Ordinal);
    }
    private static string Column(DataRow row,bool create)
    {
        var name=Q(S(row,"ColumnName"));
        if(S(row,"ComputedDefinition")!="") return name+" AS "+S(row,"ComputedDefinition")+(B(row,"IsPersisted")?" PERSISTED":"");
        var type=S(row,"DataType"); var schema=S(row,"TypeSchema");
        var sqlType=schema is not ("" or "sys") ? Q(schema)+"."+Q(type) : Q(type);
        if(schema is "" or "sys") sqlType+=type switch {
            "varchar" or "nvarchar" or "char" or "nchar" or "binary" or "varbinary" => "("+(S(row,"Length")=="-1"?"MAX":S(row,"Length"))+")",
            "decimal" or "numeric" => "("+S(row,"Precision")+","+S(row,"Scale")+")",
            "datetime2" or "datetimeoffset" or "time" => "("+S(row,"Scale")+")",
            "float" => "("+S(row,"Precision")+")", _=>"" };
        if(create&&B(row,"IsIdentity")) sqlType+=$" IDENTITY({S(row,"IdentitySeed")},{S(row,"IdentityIncrement")})";
        return name+" "+sqlType+(B(row,"IsNullable")?" NULL":" NOT NULL")+(create&&S(row,"DefaultDefinition")!=""?" DEFAULT "+S(row,"DefaultDefinition"):"");
    }
}
