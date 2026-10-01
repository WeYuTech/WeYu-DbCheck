using System.Text;
using System.Data;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;

internal sealed class MainForm : Form
{
    private sealed class Endpoint
    {
        internal readonly TextBox Connection = new() { Dock = DockStyle.Fill };
        internal readonly TextBox User = new() { Dock = DockStyle.Fill };
        internal readonly TextBox Password = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
        internal readonly TextBox Status = new() { Dock = DockStyle.Fill, ReadOnly = true };
        internal readonly Button Indicator = new() { Text = "—", Width = 40, Dock = DockStyle.Fill, TabStop = false, FlatStyle = FlatStyle.Flat };
        internal readonly Button Test = new() { Text = "測試連線", AutoSize = true };
        internal int Revision;
        internal bool Testing;
        internal string? Verified;
    }

    private readonly Endpoint source = new(), target = new();
    private readonly Button[] compare = [new() { Text = "資料結構比對" }, new() { Text = "資料 View 表比對" }, new() { Text = "Function / SP 比對" }];
    private readonly TextBox right = ResultBox();
    private readonly CheckedListBox rightTables = new() { CheckOnClick = true, Dock = DockStyle.Fill, HorizontalScrollbar = true };
    private readonly CheckBox excludeZZ = new() { Text = "排除ZZ", AutoSize = true, Checked = false };
    private readonly Button rightSql = new() { Text = "產生SCHEMA SQL", AutoSize = true, Enabled = false };
    private readonly Button dataInsertSql = new() { Text = "產生 Data Insert SQL", AutoSize = true, Enabled = false };
    private readonly ComboBox insertOrder = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 85 };
    private readonly TextBox insertRowCount = new() { Text = "100", Width = 75 };
    private readonly Label summary = new() { Text = "請先測試兩個資料庫連線。", AutoSize = true };
    private readonly CancellationTokenSource lifetime = new();
    private bool comparing;
    private int comparisonMode;
    private string ObjectKind => comparisonMode switch { 1 => "VIEW", 2 => "FUNCTION / SP", _ => "TABLE" };
    private Dictionary<DataTableSchemaReport.TableDifference, ViewSchemaService.ViewDefinition> standardViews = new(), checkViews = new();
    private DataTableSchemaReport.Report? schemaReport;
    private DataTable h5DbSchemaTb = new("H5-DB-Schema-TB");
    private DataTable projectDbSchemaTb = new("Project-DB-Schema-TB");

    internal MainForm()
    {
        Text = "WEYU-DBCheck — 資料庫比對";
        ClientSize = new Size(1180, 760);
        MinimumSize = new Size(900, 650);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft JhengHei UI", 10);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 170));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 170));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(BuildEndpoint("標準 DB", source), 0, 0);
        layout.Controls.Add(BuildEndpoint("檢查 DB", target), 0, 1);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill };
        for (var i = 0; i < compare.Length; i++)
        {
            var mode = i;
            compare[i].AutoSize = true;
            compare[i].Height = 38;
            compare[i].Enabled = false;
            compare[i].Click += async (_, _) => await CompareAsync(mode);
            buttons.Controls.Add(compare[i]);
        }
        layout.Controls.Add(buttons, 0, 2);
        layout.Controls.Add(summary, 0, 3);
        rightSql.Click += (_, _) => ShowSql(h5DbSchemaTb, projectDbSchemaTb, rightTables);
        dataInsertSql.Click += async (_, _) => await ShowDataInsertSqlAsync();
        layout.Controls.Add(BuildDifferencePanel("檢查 DB 差異（以標準 DB 為準）", right, rightTables, rightSql), 0, 4);
        Controls.Add(layout);
        Shown += (_, _) => LoadConfig();
        FormClosing += (_, _) => lifetime.Cancel();
    }

    private static TextBox ResultBox() => new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false };
    private Control BuildDifferencePanel(string title, TextBox result, CheckedListBox tables, Button generate)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 65));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 280));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.Controls.Add(tables, 0, 0);
        content.Controls.Add(result, 1, 0);
        panel.Controls.Add(content, 0, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true };
        actions.Controls.Add(excludeZZ);
        var selectAll = new Button { Text = "全選", AutoSize = true };
        var selectNone = new Button { Text = "取消全選", AutoSize = true };
        selectAll.Click += (_, _) => { if (!comparing) for (var i=0; i<tables.Items.Count; i++) tables.SetItemChecked(i,true); };
        selectNone.Click += (_, _) => { if (!comparing) for (var i=0; i<tables.Items.Count; i++) tables.SetItemChecked(i,false); };
        actions.Controls.Add(selectAll); actions.Controls.Add(selectNone); actions.Controls.Add(generate); actions.Controls.Add(dataInsertSql);
        excludeZZ.CheckedChanged += (_, _) => RefreshTableFilter();
        insertOrder.Items.AddRange(new object[] { "順排", "逆排" });
        insertOrder.SelectedIndex = 0;
        actions.Controls.Add(insertOrder);
        actions.Controls.Add(new Label { Text = "資料筆數", AutoSize = true, Margin = new Padding(5, 7, 0, 0) });
        actions.Controls.Add(insertRowCount);
        panel.Controls.Add(actions, 0, 0);
        tables.SelectedIndexChanged += (_, _) =>
        {
            if (tables.SelectedItem is DataTableSchemaReport.TableDifference selected && schemaReport is not null)
                right.Text = $"差異 {ObjectKind} 數量：{rightTables.Items.Count}\r\n\r\n" + schemaReport.TableTexts[selected];
            UpdateButtons();
        };
        tables.ItemCheck += (_, e) => UpdateButtons(tables.CheckedItems.Count
            + (e.NewValue == CheckState.Checked ? 1 : 0) - (e.CurrentValue == CheckState.Checked ? 1 : 0));
        return ResultGroup(title, panel);
    }

    private void RefreshTableFilter()
    {
        if (schemaReport is null) return;
        var previous = rightTables.SelectedItem as DataTableSchemaReport.TableDifference;
        var previouslyChecked = rightTables.CheckedItems.Cast<DataTableSchemaReport.TableDifference>().ToHashSet();
        var excludedPrefix = comparisonMode == 1 ? "V_ZZ" : "ZZ";
        var visible = schemaReport.Tables.Where(t => !excludeZZ.Checked || !t.Name.StartsWith(excludedPrefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        rightTables.BeginUpdate();
        try
        {
            rightTables.Items.Clear();
            rightTables.Items.AddRange(visible.Cast<object>().ToArray());
            for (var i=0; i<visible.Length; i++) rightTables.SetItemChecked(i, previouslyChecked.Contains(visible[i]));
            var selectedIndex = previous is null ? -1 : Array.IndexOf(visible, previous);
            if (visible.Length > 0) rightTables.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
            else right.Text = $"差異 {ObjectKind} 數量：0\r\n" + (schemaReport.Tables.Count > 0 ? $"套用排除ZZ後，沒有符合條件的差異 {ObjectKind}。" : "無差異");
        }
        finally { rightTables.EndUpdate(); }
        summary.Text = $"差異 {ObjectKind} 數量：{visible.Length}（原始 {schemaReport.Tables.Count}，排除 {schemaReport.Tables.Count - visible.Length}）";
        UpdateButtons();
    }

    private void ClearTableSelections()
    {
        schemaReport = null;
        standardViews.Clear(); checkViews.Clear();
        rightTables.Items.Clear();
        rightSql.Enabled = false;
        dataInsertSql.Enabled = false;
    }

    private async Task ShowDataInsertSqlAsync()
    {
        var selected = rightTables.CheckedItems.Cast<DataTableSchemaReport.TableDifference>().ToArray();
        if (comparisonMode != 0 || comparing || source.Verified is null || selected.Length == 0) return;
        if (!int.TryParse(insertRowCount.Text.Trim(), out var rowCount) || rowCount <= 0)
        {
            MessageBox.Show(this, "資料筆數請輸入大於 0 的整數。", "資料筆數", MessageBoxButtons.OK, MessageBoxIcon.Information);
            insertRowCount.Focus();
            return;
        }
        var descending = insertOrder.SelectedIndex == 1;
        comparing = true;
        UpdateButtons();
        rightTables.Enabled = false;
        summary.Text = $"正在產生 {selected.Length} 個 TABLE 的 INSERT SQL，每表最多 {rowCount} 筆…";
        try
        {
            var scripts = new StringBuilder();
            for (var i = 0; i < selected.Length; i++)
            {
                var table = selected[i];
                summary.Text = $"{i+1}/{selected.Length}：讀取 {table}，{(descending ? "逆排" : "順排")}前 {rowCount} 筆…";
                scripts.AppendLine(await DataInsertSqlGenerator.GenerateAsync(source.Verified, table.Schema, table.Name, lifetime.Token, rowCount, descending));
                scripts.AppendLine("GO").AppendLine();
            }
            if (lifetime.IsCancellationRequested) return;
            summary.Text = $"{selected.Length} 個 TABLE 的 Data Insert SQL 已產生，未執行。";
            using var dialog = new Form { Text = $"Data Insert SQL 預覽 — 檢查 DB — {selected.Length} 個 TABLE", Width = 1000, Height = 700, StartPosition = FormStartPosition.CenterParent };
            var box = ResultBox(); box.Font = new Font("Consolas", 11); box.Text = scripts.ToString();
            dialog.Controls.Add(box);
            dialog.ShowDialog(this);
        }
        catch (Exception ex)
        {
            if (!lifetime.IsCancellationRequested)
            {
                summary.Text = ex is SqlException sql ? $"讀取標準 DB 失敗（SQL {sql.Number}），請確認 SELECT 權限與連線。"
                    : ex is NotSupportedException or InvalidOperationException ? ex.Message : "Data Insert SQL 產生失敗，請確認資料型別與連線。";
            }
        }
        finally
        {
            comparing = false;
            if (!lifetime.IsCancellationRequested) { rightTables.Enabled = true; UpdateButtons(); }
        }
    }

    private void ShowSql(DataTable standard, DataTable actual, CheckedListBox selection)
    {
        var selected = selection.CheckedItems.Cast<DataTableSchemaReport.TableDifference>().ToArray();
        if (comparing || selected.Length == 0) return;
        try
        {
            var sql = string.Join("\r\nGO\r\n\r\n", selected.Select(table => comparisonMode != 0
                ? ViewSchemaService.Generate(table, standardViews[table], checkViews.ContainsKey(table), checkViews.GetValueOrDefault(table)?.Type)
                : TableSqlGenerator.Generate(standard, actual, table)));
            using var dialog = new Form { Text = $"SQL 預覽 — 檢查 DB — {selected.Length} 個 {ObjectKind}", Width = 1000, Height = 700, StartPosition = FormStartPosition.CenterParent };
            var box = ResultBox(); box.Font = new Font("Consolas", 11); box.Text = sql;
            dialog.Controls.Add(box);
            dialog.ShowDialog(this);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            MessageBox.Show(this, "無法完整產生此資料表 SQL：" + ex.Message, "SQL 產生", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
    private static GroupBox ResultGroup(string title, Control content)
    {
        var group = new GroupBox { Text = title, Dock = DockStyle.Fill, Padding = new Padding(10) };
        group.Controls.Add(content);
        return group;
    }

    private GroupBox BuildEndpoint(string name, Endpoint endpoint)
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 3, Padding = new Padding(5) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 85));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 65));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 45));
        for (var i = 0; i < 3; i++) grid.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33f));
        grid.Controls.Add(new Label { Text = "連線字串", AutoSize = true }, 0, 0);
        grid.Controls.Add(endpoint.Connection, 1, 0);
        grid.SetColumnSpan(endpoint.Connection, 3);
        grid.Controls.Add(endpoint.Indicator, 4, 0);
        grid.Controls.Add(new Label { Text = "帳號", AutoSize = true }, 0, 1);
        grid.Controls.Add(endpoint.User, 1, 1);
        grid.Controls.Add(new Label { Text = "密碼", AutoSize = true }, 2, 1);
        grid.Controls.Add(endpoint.Password, 3, 1);
        grid.Controls.Add(endpoint.Test, 0, 2);
        grid.Controls.Add(endpoint.Status, 1, 2);
        grid.SetColumnSpan(endpoint.Status, 4);
        foreach (var box in new[] { endpoint.Connection, endpoint.User, endpoint.Password })
            box.TextChanged += (_, _) => InvalidateEndpoint(endpoint);
        endpoint.Test.Click += async (_, _) => await TestAsync(endpoint);
        return ResultGroup(name, grid);
    }

    private void LoadConfig()
    {
        try
        {
            var config = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "app.config"));
            foreach (var (name, endpoint) in new[] { ("MES-H5-DB", source), ("Project-DB", target) })
            {
                var value = config.Root?.Element("connectionStrings")?.Elements("add")
                    .SingleOrDefault(x => (string?)x.Attribute("name") == name)?.Attribute("connectionString")?.Value
                    ?? throw new InvalidOperationException();
                var builder = new SqlConnectionStringBuilder(value);
                endpoint.User.Text = builder.UserID;
                endpoint.Password.Text = builder.Password;
                builder.Remove("User ID");
                builder.Remove("Password");
                endpoint.Connection.Text = builder.ConnectionString;
            }
            summary.Text = "設定已載入。輸入帳號使用 SQL 驗證；帳密皆空白時沿用連線字串的驗證設定。";
        }
        catch { summary.Text = "app.config 讀取失敗，請確認兩組連線名稱與格式；亦可直接輸入連線設定。"; }
    }

    private void InvalidateEndpoint(Endpoint endpoint)
    {
        endpoint.Revision++;
        endpoint.Verified = null;
        endpoint.Status.Text = "尚未測試／設定已變更";
        endpoint.Indicator.Text = "—";
        endpoint.Indicator.ForeColor = Color.Gray;
        endpoint.Indicator.BackColor = SystemColors.Control;
        right.Clear();
        ClearTableSelections();
        UpdateButtons();
    }

    private void UpdateButtons(int? checkedCount = null)
    {
        rightTables.Enabled = !comparing;
        excludeZZ.Enabled = !comparing;
        insertOrder.Enabled = insertRowCount.Enabled = !comparing && comparisonMode == 0;
        var hasCheckedTables = (checkedCount ?? rightTables.CheckedItems.Count) > 0;
        rightSql.Enabled = !comparing && hasCheckedTables;
        dataInsertSql.Enabled = comparisonMode == 0 && !comparing && source.Verified is not null && target.Verified is not null && hasCheckedTables;
        foreach (var button in compare) button.Enabled = !comparing && source.Verified is not null && target.Verified is not null;
        foreach (var endpoint in new[] { source, target })
        {
            endpoint.Connection.ReadOnly = comparing;
            endpoint.User.ReadOnly = comparing;
            endpoint.Password.ReadOnly = comparing;
            endpoint.Test.Enabled = !comparing && !endpoint.Testing;
        }
    }

    private async Task TestAsync(Endpoint endpoint)
    {
        if (endpoint.Testing || comparing) return;
        endpoint.Testing = true;
        InvalidateEndpoint(endpoint);
        var revision = endpoint.Revision;
        endpoint.Test.Enabled = false;
        endpoint.Status.Text = "測試中…";
        try
        {
            var builder = new SqlConnectionStringBuilder(endpoint.Connection.Text) { ConnectTimeout = 15 };
            if (!string.IsNullOrWhiteSpace(endpoint.User.Text))
            {
                builder.IntegratedSecurity = false;
                builder.Remove("Authentication");
                builder.UserID = endpoint.User.Text;
                builder.Password = endpoint.Password.Text;
            }
            else if (endpoint.Password.Text.Length > 0) throw new ArgumentException();
            await using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync(lifetime.Token);
            using var command = new SqlCommand("SELECT 1", connection) { CommandTimeout = 15 };
            await command.ExecuteScalarAsync(lifetime.Token);
            if (lifetime.IsCancellationRequested || revision != endpoint.Revision) return;
            endpoint.Verified = builder.ConnectionString;
            endpoint.Status.Text = "連線測試成功";
            endpoint.Indicator.Text = "✓";
            endpoint.Indicator.ForeColor = Color.ForestGreen;
            endpoint.Indicator.BackColor = Color.Honeydew;
        }
        catch (Exception ex)
        {
            if (lifetime.IsCancellationRequested || revision != endpoint.Revision) return;
            endpoint.Status.Text = ex is SqlException sql
                ? $"連線失敗（SQL {sql.Number}）：請檢查伺服器、帳密、網路與加密設定。"
                : "連線失敗：請檢查連線格式與帳號設定。";
            endpoint.Indicator.Text = "✕";
            endpoint.Indicator.ForeColor = Color.White;
            endpoint.Indicator.BackColor = Color.Firebrick;
        }
        finally
        {
            endpoint.Testing = false;
            if (!lifetime.IsCancellationRequested) { endpoint.Test.Enabled = true; UpdateButtons(); }
        }
    }

    private async Task CompareAsync(int mode)
    {
        if (source.Verified is null || target.Verified is null || comparing) return;
        comparing = true;
        comparisonMode = mode;
        UpdateButtons();
        var sourceString = source.Verified; var targetString = target.Verified;
        var sourceRevision = source.Revision; var targetRevision = target.Revision;
        summary.Text = "正在唯讀比對…";
        right.Clear();
        ClearTableSelections();
        void Report(string message)
        {
            if (lifetime.IsCancellationRequested) return;
            summary.Text = message;
        }
        try
        {
            if (mode is 1 or 2)
            {
                Report($"1/3 讀取標準 DB 的 {ObjectKind} 定義…");
                standardViews = await ViewSchemaService.ReadAsync(sourceString, lifetime.Token, mode == 2);
                Report($"2/3 讀取檢查 DB 的 {ObjectKind} 定義…");
                checkViews = await ViewSchemaService.ReadAsync(targetString, lifetime.Token, mode == 2);
                Report($"3/3 比對 {ObjectKind} 定義…");
                schemaReport = await Task.Run(() => ViewSchemaService.Compare(standardViews, checkViews), lifetime.Token);
                RefreshTableFilter();
                Report($"比對完成：顯示 {rightTables.Items.Count} 個差異 {ObjectKind}（原始 {schemaReport.Tables.Count}）。");
                MessageBox.Show(this, summary.Text, "比對完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (mode == 0)
            {
                Report("1/3 讀取標準 DB 資料表結構…");
                h5DbSchemaTb.Clear();
                projectDbSchemaTb.Clear();
                h5DbSchemaTb = await Task.Run(() => SchemaReader.GetTableStructureAsync(sourceString, lifetime.Token), lifetime.Token);
                h5DbSchemaTb.TableName = "標準 DB";
                Report($"標準 DB 已載入 {h5DbSchemaTb.Rows.Count} 個欄位。");
                Report("2/3 讀取檢查 DB 資料表結構…");
                projectDbSchemaTb = await Task.Run(() => SchemaReader.GetTableStructureAsync(targetString, lifetime.Token), lifetime.Token);
                projectDbSchemaTb.TableName = "檢查 DB";
                Report($"檢查 DB 已載入 {projectDbSchemaTb.Rows.Count} 個欄位。");
                var progress = new Progress<string>(Report);
                Report("3/3 以標準 DB 比對檢查 DB…");
                var forward = await Task.Run(() => DataTableSchemaReport.Create(h5DbSchemaTb, projectDbSchemaTb, lifetime.Token, progress), lifetime.Token);
                schemaReport = forward;
                RefreshTableFilter();
                Report($"比對完成：顯示 {rightTables.Items.Count} 個差異 TABLE（原始 {forward.Tables.Count}，排除 {forward.Tables.Count - rightTables.Items.Count}）。");
                MessageBox.Show(this, summary.Text, "比對完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var a = mode == 0 ? await SchemaReader.ReadAsync(sourceString, lifetime.Token)
                : await ReadModulesAsync(sourceString, mode, lifetime.Token);
            var b = mode == 0 ? await SchemaReader.ReadAsync(targetString, lifetime.Token)
                : await ReadModulesAsync(targetString, mode, lifetime.Token);
            if (lifetime.IsCancellationRequested || sourceRevision != source.Revision || targetRevision != target.Revision) return;
            var differences = SchemaComparer.Compare(a, b).Where(d => d.Status != "OnlyInTarget").ToArray();
            var r = new StringBuilder().AppendLine("以標準 DB 為準，檢查 DB 缺少或定義不同的物件：");
            foreach (var difference in differences)
            {
                r.AppendLine(difference.Object).AppendLine("標準 DB：").AppendLine(difference.Source)
                    .AppendLine("檢查 DB：").AppendLine(difference.Target ?? "（不存在）").AppendLine();
            }
            right.Text = differences.Length == 0 ? "無差異" : r.ToString();
            summary.Text = $"比對完成：檢查 DB 有 {differences.Length} 項差異（定義採文字比對）。";
            MessageBox.Show(this, summary.Text, "比對完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch
        {
            if (!lifetime.IsCancellationRequested)
            {
                InvalidateEndpoint(source); InvalidateEndpoint(target);
                summary.Text = "比對失敗，請重新測試連線並確認 VIEW DEFINITION 權限及物件是否加密。";
            }
        }
        finally
        {
            comparing = false;
            if (!lifetime.IsCancellationRequested) UpdateButtons();
        }
    }

    private static async Task<Dictionary<string, string>> ReadModulesAsync(string connectionString, int mode, CancellationToken token)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        using var permission = new SqlCommand("SELECT HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DEFINITION')", connection);
        if (Convert.ToInt32(await permission.ExecuteScalarAsync(token)) != 1) throw new InvalidOperationException();
        using var command = new SqlCommand("""
            SELECT s.name,o.name,o.type,m.definition,m.uses_ansi_nulls,m.uses_quoted_identifier
            FROM sys.objects o JOIN sys.schemas s ON s.schema_id=o.schema_id
            LEFT JOIN sys.sql_modules m ON m.object_id=o.object_id
            WHERE o.is_ms_shipped=0 AND ((@Mode=1 AND o.type='V') OR (@Mode=2 AND o.type IN ('FN','IF','TF','FS','FT')))
            """, connection) { CommandTimeout = 60 };
        command.Parameters.AddWithValue("@Mode", mode);
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(token))
        {
            if (reader.IsDBNull(3)) throw new InvalidOperationException();
            var key = $"[{reader.GetString(0).Replace("]", "]]")}].[{reader.GetString(1).Replace("]", "]]")}]";
            result.Add(key, $"類型：{reader.GetString(2).Trim()}；ANSI_NULLS={reader.GetBoolean(4)}；QUOTED_IDENTIFIER={reader.GetBoolean(5)}\r\n{reader.GetString(3)}");
        }
        return result;
    }
}
