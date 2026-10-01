using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using DbCheck.Workbench;

if (args.Length > 0 && args[0] == "--workbench")
    return await WorkbenchCli.RunAsync(args[1..]);
if (args.Length == 1 && args[0] == "--self-test")
    return SelfTests.Run();
if (args.Length > 0 && args[0] == "--ui-smoke-test")
{
    if (args.Length != 2) { Console.Error.WriteLine("用法：--ui-smoke-test output/ui"); return 1; }
    return RunWindows(() => UiSmokeTests.Run(args[1]), diagnostic: true);
}
if (args.Length == 0 || args.Length == 1 && args[0] is "--demo" or "--legacy-gui")
    return RunWindows(() =>
    {
        using Form form = args.Contains("--legacy-gui") ? new MainForm() : new WorkbenchForm(args.Contains("--demo"));
        Application.Run(form);
        return 0;
    });

if (args.Contains("--help"))
{
    Console.WriteLine("WEYU-DBCheck — SQL Server 唯讀比對");
    Console.WriteLine("不加參數：新版工作台；--demo：離線示範；--legacy-gui：舊版介面。");
    Console.WriteLine("新版命令列說明：--workbench --help");
    Console.WriteLine("舊版用法：WEYU-DBCheck --config app.config [--output output/differences.json]");
    Console.WriteLine("app.config：MES-H5-DB 為來源，Project-DB 為目標；亦支援原 JSON 設定。");
    Console.WriteLine("可用 WEYU_DBCHECK_SOURCE / WEYU_DBCHECK_TARGET 環境變數覆蓋連線設定。");
    Console.WriteLine("舊版退出碼：0=一致，2=有差異，1=執行失敗。新版另有 3=部分完成／計畫被阻擋。");
    return 0;
}

// Preserve the original CLI contract. Opt into shared GUI rules with --workbench.
try
{
    string? configPath = null;
    var output = "output/differences.json";
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--config" when i + 1 < args.Length: configPath = args[++i]; break;
            case "--output" when i + 1 < args.Length: output = args[++i]; break;
            default: throw new ArgumentException("參數錯誤，請使用 --help。");
        }
    }
    if (configPath is null) throw new ArgumentException("請指定 --config。");
    Settings settings;
    if (string.Equals(Path.GetExtension(configPath), ".json", StringComparison.OrdinalIgnoreCase))
        settings = JsonSerializer.Deserialize<Settings>(await File.ReadAllTextAsync(configPath)) ?? throw new ArgumentException("設定檔不可為空。");
    else
    {
        var config = XDocument.Load(configPath);
        string ReadConnection(string name) => config.Root?.Element("connectionStrings")?.Elements("add")
            .SingleOrDefault(x => (string?)x.Attribute("name") == name)?.Attribute("connectionString")?.Value ?? throw new ArgumentException($"缺少 {name} 連線設定。");
        settings = new(ReadConnection("MES-H5-DB"), ReadConnection("Project-DB"));
    }
    var sourceConnection = Environment.GetEnvironmentVariable("WEYU_DBCHECK_SOURCE") ?? settings.SourceConnectionString;
    var targetConnection = Environment.GetEnvironmentVariable("WEYU_DBCHECK_TARGET") ?? settings.TargetConnectionString;
    if (string.IsNullOrWhiteSpace(sourceConnection) || string.IsNullOrWhiteSpace(targetConnection)) throw new ArgumentException("請設定來源與目標資料庫連線。");
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    Console.WriteLine("讀取來源資料庫結構…");
    var source = await SchemaReader.ReadAsync(sourceConnection, stop.Token);
    Console.WriteLine("讀取目標資料庫結構…");
    var target = await SchemaReader.ReadAsync(targetConnection, stop.Token);
    var differences = SchemaComparer.Compare(source, target);
    var path = Path.GetFullPath(output);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
    {
        generatedAt = DateTimeOffset.Now, sourceObjectCount = source.Count, targetObjectCount = target.Count,
        differenceCount = differences.Count, differences
    }, new JsonSerializerOptions { WriteIndented = true }), stop.Token);
    Console.WriteLine($"比對完成：{differences.Count} 項差異。報告：{path}");
    return differences.Count == 0 ? 0 : 2;
}
catch (SqlException ex)
{
    Console.Error.WriteLine($"資料庫操作失敗（SQL {ex.Number}）。請確認連線、加密與 VIEW DEFINITION 權限。");
    return 1;
}
catch (Exception ex) when (ex is ArgumentException or IOException or JsonException or XmlException or OperationCanceledException or InvalidOperationException)
{
    Console.Error.WriteLine($"執行失敗：{ex.GetType().Name}。請確認參數、設定、檔案權限與資料庫中繼資料存取權限。");
    return 1;
}

static int RunWindows(Func<int> action, bool diagnostic = false)
{
    var result = 1;
    var thread = new Thread(() =>
    {
        try
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            ApplicationConfiguration.Initialize();
            result = action();
        }
        catch (Exception ex) { Console.Error.WriteLine(diagnostic ? ex.ToString() : SqlText.Failure(ex)); result = 1; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start(); thread.Join(); return result;
}

internal sealed record Settings(string SourceConnectionString, string TargetConnectionString);
