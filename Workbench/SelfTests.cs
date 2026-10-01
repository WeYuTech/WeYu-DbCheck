using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace DbCheck.Workbench;

/// <summary>No testing package and no database connection. Failures produce a nonzero process exit code.</summary>
internal static class SelfTests
{
    private static void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    public static int Run()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("diff: equal and empty", () => { Assert(DiffEngine.Compute("", "").Rows.Count == 0); Assert(DiffEngine.Compute("a\nb", "a\nb").Rows.All(r => r.Kind == DiffKind.Equal)); }),
            ("diff: additions and deletion line numbers", () =>
            {
                var document = DiffEngine.Compute("a\nc", "a\nb\nc");
                Assert(document.Rows.Count(r => r.Kind == DiffKind.Added) == 1);
                Assert(document.Rows.Single(r => r.Kind == DiffKind.Added).RightNumber == 2);
                Assert(DiffEngine.Compute("a\nb", "a").Rows.Any(r => r.Kind == DiffKind.Removed));
            }),
            ("diff: word highlighting", () =>
            {
                var row = DiffEngine.Compute("Remark nvarchar(100) NULL", "Remark nvarchar(200) NULL").Rows.Single();
                Assert(row.Kind == DiffKind.Modified && row.LeftHighlights.Count > 0 && row.RightHighlights.Count > 0);
                Assert(row.LeftHighlights.Any(s => row.Left.Substring(s.Start, s.Length).Contains("100", StringComparison.Ordinal)));
            }),
            ("diff: CRLF normalization and Unicode", () => Assert(DiffEngine.Compute("報工\r\n資料 😀", "報工\n資料 😀").Rows.All(r => !r.Changed))),
            ("diff: lossless randomized row reconstruction", RandomDiff),
            ("diff: large range is bounded without losing displayed lines", () =>
            {
                var before = string.Join('\n', Enumerable.Range(0, 2400).Select(i => "old " + i));
                var after = string.Join('\n', Enumerable.Range(0, 2400).Select(i => "new " + i));
                var result = DiffEngine.Compute(before, after);
                Assert(result.Simplified && !result.Truncated); CheckReconstruction(before, after, result);
            }),
            ("diff: preview truncation is explicit", () => Assert(DiffEngine.Compute(new string('x', 2_000_001), "").Truncated)),
            ("diff: cancellation", () => { using var stop = new CancellationTokenSource(); stop.Cancel(); Throws<OperationCanceledException>(() => DiffEngine.Compute("a", "b", stop.Token)); }),
            ("diff: fold expansion and unified view", () =>
            {
                var result = DiffEngine.Compute(string.Join('\n', Enumerable.Range(0, 40)), string.Join('\n', Enumerable.Range(0, 40).Select(i => i == 20 ? "modified" : i.ToString())));
                var folded = DiffEngine.Collapse(result.Rows); Assert(folded.Any(r => r.Kind == DiffKind.Fold));
                var expanded = DiffEngine.Collapse(result.Rows, 3, folded.Where(r => r.Kind == DiffKind.Fold).Select(r => r.OriginalIndex).ToHashSet());
                Assert(expanded.Count == result.Rows.Count); Assert(DiffEngine.Unified(result.Rows).Any(r => r.Kind == DiffKind.Added));
            }),
            ("comparison: unreadable is never equal", () =>
            {
                var session = DemoData.CreateSession(); Assert(!session.Report.Complete); Assert(session.Report.Differences.Any(d => d.State == DifferenceState.Unverifiable));
                Assert(!ComparisonEngine.Compare(session.Source, session.Source, session.Source.Options).Complete);
            }),
            ("comparison: stable source equals itself", () =>
            {
                var source = DemoData.CreateSession(includeUnreadable: false).Source; var report = ComparisonEngine.Compare(source, source, source.Options);
                Assert(report.Complete && report.RequiredCount == 0 && report.Differences.Count == 0);
            }),
            ("comparison: index difference is counted once", () =>
            {
                var session = DemoData.CreateSession(includeUnreadable: false);
                var report = session.Report.Differences.Single(d => d.Key.Name == "ProductionReport");
                Assert(report.Properties.Count(p => p.Subject.StartsWith("索引 ", StringComparison.Ordinal)) == 1);
                Assert(!report.Properties.Any(p => p.Subject.StartsWith("欄位 ", StringComparison.Ordinal) && p.Property.Contains("Index", StringComparison.Ordinal)));
            }),
            ("comparison: equivalent index names and include order", () =>
            {
                var a = new IndexModel { Name = "A", Type = 2, Columns = [new("Id", 1, false, false), new("X", 0, false, true), new("Y", 0, false, true)] };
                var b = a with { Name = "B", Columns = [new("Id", 1, false, false), new("Y", 0, false, true), new("X", 0, false, true)] };
                Assert(a.Signature == b.Signature);
            }),
            ("comparison: target extras are retained", () =>
            {
                var session = DemoData.CreateSession(includeUnreadable: false); var extra = session.Report.Differences.Single(d => d.Key.Name == "WorkOrder");
                Assert(extra.State == DifferenceState.Retained && !extra.CanSelect && extra.Properties.All(p => p.Retained));
            }),
            ("comparison: failed scope never reports missing tables", () =>
            {
                var session = DemoData.CreateSession(includeUnreadable: false); var target = session.Target with { CompletedScopes = ComparisonScope.None, Tables = [], Modules = [] };
                var report = ComparisonEngine.Compare(session.Source, target, session.Source.Options);
                Assert(!report.Complete && !report.Differences.Any(d => d.State == DifferenceState.Missing));
            }),
            ("comparison: mismatched snapshot filters are incomplete", () =>
            {
                var session = DemoData.CreateSession(includeUnreadable: false);
                var source = session.Source with { Options = session.Source.Options with { ExcludeZZ = true } };
                var report = ComparisonEngine.Compare(source, session.Target, session.Target.Options);
                Assert(!report.Complete && report.RequiredCount == 0);
            }),
            ("comparison: explicit table-only scope", () =>
            {
                var options = new ComparisonOptions { Scope = ComparisonScope.Tables };
                var session = DemoData.CreateSession(options); Assert(session.Report.Complete && session.Report.Differences.All(d => d.Category == ObjectCategory.Table));
            }),
            ("comparison: cancellation", () =>
            {
                using var stop = new CancellationTokenSource(); stop.Cancel(); var session = DemoData.CreateSession();
                Throws<OperationCanceledException>(() => ComparisonEngine.Compare(session.Source, session.Target, session.Report.Options, stop.Token));
            }),
            ("options: scope and prefix validation", () =>
            {
                Throws<ArgumentException>(() => new ComparisonOptions { Scope = ComparisonScope.None }.Validate());
                Throws<ArgumentException>(() => new ComparisonOptions { Scope = ComparisonScope.ForeignKeys }.Validate());
                var options = new ComparisonOptions { ExcludeZZ = true };
                Assert(!options.Includes(new("dbo", "v_zz_backup"), ObjectCategory.View));
                Assert(options.Includes(new("dbo", "ZZ_NotViewPrefix"), ObjectCategory.View));
            }),
            ("module: CREATE and ALTER header normalization", () =>
            {
                var a = new ModuleModel { Key = new("dbo", "P"), Category = ObjectCategory.Procedure, TypeCode = "P", Definition = "CREATE PROC dbo.P AS SELECT 1;" };
                var b = a with { Definition = "ALTER PROCEDURE [dbo].[P] AS SELECT 1;" };
                Assert(ModuleText.Comparable(a) == ModuleText.Comparable(b)); Assert(ModuleText.Ddl(b, false).StartsWith("CREATE PROCEDURE [dbo].[P]", StringComparison.Ordinal));
            }),
            ("module: whitespace inside a string remains significant", () =>
            {
                var a = new ModuleModel { Key = new("dbo", "P"), Category = ObjectCategory.Procedure, TypeCode = "P", Definition = "CREATE PROC dbo.P AS SELECT N'a  b';" };
                Assert(ModuleText.Comparable(a) != ModuleText.Comparable(a with { Definition = "CREATE PROC dbo.P AS SELECT N'a b';" }));
            }),
            ("SQL: identifier and literal escaping", () =>
            {
                Assert(SqlText.Quote("x]y") == "[x]]y]"); Assert(SqlText.String("a'b") == "N'a''b'");
                Assert(new ObjectKey("a]", "t'").Sql == "[a]]].[t']");
            }),
            ("plan: guarded transaction and single outer batch", () =>
            {
                var session = DemoData.CreateSession(includeUnreadable: false); var selected = session.Report.Differences.Where(d => d.Category == ObjectCategory.View).ToArray();
                var plan = ScriptPlanner.Build(session, selected); Assert(!plan.Blocked); Assert(plan.Sql.Contains("IF @@TRANCOUNT <> 0", StringComparison.Ordinal));
                Assert(plan.Sql.Contains("DB_NAME()", StringComparison.Ordinal) && plan.Sql.Contains("SERVERPROPERTY", StringComparison.Ordinal));
                Assert(plan.Sql.Contains("ROLLBACK TRANSACTION", StringComparison.Ordinal)); Assert(plan.Sql.Contains("EXEC sys.sp_executesql N'", StringComparison.Ordinal));
                Assert(!Regex.IsMatch(plan.Sql, @"(?m)^\s*GO\s*$"));
            }),
            ("plan: retained objects cannot be deployed", () =>
            {
                var session = DemoData.CreateSession(includeUnreadable: false);
                var plan = ScriptPlanner.Build(session, session.Report.Differences.Where(d => d.State == DifferenceState.Retained).ToArray());
                Assert(plan.Blocked && !plan.Sql.Contains("EXEC sys.sp_executesql", StringComparison.Ordinal));
            }),
            ("plan: missing schema blocks all executable SQL", () =>
            {
                var session = DemoData.CreateSession(includeUnreadable: false); session = session with { Target = session.Target with { Schemas = [] } };
                Assert(ScriptPlanner.Build(session, session.Report.Differences.Where(d => d.CanSelect).ToArray()).Blocked);
            }),
            ("plan: missing dependency inventory blocks SQL", () =>
            {
                var session = DemoData.CreateSession(includeUnreadable: false); session = session with { Target = session.Target with { DependenciesRead = false } };
                Assert(ScriptPlanner.Build(session, session.Report.Differences.Where(d => d.CanSelect).ToArray()).Blocked);
            }),
            ("plan: dependency cycle is blocked", DependencyCycle),
            ("plan: indexed view is blocked", () =>
            {
                var session = DemoData.CreateSession(includeUnreadable: false);
                session = session with { Source = session.Source with { Modules = session.Source.Modules.Select(m => m.Category == ObjectCategory.View ? m with { HasIndexes = true } : m).ToList() } };
                Assert(ScriptPlanner.Build(session, session.Report.Differences.Where(d => d.Category == ObjectCategory.View).ToArray()).Blocked);
            }),
            ("plan: failed preflight blocks executable output", () =>
            {
                var session = DemoData.CreateSession(includeUnreadable: false);
                var plan = ScriptPlanner.Build(session, session.Report.Differences.Where(d => d.CanSelect).ToArray(), [new(new("dbo", "ProductionReport"), "NULL", 1, RiskLevel.Blocked, "NULL found")]);
                Assert(plan.Blocked && !plan.Sql.Contains("BEGIN TRANSACTION", StringComparison.Ordinal));
            }),
            ("data: datetime2 and offset preserve seven digits", () =>
            {
                var date = new DateTime(2026, 10, 1, 12, 0, 0).AddTicks(1234567);
                Assert(DataService.Literal(date, new() { SystemTypeId = 42 }).Contains(".1234567", StringComparison.Ordinal));
                Assert(DataService.Literal(new DateTimeOffset(date, TimeSpan.FromHours(8)), new() { SystemTypeId = 43 }).Contains(".1234567+08:00", StringComparison.Ordinal));
                Assert(DataService.Literal(TimeSpan.FromTicks(1234567), new() { SystemTypeId = 41 }).Contains(".1234567", StringComparison.Ordinal));
                Assert(DataService.Literal(date, new() { SystemTypeId = 61 }).Contains(".123'", StringComparison.Ordinal));
            }),
            ("data: literal conversion is culture invariant", () =>
            {
                var previous = CultureInfo.CurrentCulture;
                try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); Assert(DataService.Literal(1.25m, new()) == "1.25"); Assert(DataService.Literal("a'b", new()) == "N'a''b'"); Assert(DataService.Literal(new byte[] { 0, 255 }, new()) == "0x00FF"); }
                finally { CultureInfo.CurrentCulture = previous; }
            }),
            ("data: only valid unique keys are accepted", () =>
            {
                var table = DemoData.CreateSession().Source.Tables[0]; Assert(DataService.UsableKeys(table).Count == 1);
                Throws<InvalidOperationException>(() => DataService.Validate(table, new(table.Key, ["Remark"], ["Remark"])));
                var selected = DataService.Validate(table, new(table.Key, ["Remark"], ["Id"])); Assert(selected.Any(c => c.Name == "Id"));
                Throws<ArgumentException>(() => DataService.Validate(table, new(table.Key, ["Remark"], ["Id"], 10001)));
            }),
            ("data: unsafe column selection is rejected", () =>
            {
                var table = DemoData.CreateSession().Source.Tables[0];
                Throws<ArgumentException>(() => DataService.Validate(table, new(table.Key, ["Id]; DROP TABLE x--"], ["Id"])));
                Assert(!DataService.Supported(new() { SystemTypeId = 240 }));
            }),
            ("profiles: saved settings contain no credentials", () =>
            {
                var sanitized = WorkspaceStore.SanitizeConnection("Server=demo;Database=test;User ID=private-user;Password=private-secret;Encrypt=True;TrustServerCertificate=False");
                var parsed = new SqlConnectionStringBuilder(sanitized);
                Assert(parsed.UserID.Length == 0 && parsed.Password.Length == 0 && !sanitized.Contains("private-", StringComparison.Ordinal));
                Assert(parsed.DataSource == "demo" && parsed.InitialCatalog == "test");
            }),
            ("export: HTML and CSV are escaped", () =>
            {
                var report = new ComparisonReport { Source = "<script>alert(1)</script>", Target = "<img src=x>" };
                var html = WorkspaceStore.Html(report); Assert(!html.Contains("<script>", StringComparison.Ordinal) && html.Contains("&lt;script&gt;", StringComparison.Ordinal));
                Assert(WorkspaceStore.CsvCell("  =HYPERLINK(\"x\")").StartsWith("\"'", StringComparison.Ordinal));
                Assert(WorkspaceStore.CsvCell("abc\"def") == "\"abc\"\"def\"");
            }),
            ("snapshot: round trip and duplicate validation", SnapshotRoundTrip),
            ("history: only compatible complete reports have a delta", () =>
            {
                var options = new ComparisonOptions();
                var before = new HistoryEntry(DateTimeOffset.Now.AddDays(-1), "s", "t", true, 2, 0, 0, options, ["a", "b"]);
                var after = before with { Time = DateTimeOffset.Now, RequiredObjects = ["b", "c"] };
                Assert(WorkspaceStore.HistoryDelta(after, before) == (1, 1)); Assert(WorkspaceStore.HistoryDelta(after with { Complete = false }, before) is null);
            }),
            ("CLI: rejects unsafe or ambiguous arguments", () =>
            {
                Throws<ArgumentException>(() => WorkbenchCli.Parse(["--scope", "garbage"]));
                Throws<ArgumentException>(() => WorkbenchCli.Parse(["--scope", "foreignkeys"]));
                Throws<ArgumentException>(() => WorkbenchCli.Parse(["--demo", "--preflight"]));
                Throws<ArgumentException>(() => WorkbenchCli.Parse(["--output", "same.json", "--source-snapshot", "same.json"]));
                Assert(WorkbenchCli.Parse(["--scope", "tables", "--strict"]).Options.IgnoreIndexNames == false);
            }),
            ("files: atomic writes honor cancellation", () =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "dbcheck-tests-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                try
                {
                    var path = Path.Combine(directory, "test.txt"); WorkspaceStore.WriteTextAsync(path, "original").GetAwaiter().GetResult();
                    using var stop = new CancellationTokenSource(); stop.Cancel(); Throws<OperationCanceledException>(() => WorkspaceStore.WriteTextAsync(path, "changed", stop.Token).GetAwaiter().GetResult());
                    Assert(File.ReadAllText(path) == "original" && Directory.GetFiles(directory).Length == 1);
                }
                finally { Directory.Delete(directory, true); }
            })
        };
        var failed = 0;
        foreach (var test in tests)
        {
            try { test.Test(); Console.WriteLine("PASS " + test.Name); }
            catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + test.Name + "\n" + ex); }
        }
        Console.WriteLine($"RESULT: {tests.Length - failed}/{tests.Length} passed; {failed} failed. No database connections were opened.");
        return failed == 0 ? 0 : 1;
    }

    private static void RandomDiff()
    {
        var random = new Random(6149);
        for (var sample = 0; sample < 100; sample++)
        {
            string Make() => string.Join('\n', Enumerable.Range(0, random.Next(1, 30)).Select(_ => "line " + random.Next(8)));
            var before = Make(); var after = Make(); CheckReconstruction(before, after, DiffEngine.Compute(before, after));
        }
    }
    private static void CheckReconstruction(string before, string after, DiffDocument document)
    {
        Assert(string.Join('\n', document.Rows.Where(r => r.LeftNumber.HasValue).Select(r => r.Left)) == before, "Left reconstruction lost content");
        Assert(string.Join('\n', document.Rows.Where(r => r.RightNumber.HasValue).Select(r => r.Right)) == after, "Right reconstruction lost content");
        Assert(document.Rows.Where(r => r.LeftNumber.HasValue).Select(r => r.LeftNumber!.Value).SequenceEqual(Enumerable.Range(1, before.Split('\n').Length)));
    }
    private static void DependencyCycle()
    {
        var options = new ComparisonOptions { Scope = ComparisonScope.Views };
        var a = new ModuleModel { Key = new("dbo", "A"), TypeCode = "V", Category = ObjectCategory.View, Definition = "CREATE VIEW dbo.A AS SELECT 1 AS x;" };
        var b = a with { Key = new("dbo", "B"), Definition = "CREATE VIEW dbo.B AS SELECT 1 AS x;" };
        var source = new CatalogSnapshot { Server = "s", Database = "s", Options = options, CompletedScopes = options.Scope, DependenciesRead = true, Schemas = ["dbo"], Modules = [a, b], Dependencies = [new(a.Key, b.Key, null, null, "B", true, false), new(b.Key, a.Key, null, null, "A", true, false)] };
        var target = source with { Server = "t", Database = "t", Modules = [], Dependencies = [] };
        var session = new ComparisonSession(source, target, ComparisonEngine.Compare(source, target, options));
        Assert(ScriptPlanner.Build(session, session.Report.Differences).Blocked);
    }
    private static void SnapshotRoundTrip()
    {
        var source = DemoData.CreateSession(includeUnreadable: false).Source;
        var json = JsonSerializer.Serialize(source, WorkspaceStore.JsonOptions);
        var restored = JsonSerializer.Deserialize<CatalogSnapshot>(json, WorkspaceStore.JsonOptions)!;
        WorkspaceStore.ValidateSnapshot(restored); Assert(restored.Fingerprint == source.Fingerprint);
        Throws<InvalidOperationException>(() => WorkspaceStore.ValidateSnapshot(restored with { Tables = [restored.Tables[0], restored.Tables[0]] }));
        Throws<NotSupportedException>(() => WorkspaceStore.ValidateSnapshot(restored with { FormatVersion = 9 }));
    }
}
