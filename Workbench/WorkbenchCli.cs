using System.Text.Json;
using System.Xml.Linq;

namespace DbCheck.Workbench;

internal static class WorkbenchCli
{
    internal const string Help = """
        WEYU DBCheck — 新版唯讀工作台命令列
        --workbench [--config app.config] [--output output/report.json]
          [--scope tables,views,routines,foreignkeys,checks,triggers]
          [--exclude-zz] [--exclude-prefix TEMP_,BACKUP_] [--strict]
          [--source-snapshot source.json] [--target-snapshot target.json]
          [--save-snapshot output/standard.snapshot.json]
          [--plan output/review.sql] [--preflight]
        --workbench --demo [--output output/demo.html]

        --strict：比對定序、欄位順序及索引／約束名稱。
        --plan：只匯出草稿，永遠不執行。存在阻擋項目時不覆寫舊 SQL 檔。
        --preflight：明確要求唯讀資料預檢；可能掃描資料，需 SELECT 權限。
        預設範圍：tables,views,routines,foreignkeys,checks。
        可用 WEYU_DBCHECK_SOURCE、WEYU_DBCHECK_TARGET 覆蓋連線；不在輸出中記錄連線字串。
        報告副檔名：.json、.html、.csv。快照不含資料列，但包含 SQL 定義。
        退出碼：0=已選範圍無待處理差異；2=有差異；3=部分完成／計畫被阻擋；1=失敗／取消。
        舊版命令列不加 --workbench，保留原來的範圍與退出碼。
        """;

    internal sealed record Arguments(string? Config, string Output, string? SourceSnapshot, string? TargetSnapshot, string? SaveSnapshot, string? Plan, bool Preflight, bool Demo, ComparisonOptions Options);

    internal static Arguments Parse(string[] args)
    {
        string? config = null, sourceSnapshot = null, targetSnapshot = null, saveSnapshot = null, plan = null;
        var output = "output/report.json";
        var options = new ComparisonOptions(); var preflight = false; var demo = false;
        for (var i = 0; i < args.Length; i++)
        {
            string Value() => ++i < args.Length && !args[i].StartsWith("--", StringComparison.Ordinal) ? args[i] : throw new ArgumentException("缺少參數值。");
            switch (args[i])
            {
                case "--config": config = Value(); break;
                case "--output": output = Value(); break;
                case "--source-snapshot": sourceSnapshot = Value(); break;
                case "--target-snapshot": targetSnapshot = Value(); break;
                case "--save-snapshot": saveSnapshot = Value(); break;
                case "--plan": plan = Value(); break;
                case "--preflight": preflight = true; break;
                case "--demo": demo = true; break;
                case "--exclude-zz": options = options with { ExcludeZZ = true }; break;
                case "--exclude-prefix": options = options with { ExcludedPrefixes = Value().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) }; break;
                case "--strict": options = options with { IgnoreCollation = false, IgnoreColumnOrder = false, IgnoreIndexNames = false }; break;
                case "--scope": options = options with { Scope = ParseScope(Value()) }; break;
                default: throw new ArgumentException("不支援的參數，請使用 --workbench --help。");
            }
        }
        options.Validate();
        if (preflight && (targetSnapshot is not null || demo)) throw new ArgumentException("離線快照／示範模式無法執行資料預檢。");
        var outputs = new[] { output, saveSnapshot, plan }.Where(p => p is not null).Select(p => Path.GetFullPath(p!)).ToArray();
        if (outputs.Distinct(StringComparer.OrdinalIgnoreCase).Count() != outputs.Length) throw new ArgumentException("報告、快照與 SQL 必須使用不同輸出路徑。");
        var inputs = new[] { config, sourceSnapshot, targetSnapshot }.Where(p => p is not null).Select(p => Path.GetFullPath(p!));
        if (inputs.Any(p => outputs.Contains(p, StringComparer.OrdinalIgnoreCase))) throw new ArgumentException("輸出路徑不可覆蓋輸入設定或快照。");
        return new(config, output, sourceSnapshot, targetSnapshot, saveSnapshot, plan, preflight, demo, options);
    }

    private static ComparisonScope ParseScope(string value)
    {
        var result = ComparisonScope.None;
        foreach (var item in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            result |= item.ToLowerInvariant() switch
            {
                "tables" => ComparisonScope.Tables, "views" => ComparisonScope.Views, "routines" => ComparisonScope.Routines,
                "foreignkeys" => ComparisonScope.ForeignKeys, "checks" => ComparisonScope.Checks, "triggers" => ComparisonScope.Triggers,
                "all" => ComparisonScope.All, _ => throw new ArgumentException("不支援的比對範圍。")
            };
        return result;
    }

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Contains("--help", StringComparer.Ordinal)) { Console.WriteLine(Help); return 0; }
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var arguments = Parse(args); var token = stop.Token;
            var connections = arguments.Demo ? (Source: (string?)null, Target: (string?)null) : ReadConnections(arguments.Config);
            ComparisonSession session;
            if (arguments.Demo) session = DemoData.CreateSession(arguments.Options);
            else
            {
                var standard = arguments.SourceSnapshot is not null ? await WorkspaceStore.LoadSnapshotAsync(arguments.SourceSnapshot, token)
                    : await new CatalogService().ReadAsync(Required(connections.Source, "標準"), arguments.Options, token, new ConsoleProgress());
                var actual = arguments.TargetSnapshot is not null ? await WorkspaceStore.LoadSnapshotAsync(arguments.TargetSnapshot, token)
                    : await new CatalogService().ReadAsync(Required(connections.Target, "檢查"), arguments.Options, token, new ConsoleProgress());
                // Explicit CLI rules are authoritative; mismatched snapshot coverage is reported as incomplete.
                session = new(standard, actual, ComparisonEngine.Compare(standard, actual, arguments.Options, token));
            }
            await WorkspaceStore.SaveReportAsync(arguments.Output, session.Report, token);
            if (arguments.SaveSnapshot is not null) await WorkspaceStore.SaveSnapshotAsync(arguments.SaveSnapshot, session.Source, token);
            Console.WriteLine(session.Report.Summary);
            Console.WriteLine(session.Report.Options.Description);
            Console.WriteLine("報告：" + Path.GetFullPath(arguments.Output));
            IReadOnlyList<PreflightFinding>? findings = null;
            var selected = session.Report.Differences.Where(d => d.CanSelect).ToArray();
            if (arguments.Preflight)
            {
                Console.WriteLine("執行明確要求的唯讀資料預檢，可能掃描資料表…");
                findings = await new PreflightService().RunAsync(Required(connections.Target, "檢查"), session, selected, token, new ConsoleProgress());
                foreach (var finding in findings) Console.WriteLine($"{finding.Risk}：{finding.Object}／{finding.Check}：{finding.Message}");
            }
            var blocked = findings?.Any(f => f.Risk == RiskLevel.Blocked) == true;
            if (arguments.Plan is not null)
            {
                var changePlan = ScriptPlanner.Build(session, selected, findings); blocked |= changePlan.Blocked;
                foreach (var step in changePlan.Steps) Console.WriteLine($"{step.Risk}：{step.Object}／{step.Action}：{step.Note}");
                if (!changePlan.Blocked) { await WorkspaceStore.WriteTextAsync(arguments.Plan, changePlan.Sql, token); Console.WriteLine("SQL 草稿已匯出，未執行。" ); }
                else Console.Error.WriteLine("SQL 計畫被阻擋；沒有寫入或覆蓋 SQL 檔，先前同名檔案不代表本次結果。");
            }
            return blocked || !session.Report.Complete ? 3 : session.Report.RequiredCount == 0 ? 0 : 2;
        }
        catch (Exception ex) { Console.Error.WriteLine(SqlText.Failure(ex)); return 1; }
        finally { Console.CancelKeyPress -= cancel; }
    }

    private static string Required(string? connection, string name) => string.IsNullOrWhiteSpace(connection) ? throw new ArgumentException("缺少" + name + "資料庫連線；可使用設定檔、環境變數或離線快照。") : connection;
    private static (string? Source, string? Target) ReadConnections(string? configPath)
    {
        string? source = null, target = null;
        if (configPath is not null)
        {
            if (Path.GetExtension(configPath).Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(configPath));
                source = document.RootElement.TryGetProperty("SourceConnectionString", out var s) ? s.GetString() : null;
                target = document.RootElement.TryGetProperty("TargetConnectionString", out var t) ? t.GetString() : null;
            }
            else
            {
                var document = XDocument.Load(configPath);
                string? Read(string name) => document.Root?.Element("connectionStrings")?.Elements("add").SingleOrDefault(e => (string?)e.Attribute("name") == name)?.Attribute("connectionString")?.Value;
                source = Read("MES-H5-DB"); target = Read("Project-DB");
            }
        }
        return (Environment.GetEnvironmentVariable("WEYU_DBCHECK_SOURCE") ?? source, Environment.GetEnvironmentVariable("WEYU_DBCHECK_TARGET") ?? target);
    }
    private sealed class ConsoleProgress : IProgress<string> { public void Report(string value) => Console.WriteLine(value); }
}
