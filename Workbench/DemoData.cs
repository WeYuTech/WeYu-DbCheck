namespace DbCheck.Workbench;

/// <summary>Synthetic fixtures only. No connection, customer schema, credentials or production data.</summary>
internal static class DemoData
{
    public static ComparisonSession CreateSession(ComparisonOptions? options = null, bool includeUnreadable = true)
    {
        options ??= new ComparisonOptions();
        options.Validate();
        var reportKey = new ObjectKey("dbo", "ProductionReport");
        var sourceReport = new TableModel
        {
            Key = reportKey,
            Columns = [Id(), Text("WorkOrder", 40, 2, false), new() { Name = "Quantity", DataType = "decimal", SystemTypeId = 106, Length = 9, MaxLengthBytes = 9, Precision = 18, Scale = 4, Ordinal = 3 }, Text("Remark", 200, 4), new() { Name = "ReportTime", DataType = "datetime2", SystemTypeId = 42, Length = 8, MaxLengthBytes = 8, Precision = 27, Scale = 7, IsNullable = true, Ordinal = 5 }],
            Indexes = [Primary("PK_ProductionReport"), new() { Name = "IX_ProductionReport_Order", Type = 2, Columns = [new("WorkOrder", 1, false, false), new("Quantity", 0, false, true)] }]
        };
        var targetReport = sourceReport with
        {
            Columns = [Id(), Text("WorkOrder", 40, 2, false), sourceReport.Columns[2] with { Scale = 2 }, Text("Remark", 100, 4)],
            Indexes = [Primary("PK_Report_Id")]
        };
        var workOrder = new TableModel { Key = new("dbo", "WorkOrder"), Columns = [Id(), Text("OrderNo", 40, 2, false)], Indexes = [Primary("PK_WorkOrder")] };
        var targetWorkOrder = workOrder with { Columns = [.. workOrder.Columns, Text("PlantNote", 200, 3)] };
        var view = new ModuleModel
        {
            Key = new("dbo", "V_OrderSummary"), Category = ObjectCategory.View, TypeCode = "V", AnsiNulls = true, QuotedIdentifier = true,
            Definition = """
                CREATE VIEW [dbo].[V_OrderSummary]
                AS
                SELECT
                    WorkOrder,
                    SUM(Quantity) AS TotalQuantity,
                    COUNT_BIG(*) AS ReportCount,
                    MAX(ReportTime) AS LastReportTime
                FROM dbo.ProductionReport
                WHERE Quantity > 0
                GROUP BY WorkOrder;
                """
        };
        var targetView = view with { Definition = """
            ALTER VIEW [dbo].[V_OrderSummary]
            AS
            SELECT
                WorkOrder,
                SUM(Quantity) AS TotalQuantity
            FROM dbo.ProductionReport
            GROUP BY WorkOrder;
            """ };
        var procedure = new ModuleModel
        {
            Key = new("dbo", "usp_GetPendingReports"), Category = ObjectCategory.Procedure, TypeCode = "P", AnsiNulls = true, QuotedIdentifier = true,
            Definition = """
                CREATE PROCEDURE dbo.usp_GetPendingReports
                    @WorkOrder nvarchar(40)
                AS
                BEGIN
                    SET NOCOUNT ON;
                    SELECT Id, WorkOrder, Quantity, ReportTime
                    FROM dbo.ProductionReport
                    WHERE WorkOrder = @WorkOrder
                    ORDER BY Id;
                END;
                """
        };
        var function = new ModuleModel { Key = new("dbo", "fn_ReportLabel"), Category = ObjectCategory.Function, TypeCode = "FN", AnsiNulls = true, QuotedIdentifier = true, Definition = "CREATE FUNCTION dbo.fn_ReportLabel(@Id int) RETURNS nvarchar(100) AS BEGIN RETURN N'Report-' + CONVERT(nvarchar(20), @Id); END;" };
        var targetFunction = function with { Definition = "ALTER FUNCTION dbo.fn_ReportLabel(@Id int) RETURNS nvarchar(50) AS BEGIN RETURN N'Report-' + CONVERT(nvarchar(20), @Id); END;" };
        var unreadable = new ModuleModel { Key = new("dbo", "usp_LegacyEncrypted"), Category = ObjectCategory.Procedure, TypeCode = "P", Definition = null };
        var sourceModules = new List<ModuleModel> { view, procedure, function };
        var targetModules = new List<ModuleModel> { targetView, targetFunction };
        if (includeUnreadable) { sourceModules.Add(unreadable); targetModules.Add(unreadable); }
        CatalogSnapshot Snapshot(bool standard)
        {
            var modules = (standard ? sourceModules : targetModules).Where(m => options.Scope.HasFlag(ComparisonEngine.ScopeOf(m.Category)) && options.Includes(m.Key, m.Category)).ToList();
            var tables = options.Scope.HasFlag(ComparisonScope.Tables) ? (standard ? new[] { sourceReport, workOrder } : new[] { targetReport, targetWorkOrder }).Where(t => options.Includes(t.Key, ObjectCategory.Table)).ToList() : [];
            return new()
            {
                Server = standard ? "DEMO-STANDARD" : "DEMO-CHECK", Database = standard ? "WeYu_Standard" : "WeYu_Check",
                CapturedAt = new DateTimeOffset(2026, 10, 1, 17, 40, 0, TimeSpan.FromHours(8)), Options = options, CompletedScopes = options.Scope, DependenciesRead = true,
                Schemas = ["dbo", "sys"], Tables = tables, Modules = modules,
                Dependencies = [new(view.Key, reportKey, null, null, reportKey.Name, true, false), new(procedure.Key, reportKey, null, null, reportKey.Name, true, false)],
                Issues = modules.Where(m => !m.Readable).Select(m => new CatalogIssue(ComparisonEngine.ScopeOf(m.Category), m.Key, "離線示範：加密定義不可讀取。" )).ToList()
            };
        }
        var source = Snapshot(true); var target = Snapshot(false);
        return new(source, target, ComparisonEngine.Compare(source, target, options));
    }
    public static ColumnModel Id() => new() { Name = "Id", DataType = "int", SystemTypeId = 56, Length = 4, MaxLengthBytes = 4, Precision = 10, Ordinal = 1, IsIdentity = true, IdentitySeed = "1", IdentityIncrement = "1" };
    public static ColumnModel Text(string name, int length, int ordinal, bool nullable = true) => new() { Name = name, DataType = "nvarchar", SystemTypeId = 231, Length = length, MaxLengthBytes = length * 2, IsNullable = nullable, Ordinal = ordinal, Collation = "Latin1_General_100_CI_AS" };
    public static IndexModel Primary(string name) => new() { Name = name, Type = 1, IsUnique = true, IsPrimaryKey = true, Columns = [new("Id", 1, false, false)] };
}
