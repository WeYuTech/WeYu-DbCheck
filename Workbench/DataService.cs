using System.Data;
using System.Data.SqlTypes;
using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;

namespace DbCheck.Workbench;

internal enum DataFilterOperator { Equal, NotEqual, Greater, GreaterOrEqual, Less, LessOrEqual, Contains, StartsWith, IsNull, IsNotNull }
internal sealed record DataFilter(string Column, DataFilterOperator Operator, string Value);
internal sealed record DataRequest(ObjectKey Table, string[] Columns, string[] Keys, int Limit = 100, bool Descending = false, DataFilter? Filter = null);
internal sealed record DataDifference(string Key, string State, string ChangedColumns, string Current, string Standard);
internal sealed record DataResult(int ReadCount, int Missing, int Changed, int Same, IReadOnlyList<DataDifference> Differences, string Sql, IReadOnlyList<string> Warnings);
internal sealed record DataCell(object? Value, string Literal, byte[]? DatabaseBytes);
internal sealed record DataRecord(int SourceIndex, IReadOnlyList<DataCell> Cells);

/// <summary>Small reference/configuration data, not a bulk-copy or full-database data comparison engine.</summary>
internal sealed class DataService
{
    public const int MaximumRows = 10_000;
    private const int MaximumCellBytes = 1024 * 1024;
    private const int MaximumBufferedBytes = 16 * 1024 * 1024;

    public static IReadOnlyList<IndexModel> UsableKeys(TableModel table) => table.Indexes.Where(i => i.IsUnique && !i.IsDisabled && i.Filter is null && i.Type is 1 or 2
        && i.Columns.Any(c => !c.Included) && i.Columns.Where(c => !c.Included).All(k => table.Columns.Any(c => c.Name == k.Name && !c.IsNullable && c.IsWritable && Supported(c))))
        .OrderByDescending(i => i.IsPrimaryKey).ThenBy(i => i.Name, StringComparer.Ordinal).ToArray();
    public static string[] KeyColumns(IndexModel index) => index.Columns.Where(c => !c.Included).OrderBy(c => c.KeyOrdinal).Select(c => c.Name).ToArray();
    public static bool Supported(ColumnModel column) => column.SystemTypeId is not (98 or 240 or 189) && column.EncryptionType == 0;

    internal static ColumnModel[] Validate(TableModel table, DataRequest request)
    {
        if (request.Table != table.Key || request.Limit is < 1 or > MaximumRows) throw new ArgumentException($"每表筆數必須為 1～{MaximumRows}。");
        if (request.Columns is null || request.Keys is null || request.Columns.Length == 0 || request.Keys.Length == 0 || request.Columns.Length > 256) throw new ArgumentException("請選擇 1～256 個欄位及有效唯一鍵。");
        if (!UsableKeys(table).Any(i => KeyColumns(i).SequenceEqual(request.Keys, StringComparer.Ordinal))) throw new InvalidOperationException("必須使用未停用、未篩選且不可為 NULL 的主鍵／唯一索引；無此唯一鍵時不推測資料身分。");
        var names = request.Columns.Concat(request.Keys).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        if (names.Any(n => table.Columns.All(c => c.Name != n))) throw new ArgumentException("選取欄位不在讀取的 metadata 中。");
        var columns = table.Columns.Where(c => names.Contains(c.Name)).OrderBy(c => c.Ordinal).ToArray();
        if (columns.Any(c => !c.IsWritable || !Supported(c))) throw new NotSupportedException("計算、rowversion、hidden、generated、加密及 CLR／空間／sql_variant 欄位不支援匯出寫入。");
        return columns;
    }

    public async Task<DataResult> ExportAsync(string sourceConnection, TableModel table, DataRequest request, CatalogSnapshot? destination, CancellationToken token)
    {
        var columns = Validate(table, request);
        var rows = await ReadSourceAsync(sourceConnection, table, columns, request, token);
        var statements = new StringBuilder();
        foreach (var row in rows) statements.AppendLine(Insert(table.Key, columns, row));
        var sql = ScriptPlanner.Wrap(destination ?? PlaceholderTarget(), [new(table.Key, "資料匯出", RiskLevel.Review, "", IdentityScope(table.Key, columns.Any(c => c.IsIdentity), statements.ToString()))], $"標準 DB 資料匯出；{rows.Count} 筆；只取指定條件與主鍵排序的前 {request.Limit} 筆，未執行。");
        return new(rows.Count, 0, 0, 0, [], sql, ["這是資料匯出，不是兩邊資料比對；INSERT 未排除既有鍵值。", "SQL 可能包含敏感資料；不會自動保存資料內容。", destination is null ? "尚未指定已比對目標，腳本使用占位伺服器／資料庫防護；請人工確認並替換。" : "請先確認目標欄位相容性、鍵值衝突與約束。"]);
    }

    public async Task<DataResult> ReconcileAsync(string sourceConnection, string targetConnection, TableModel source, TableModel target, CatalogSnapshot destination, DataRequest request, bool includeUpdates, CancellationToken token, IProgress<string>? progress = null)
    {
        var columns = Validate(source, request);
        var targetColumns = Validate(target, request);
        // The reader order must follow the source selection even when physical ordinals differ.
        var targetLookup = targetColumns.ToDictionary(c => c.Name, StringComparer.Ordinal);
        targetColumns = columns.Select(c => targetLookup[c.Name]).ToArray();
        for (var i = 0; i < columns.Length; i++)
            if (columns[i].SqlType != targetColumns[i].SqlType || columns[i].SystemTypeId != targetColumns[i].SystemTypeId || request.Keys.Contains(columns[i].Name, StringComparer.Ordinal) && columns[i].Collation != targetColumns[i].Collation)
                throw new InvalidOperationException("資料比對要求選取欄位型別相容、鍵欄位定序相同；請先檢查 " + columns[i].Name + "。");
        var sourceRows = await ReadSourceAsync(sourceConnection, source, columns, request, token);
        var targetRows = new Dictionary<int, DataRecord>();
        var keyIndexes = request.Keys.Select(k => Array.FindIndex(columns, c => c.Name == k)).ToArray();
        await using var connection = new SqlConnection(targetConnection);
        await connection.OpenAsync(token);
        long bytes = 0;
        var batchSize = Math.Min(100, 2000 / request.Keys.Length);
        foreach (var batch in sourceRows.Chunk(batchSize))
        {
            token.ThrowIfCancellationRequested();
            progress?.Report($"依唯一鍵讀取目標：{Math.Min(sourceRows.Count, batch[^1].SourceIndex + 1)}/{sourceRows.Count}…");
            using var command = new SqlCommand { Connection = connection, CommandTimeout = 60 };
            var values = new List<string>();
            for (var r = 0; r < batch.Length; r++)
            {
                var parameters = new List<string> { batch[r].SourceIndex.ToString(CultureInfo.InvariantCulture) };
                for (var k = 0; k < keyIndexes.Length; k++)
                {
                    var parameter = "@k" + r + "_" + k; parameters.Add(parameter);
                    AddParameter(command, parameter, targetColumns[keyIndexes[k]], batch[r].Cells[keyIndexes[k]].Value);
                }
                values.Add("(" + string.Join(",", parameters) + ")");
            }
            var aliases = string.Join(",", Enumerable.Range(0, keyIndexes.Length).Select(k => "[k" + k + "]"));
            var match = string.Join(" AND ", keyIndexes.Select((index, k) => "t." + SqlText.Quote(columns[index].Name) + "=wanted.[k" + k + "]"));
            var selected = string.Join(",", targetColumns.Select(c => "t." + SqlText.Quote(c.Name) + (includeUpdates ? ",CONVERT(varbinary(max),t." + SqlText.Quote(c.Name) + ")" : "")));
            if (includeUpdates && targetColumns.Any(c => c.SystemTypeId is 34 or 35 or 99 or 241)) throw new NotSupportedException("含 text／ntext／image／XML 的資料可匯出，但本版本不產生帶並發檢查的 UPDATE。");
            command.CommandText = $"SELECT wanted.[row_number],{selected} FROM (VALUES {string.Join(",", values)}) wanted([row_number],{aliases}) JOIN {target.Key.Sql} t ON {match};";
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
            while (await reader.ReadAsync(token))
            {
                var index = reader.GetInt32(0);
                var cells = ReadCells(reader, targetColumns, 1, includeUpdates, ref bytes);
                if (!targetRows.TryAdd(index, new(index, cells))) throw new InvalidOperationException("目標唯一鍵未能唯一識別資料，已停止產生 SQL。");
            }
        }
        var differences = new List<DataDifference>(); var statements = new StringBuilder();
        var warnings = new List<string> { $"只比對標準 DB 所選條件與排序的前 {request.Limit} 筆，不代表全表一致。", "未掃描目標獨有資料，目標額外資料一律保留；鍵配對使用 SQL Server 的鍵欄位語意。", "並非跨資料庫的一致快照；請在執行腳本後重新比對。" };
        var missing = 0; var changed = 0; var same = 0;
        var selectedNames = columns.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var requiredMissing = target.Columns.Where(c => c.IsWritable && !selectedNames.Contains(c.Name) && !c.IsNullable && !c.IsIdentity && c.DefaultDefinition is null).ToArray();
        foreach (var row in sourceRows)
        {
            token.ThrowIfCancellationRequested();
            var key = string.Join(", ", keyIndexes.Select(i => columns[i].Name + "=" + row.Cells[i].Literal));
            var predicate = KeyPredicate(columns, request.Keys, row);
            if (!targetRows.TryGetValue(row.SourceIndex, out var old))
            {
                missing++;
                differences.Add(new(key, "目標缺少", "", "（不存在）", Display(columns, row)));
                statements.AppendLine($"IF NOT EXISTS (SELECT 1 FROM {target.Key.Sql} WITH (UPDLOCK,HOLDLOCK) WHERE {predicate})")
                    .AppendLine("    " + Insert(target.Key, columns, row));
                continue;
            }
            var changedIndexes = Enumerable.Range(0, columns.Length).Where(i => !request.Keys.Contains(columns[i].Name, StringComparer.Ordinal) && row.Cells[i].Literal != old.Cells[i].Literal).ToArray();
            if (changedIndexes.Length == 0) { same++; continue; }
            changed++;
            differences.Add(new(key, "內容不同", string.Join(", ", changedIndexes.Select(i => columns[i].Name)), Display(columns, old), Display(columns, row)));
            if (!includeUpdates) continue;
            if (changedIndexes.Any(i => targetColumns[i].IsIdentity)) throw new NotSupportedException("IDENTITY 欄位內容不同，不能以 UPDATE 修正。");
            var set = string.Join(", ", changedIndexes.Select(i => SqlText.Quote(columns[i].Name) + "=" + row.Cells[i].Literal));
            var guard = string.Join(" AND ", old.Cells.Select((cell, i) => cell.Value is null or DBNull ? SqlText.Quote(columns[i].Name) + " IS NULL" : $"CONVERT(varbinary(max),{SqlText.Quote(columns[i].Name)})=0x{Convert.ToHexString(cell.DatabaseBytes ?? throw new InvalidOperationException("缺少並發檢查值。"))}"));
            statements.AppendLine($"UPDATE {target.Key.Sql} SET {set} WHERE {predicate} AND {guard};")
                .AppendLine("IF @@ROWCOUNT <> 1 THROW 51002, N'資料已異動或不存在；請重新比對，勿盲目重試 UPDATE。', 1;");
        }
        if (!includeUpdates && changed > 0) warnings.Add($"{changed} 筆內容不同的資料只顯示差異，不產生 UPDATE。");
        if (includeUpdates) warnings.Add("UPDATE 使用讀取時的二進位欄位值做樂觀並發檢查，僅保護本次選取欄位；重複執行前必须重新比對。");
        warnings.Add("補缺 SQL 執行時若同鍵已存在，保留該筆而不覆寫；這不代表其內容已一致。");
        string sql;
        if (missing > 0 && requiredMissing.Length > 0)
        {
            warnings.Add("已阻擋產生腳本：目標還有未選取、不可為 NULL 且無預設值的欄位：" + string.Join(", ", requiredMissing.Select(c => c.Name)));
            sql = "";
        }
        else if (statements.Length == 0) sql = "-- 本次選取範圍沒有可產生的 INSERT／UPDATE。";
        else sql = ScriptPlanner.Wrap(destination, [new(target.Key, includeUpdates ? "補缺及選擇性 UPDATE" : "只補缺少資料", RiskLevel.Review, "", IdentityScope(target.Key, missing > 0 && targetColumns.Any(c => c.IsIdentity), statements.ToString()))], $"資料修正草稿；{missing} 筆缺少、{changed} 筆不同、{same} 筆相同；未執行。敏感內容請妥善保管。");
        return new(sourceRows.Count, missing, changed, same, differences, sql, warnings);
    }

    private static async Task<List<DataRecord>> ReadSourceAsync(string connectionString, TableModel table, ColumnModel[] columns, DataRequest request, CancellationToken token)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        using var command = new SqlCommand { Connection = connection, CommandTimeout = 60 };
        command.Parameters.Add("@Limit", SqlDbType.Int).Value = request.Limit;
        var where = BuildFilter(command, table, request.Filter);
        command.CommandText = $"SELECT TOP (@Limit) {string.Join(",", columns.Select(c => SqlText.Quote(c.Name)))} FROM {table.Key.Sql}{where} ORDER BY {string.Join(",", request.Keys.Select(k => SqlText.Quote(k) + (request.Descending ? " DESC" : " ASC")))};";
        var rows = new List<DataRecord>(); long bytes = 0;
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
        while (await reader.ReadAsync(token)) rows.Add(new(rows.Count, ReadCells(reader, columns, 0, false, ref bytes)));
        return rows;
    }

    private static IReadOnlyList<DataCell> ReadCells(SqlDataReader reader, ColumnModel[] columns, int offset, bool captureBinary, ref long bytes)
    {
        var cells = new List<DataCell>(columns.Length);
        for (var c = 0; c < columns.Length; c++)
        {
            var index = offset + c * (captureBinary ? 2 : 1);
            object? value = null;
            if (!reader.IsDBNull(index))
            {
                var fieldType = reader.GetFieldType(index);
                if (fieldType == typeof(string) && reader.GetChars(index, 0, null, 0, 0) * 2 > MaximumCellBytes || fieldType == typeof(byte[]) && reader.GetBytes(index, 0, null, 0, 0) > MaximumCellBytes) throw new InvalidOperationException("單一欄位超過 1 MiB，請排除此欄位或使用專用大量匯出工具。");
                value = columns[c].SystemTypeId is 106 or 108 ? reader.GetSqlDecimal(index) : reader.GetValue(index);
            }
            byte[]? raw = null;
            if (captureBinary && !reader.IsDBNull(index + 1))
            {
                if (reader.GetBytes(index + 1, 0, null, 0, 0) > MaximumCellBytes) throw new InvalidOperationException("並發檢查欄位過大。");
                raw = (byte[])reader.GetValue(index + 1);
            }
            var literal = Literal(value, columns[c]);
            bytes += literal.Length * 4L + (raw?.Length ?? 0);
            if (bytes > MaximumBufferedBytes) throw new InvalidOperationException("本次資料超過 16 MiB 記憶體上限；請減少欄位／筆數或縮小條件。");
            cells.Add(new(value, literal, raw));
        }
        return cells;
    }

    internal static string Literal(object? value, ColumnModel column) => value switch
    {
        null or DBNull => "NULL",
        string text => SqlText.String(text),
        DateTime date => "'" + date.ToString(column.SystemTypeId switch { 40 => "yyyy-MM-dd", 58 => "yyyy-MM-ddTHH:mm:ss", 61 => "yyyy-MM-ddTHH:mm:ss.fff", _ => "yyyy-MM-ddTHH:mm:ss.fffffff" }, CultureInfo.InvariantCulture) + "'",
        DateTimeOffset date => "'" + date.ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture) + "'",
        TimeSpan time => "'" + time.ToString(@"hh\:mm\:ss\.fffffff", CultureInfo.InvariantCulture) + "'",
        byte[] binary => "0x" + Convert.ToHexString(binary),
        bool flag => flag ? "1" : "0",
        Guid guid => "'" + guid.ToString("D") + "'",
        SqlDecimal number => number.IsNull ? "NULL" : number.ToString().Replace(CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator, "."),
        float number when float.IsFinite(number) => number.ToString("R", CultureInfo.InvariantCulture),
        double number when double.IsFinite(number) => number.ToString("R", CultureInfo.InvariantCulture),
        byte or short or int or long or decimal => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        _ => throw new NotSupportedException("無法忠實轉換此資料型別：" + value.GetType().Name)
    };

    private static string BuildFilter(SqlCommand command, TableModel table, DataFilter? filter)
    {
        if (filter is null) return "";
        if (!Enum.IsDefined(filter.Operator)) throw new ArgumentException("無效的篩選運算子。");
        var column = table.Columns.SingleOrDefault(c => c.Name == filter.Column) ?? throw new ArgumentException("篩選欄位不存在。");
        var name = SqlText.Quote(column.Name);
        if (filter.Operator == DataFilterOperator.IsNull) return " WHERE " + name + " IS NULL";
        if (filter.Operator == DataFilterOperator.IsNotNull) return " WHERE " + name + " IS NOT NULL";
        if (filter.Value.Length > 4096) throw new ArgumentException("篩選值不可超過 4096 字元。");
        object value = Parse(filter.Value, column);
        string operation;
        if (filter.Operator is DataFilterOperator.Contains or DataFilterOperator.StartsWith)
        {
            if (column.SystemTypeId is not (167 or 175 or 231 or 239)) throw new ArgumentException("包含／開頭篩選只適用於一般字串欄位。");
            var escaped = filter.Value.Replace("~", "~~").Replace("%", "~%").Replace("_", "~_").Replace("[", "~[");
            value = (filter.Operator == DataFilterOperator.Contains ? "%" : "") + escaped + "%";
            operation = "LIKE @Filter ESCAPE N'~'";
        }
        else operation = (filter.Operator switch { DataFilterOperator.Equal => "=", DataFilterOperator.NotEqual => "<>", DataFilterOperator.Greater => ">", DataFilterOperator.GreaterOrEqual => ">=", DataFilterOperator.Less => "<", DataFilterOperator.LessOrEqual => "<=", _ => throw new ArgumentException("不支援的運算子。") }) + " @Filter";
        AddParameter(command, "@Filter", column, value);
        return " WHERE " + name + " " + operation;
    }

    private static object Parse(string text, ColumnModel column) => column.SystemTypeId switch
    {
        167 or 175 or 231 or 239 or 35 or 99 or 241 => text,
        48 => byte.Parse(text, CultureInfo.InvariantCulture), 52 => short.Parse(text, CultureInfo.InvariantCulture), 56 => int.Parse(text, CultureInfo.InvariantCulture), 127 => long.Parse(text, CultureInfo.InvariantCulture),
        106 or 108 => SqlDecimal.Parse(text), 60 or 122 => decimal.Parse(text, CultureInfo.InvariantCulture),
        59 => float.Parse(text, CultureInfo.InvariantCulture), 62 => double.Parse(text, CultureInfo.InvariantCulture),
        104 => text == "1" ? true : text == "0" ? false : bool.Parse(text),
        36 => Guid.Parse(text), 40 or 42 or 58 or 61 => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces),
        43 => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces), 41 => TimeSpan.Parse(text, CultureInfo.InvariantCulture),
        165 or 173 or 34 => Convert.FromHexString(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text),
        _ => throw new NotSupportedException("此型別不支援條件篩選。")
    };

    private static void AddParameter(SqlCommand command, string name, ColumnModel column, object? value)
    {
        var type = column.SystemTypeId switch
        {
            48 => SqlDbType.TinyInt, 52 => SqlDbType.SmallInt, 56 => SqlDbType.Int, 127 => SqlDbType.BigInt,
            106 or 108 => SqlDbType.Decimal, 60 => SqlDbType.Money, 122 => SqlDbType.SmallMoney, 59 => SqlDbType.Real, 62 => SqlDbType.Float, 104 => SqlDbType.Bit, 36 => SqlDbType.UniqueIdentifier,
            40 => SqlDbType.Date, 41 => SqlDbType.Time, 42 => SqlDbType.DateTime2, 43 => SqlDbType.DateTimeOffset, 58 => SqlDbType.SmallDateTime, 61 => SqlDbType.DateTime,
            167 or 175 or 35 => SqlDbType.VarChar, 231 or 239 or 99 => SqlDbType.NVarChar, 241 => SqlDbType.Xml, 165 or 173 or 34 => SqlDbType.VarBinary,
            _ => throw new NotSupportedException("此型別不支援參數化條件。")
        };
        var parameter = command.Parameters.Add(name, type);
        parameter.Value = value ?? DBNull.Value;
        if (type == SqlDbType.Decimal) { parameter.Precision = column.Precision; parameter.Scale = column.Scale; }
        if (type is SqlDbType.Time or SqlDbType.DateTime2 or SqlDbType.DateTimeOffset) parameter.Scale = column.Scale;
        if (value is string text && type is SqlDbType.VarChar or SqlDbType.NVarChar) parameter.Size = Math.Max(1, text.Length) > 4000 ? -1 : Math.Max(1, text.Length);
        if (value is byte[] binary) parameter.Size = binary.Length > 8000 ? -1 : Math.Max(1, binary.Length);
    }

    private static string Insert(ObjectKey table, ColumnModel[] columns, DataRecord row) => $"INSERT INTO {table.Sql} ({string.Join(", ", columns.Select(c => SqlText.Quote(c.Name)))}) VALUES ({string.Join(", ", row.Cells.Select(c => c.Literal))});";
    private static string KeyPredicate(ColumnModel[] columns, string[] keys, DataRecord row) => string.Join(" AND ", keys.Select(k => { var i = Array.FindIndex(columns, c => c.Name == k); return SqlText.Quote(k) + "=" + row.Cells[i].Literal; }));
    private static string Display(ColumnModel[] columns, DataRecord row) => string.Join("\r\n", columns.Select((c, i) => c.Name + " = " + row.Cells[i].Literal));
    private static string IdentityScope(ObjectKey table, bool identity, string statements) => !identity || statements.Length == 0 ? statements : $"SET IDENTITY_INSERT {table.Sql} ON;\r\nBEGIN TRY\r\n{statements}SET IDENTITY_INSERT {table.Sql} OFF;\r\nEND TRY\r\nBEGIN CATCH\r\nSET IDENTITY_INSERT {table.Sql} OFF;\r\nTHROW;\r\nEND CATCH;";
    private static CatalogSnapshot PlaceholderTarget() => new() { Server = "__REVIEW_TARGET_SERVER__", Database = "__REVIEW_TARGET_DATABASE__" };
}
