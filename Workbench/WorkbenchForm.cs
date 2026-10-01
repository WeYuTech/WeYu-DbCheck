namespace DbCheck.Workbench;

internal sealed partial class WorkbenchForm : Form
{
    private sealed record TargetEndpoint(string Label, string Connection) { public override string ToString() => Label; }
    private sealed record ResultChoice(string Label, bool Failed) { public override string ToString() => (Failed ? "! " : "") + Label; }
    private readonly bool demo;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? operation;
    private bool busy, binding, applyingOptions;
    private readonly EndpointEditor source = new("標準 DB", "只讀取標準，不會寫入來源資料庫。"), target = new("檢查 DB", "SQL 草稿須人工檢閱後另行執行。");
    private readonly Panel pagesHost = new() { Dock = DockStyle.Fill, Padding = new Padding(20), BackColor = Ui.Background };
    private readonly FlowLayoutPanel navigation = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(12, 20, 12, 12), BackColor = Ui.Sidebar };
    private readonly Dictionary<string, Control> pages = new();
    private readonly Dictionary<string, Button> navButtons = new();
    private readonly Dictionary<ComparisonScope, CheckBox> scopeChecks = new();
    private readonly CheckBox ignoreCollation = Check("忽略欄位定序", true), ignoreOrdinal = Check("忽略欄位順序", true), ignoreNames = Check("忽略索引／約束名稱", true), excludeZZ = Check("排除 ZZ／V_ZZ", false), useSnapshot = Check("使用離線標準快照", false);
    private readonly TextBox prefixes = Ui.TextBox(), search = Ui.TextBox(), scopeText = Ui.CodeBox(), raw = Ui.CodeBox(), sql = Ui.CodeBox(), historyDetail = Ui.CodeBox();
    private readonly Label status = Ui.Label("請先設定兩側環境並測試連線。", true), snapshotLabel = Ui.Label("未載入標準快照", true), summary = Ui.Label("尚未比對", true), selectionLabel = Ui.Label("已選取 0 個物件", true), planLabel = Ui.Label("尚未建立變更計畫", true);
    private readonly ProgressBar progress = new() { Width = 140, Height = 18, Style = ProgressBarStyle.Marquee, Visible = false, Margin = new Padding(8) };
    private readonly Button cancel = Ui.Button("取消工作"), exportSql = Ui.Button("匯出 SQL", true), copySql = Ui.Button("複製 SQL");
    private readonly ListBox targetQueue = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle };
    private readonly ComboBox targetResults = Combo(), typeFilter = Combo(), stateFilter = Combo();
    private readonly DataGridView objects = Ui.Grid(), properties = Ui.Grid(), planGrid = Ui.Grid(), historyGrid = Ui.Grid();
    private readonly GitDiffView diff = new();
    private readonly HashSet<string> selectedIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ComparisonSession> sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TargetEndpoint> endpoints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> failures = new(StringComparer.Ordinal);
    private CatalogSnapshot? baseline;
    private ComparisonSession? active;
    private ChangePlan? plan;
    private IReadOnlyList<PreflightFinding>? findings;
    private DataToolsControl dataTools = null!;

    internal WorkbenchForm(bool demo = false)
    {
        this.demo = demo;
        Text = "WEYU DBCheck — 唯讀比對工作台" + (demo ? "（離線示範）" : "");
        Font = new Font("Microsoft JhengHei UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi; ClientSize = new Size(1440, 900); MinimumSize = new Size(1000, 720);
        StartPosition = FormStartPosition.CenterScreen; BackColor = Ui.Background; KeyPreview = true;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Margin = new Padding(0) };
        root.ColumnStyles.Add(new(SizeType.Absolute, 190)); root.ColumnStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.Absolute, 84)); root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.Absolute, 48));
        var brand = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Sidebar, Padding = new Padding(22, 18, 18, 8) };
        brand.Controls.Add(new Label { Text = "WEYU\nDBCheck", ForeColor = Color.White, Font = new Font(Font.FontFamily, 15, FontStyle.Bold), Dock = DockStyle.Fill }); root.Controls.Add(brand, 0, 0);
        var heading = Ui.Rows(new(SizeType.Percent, 58), new(SizeType.Percent, 42)); heading.Padding = new Padding(20, 10, 12, 5); heading.BackColor = Color.White;
        var title = Ui.Label("資料庫差異，一眼看懂"); title.Font = new Font(Font.FontFamily, 19, FontStyle.Bold);
        heading.Controls.Add(title, 0, 0); heading.Controls.Add(Ui.Label("READ ONLY   ／   比對 → 檢閱 → 匯出草稿   ·   不執行資料庫修改" + (demo ? "   ·   DEMO" : ""), true), 0, 1);
        root.Controls.Add(heading, 1, 0); root.Controls.Add(navigation, 0, 1); root.Controls.Add(pagesHost, 1, 1);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Padding = new Padding(16, 4, 8, 4), BackColor = Color.White };
        footer.ColumnStyles.Add(new(SizeType.Percent, 100)); footer.ColumnStyles.Add(new(SizeType.Absolute, 160)); footer.ColumnStyles.Add(new(SizeType.Absolute, 116));
        status.AutoSize = false; status.Dock = DockStyle.Fill; status.AutoEllipsis = true;
        footer.Controls.Add(status, 0, 0); footer.Controls.Add(progress, 1, 0); footer.Controls.Add(cancel, 2, 0); cancel.Enabled = false;
        root.Controls.Add(footer, 0, 2); root.SetColumnSpan(footer, 2); Controls.Add(root);
        AddPage("environment", "01   環境與基準", BuildEnvironment());
        AddPage("scope", "02   比對範圍", BuildScope());
        AddPage("differences", "03   差異檢視", BuildDifferences());
        AddPage("plan", "04   變更計畫", BuildPlan());
        dataTools = new DataToolsControl(() => source.VerifiedConnection ?? throw new InvalidOperationException("請先測試標準 DB 連線。"), () => target.VerifiedConnection, RunAsync, demo);
        AddPage("data", "資料匯出與補齊", dataTools);
        AddPage("history", "歷史與操作手順", BuildHistory());
        foreach (var endpoint in new[] { source, target })
        {
            endpoint.TestRequested += async (_, _) => await RunAsync(async token => { if (demo) throw new InvalidOperationException("離線示範模式不連接資料庫。"); await endpoint.TestAsync(token); status.Text = "連線已確認：" + endpoint.VerifiedLabel; });
            endpoint.SaveRequested += async (_, _) => await SaveProfileAsync(endpoint);
            endpoint.DeleteRequested += async (_, _) => await DeleteProfileAsync(endpoint);
            endpoint.Changed += (_, _) => { if (!binding && !busy) { ClearComparison(); dataTools.InvalidateResult(); } };
        }
        cancel.Click += (_, _) => operation?.Cancel();
        FormClosing += (_, _) => { lifetime.Cancel(); operation?.Cancel(); };
        KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Escape && busy) { operation?.Cancel(); e.Handled = true; }
            if (e.KeyCode == Keys.F5 && !busy) { await CompareAsync(false); e.Handled = true; }
            if (e.Control && e.KeyCode == Keys.F) { ShowPage("differences"); search.Focus(); e.Handled = true; }
        };
        Shown += async (_, _) =>
        {
            if (demo) { LoadDemo(); return; }
            await RunAsync(async token => { LoadLocalConnections(); await RefreshProfilesAsync(token); status.Text = "設定已載入；請測試連線。保存環境不會保存帳號或密碼。"; });
        };
        ShowPage("environment");
    }

    private Control BuildEnvironment()
    {
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var layout = Ui.Rows(new(SizeType.Absolute, 70), new(SizeType.Absolute, 424), new(SizeType.Absolute, 58), new(SizeType.Absolute, 130), new(SizeType.Absolute, 48));
        layout.Dock = DockStyle.Top; layout.Height = 730;
        layout.Controls.Add(PageHeading("環境與基準", "先確認標準来源與檢查目標；可用一份標準依序檢查多個環境。"), 0, 0);
        var cards = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
        cards.ColumnStyles.Add(new(SizeType.Percent, 50)); cards.ColumnStyles.Add(new(SizeType.Percent, 50)); cards.Controls.Add(source, 0, 0); cards.Controls.Add(target, 1, 0); layout.Controls.Add(cards, 0, 1);
        var loadSnapshot = Ui.Button("載入標準快照"); loadSnapshot.Click += async (_, _) => await LoadBaselineAsync();
        useSnapshot.CheckedChanged += (_, _) => { if (busy || applyingOptions) return; source.Enabled = !useSnapshot.Checked; ClearComparison(); };
        snapshotLabel.MaximumSize = new Size(500, 0); layout.Controls.Add(Ui.Actions(useSnapshot, loadSnapshot, snapshotLabel), 0, 2);
        var queuePanel = Ui.Rows(new(SizeType.Absolute, 32), new(SizeType.Percent, 100));
        queuePanel.Controls.Add(Ui.Label("批次目標（空白時使用上方檢查 DB；連線只留在本次記憶體）", true), 0, 0);
        var queue = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 }; queue.ColumnStyles.Add(new(SizeType.Percent, 100)); queue.ColumnStyles.Add(new(SizeType.Absolute, 280)); queue.Controls.Add(targetQueue, 0, 0);
        Button add = Ui.Button("加入已驗證目標"), remove = Ui.Button("移除選取"), clear = Ui.Button("清空");
        add.Click += (_, _) => { if (target.VerifiedConnection is null || target.VerifiedLabel is null) { Ui.Error(this, new InvalidOperationException("請先測試檢查 DB 連線。")); return; } if (!targetQueue.Items.Cast<TargetEndpoint>().Any(t => t.Label == target.VerifiedLabel)) targetQueue.Items.Add(new TargetEndpoint(target.VerifiedLabel, target.VerifiedConnection)); ClearComparison(); };
        remove.Click += (_, _) => { if (targetQueue.SelectedItem is not null) targetQueue.Items.Remove(targetQueue.SelectedItem); ClearComparison(); };
        clear.Click += (_, _) => { targetQueue.Items.Clear(); ClearComparison(); };
        queue.Controls.Add(Ui.Actions(add, remove, clear), 1, 0); queuePanel.Controls.Add(queue, 0, 1); layout.Controls.Add(queuePanel, 0, 3);
        var next = Ui.Button("下一步：選擇比對範圍 →", true); next.Click += (_, _) => ShowPage("scope"); layout.Controls.Add(Ui.Actions(next), 0, 4);
        scroll.Controls.Add(layout); return scroll;
    }

    private Control BuildScope()
    {
        var layout = Ui.Rows(new(SizeType.Absolute, 72), new(SizeType.Absolute, 184), new(SizeType.Absolute, 132), new(SizeType.Percent, 100), new(SizeType.Absolute, 52));
        layout.Controls.Add(PageHeading("比對範圍", "畫面與新版命令列共用同一套結果模型；未選範圍不代表一致。"), 0, 0);
        var categories = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = true, Padding = new Padding(16), BackColor = Color.White };
        foreach (var item in new[] { (ComparisonScope.Tables, "資料表、欄位與索引"), (ComparisonScope.Views, "View 定義"), (ComparisonScope.Routines, "Function／Stored Procedure"), (ComparisonScope.ForeignKeys, "外鍵約束"), (ComparisonScope.Checks, "CHECK 約束"), (ComparisonScope.Triggers, "資料表／View 的 DML Trigger") })
        {
            var check = Check(item.Item2, item.Item1 != ComparisonScope.Triggers); check.Margin = new Padding(0, 6, 40, 10); scopeChecks[item.Item1] = check; categories.Controls.Add(check);
            check.CheckedChanged += (_, _) => { if (!applyingOptions) { ClearComparison(); UpdateScopeDescription(); } };
        }
        layout.Controls.Add(categories, 0, 1);
        var rules = Ui.Rows(new(SizeType.Absolute, 42), new(SizeType.Absolute, 42), new(SizeType.Absolute, 40)); rules.BackColor = Color.White; rules.Padding = new Padding(12, 0, 12, 0);
        rules.Controls.Add(Ui.Actions(ignoreCollation, ignoreOrdinal, ignoreNames, excludeZZ), 0, 0);
        var prefixRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 }; prefixRow.ColumnStyles.Add(new(SizeType.Absolute, 200)); prefixRow.ColumnStyles.Add(new(SizeType.Percent, 100));
        prefixes.PlaceholderText = "例如 TEMP_,BACKUP_；逗號分隔，不支援 SQL 或正規表示式"; prefixRow.Controls.Add(Ui.Label("額外排除物件名稱前綴"), 0, 0); prefixRow.Controls.Add(prefixes, 1, 0); rules.Controls.Add(prefixRow, 0, 1);
        rules.Controls.Add(Ui.Label("目標獨有物件一律保留；沒有自動 DROP 或一鍵同步。", true), 0, 2); layout.Controls.Add(rules, 0, 2);
        foreach (var check in new[] { ignoreCollation, ignoreOrdinal, ignoreNames, excludeZZ }) check.CheckedChanged += (_, _) => { if (!applyingOptions) { ClearComparison(); UpdateScopeDescription(); } };
        prefixes.TextChanged += (_, _) => { if (!applyingOptions) { ClearComparison(); UpdateScopeDescription(); } };
        var explanation = Ui.CodeBox(); explanation.Font = Font; explanation.WordWrap = true;
        explanation.Text = "比對完成狀態\r\n\r\n完整：選定範圍已讀取完成。\r\n部分完成：加密、CLR、權限或某個範圍讀取失敗；其他物件仍保留結果。\r\n保留：檢查 DB 的額外擴充，不產生刪除。\r\n\r\n已知限制\r\nSQL 以文字比對，不判斷語意等價。未比對資料內容、授權、儲存配置、UDT／XML schema 定義或資料庫 DDL Trigger。\r\n兩個資料庫及各次查詢不是同一個一致性快照；請避免比對期間進行 DDL。";
        layout.Controls.Add(explanation, 0, 3);
        var start = Ui.Button("開始唯讀比對 →", true); start.Click += async (_, _) => await CompareAsync(false); layout.Controls.Add(Ui.Actions(start), 0, 4); return layout;
    }

    private Control BuildDifferences()
    {
        var layout = Ui.Rows(new(SizeType.Absolute, 62), new(SizeType.Absolute, 38), new(SizeType.Absolute, 32), new(SizeType.Percent, 100), new(SizeType.Absolute, 48));
        layout.Controls.Add(PageHeading("差異檢視", "檢查 DB 目前內容在左，標準基準在右；勾選物件後建立變更計畫。"), 0, 0);
        var toolbar = new ToolStrip { Dock = DockStyle.Fill, GripStyle = ToolStripGripStyle.Hidden, BackColor = Color.White };
        targetResults.Width = 310; var resultHost = new ToolStripControlHost(targetResults) { AutoSize = false, Width = 315 };
        ToolStripButton refresh = new("重新比對 F5"), retry = new("重試失敗");
        var export = new ToolStripDropDownButton("報告／快照");
        void ExportItem(string label, Func<Task> action) { var item = new ToolStripMenuItem(label); item.Click += async (_, _) => await action(); export.DropDownItems.Add(item); }
        ExportItem("匯出報告（JSON／HTML／CSV）", ExportReportAsync); ExportItem("保存標準結構快照", () => SaveSnapshotAsync(true)); ExportItem("保存檢查結構快照", () => SaveSnapshotAsync(false));
        toolbar.Items.AddRange([resultHost, refresh, retry, export]); layout.Controls.Add(toolbar, 0, 1);
        targetResults.SelectedIndexChanged += (_, _) => { if (!binding) ActivateSelectedResult(); };
        refresh.Click += async (_, _) => await CompareAsync(false); retry.Click += async (_, _) => await CompareAsync(true);
        summary.Dock = DockStyle.Fill; summary.AutoSize = false; summary.AutoEllipsis = true; layout.Controls.Add(summary, 0, 2);
        var split = new SplitContainer { Size = new Size(1100, 550), Dock = DockStyle.Fill, SplitterDistance = 285, SplitterWidth = 8, Panel1MinSize = 220, Panel2MinSize = 340, BackColor = Ui.Background };
        var list = Ui.Rows(new(SizeType.Absolute, 38), new(SizeType.Absolute, 38), new(SizeType.Percent, 100), new(SizeType.Absolute, 40));
        search.PlaceholderText = "搜尋 schema／物件（Ctrl+F）"; list.Controls.Add(search, 0, 0);
        typeFilter.Items.Add("全部類型"); foreach (var category in Enum.GetValues<ObjectCategory>()) typeFilter.Items.Add(Ui.Category(category)); typeFilter.SelectedIndex = 0;
        stateFilter.Items.AddRange(new object[] { "全部狀態", "待處理", "保留", "無法比對" }); stateFilter.SelectedIndex = 0;
        var filters = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 }; filters.ColumnStyles.Add(new(SizeType.Percent, 50)); filters.ColumnStyles.Add(new(SizeType.Percent, 50)); filters.Controls.Add(typeFilter, 0, 0); filters.Controls.Add(stateFilter, 1, 0); list.Controls.Add(filters, 0, 1);
        objects.ReadOnly = false;
        objects.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Selected", HeaderText = "選取", Width = 46, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
        objects.Columns.Add(new DataGridViewTextBoxColumn { Name = "Object", HeaderText = "物件", ReadOnly = true, FillWeight = 76 }); objects.Columns.Add(new DataGridViewTextBoxColumn { Name = "State", HeaderText = "狀態", ReadOnly = true, FillWeight = 24 });
        list.Controls.Add(objects, 0, 2); Button all = Ui.Button("選取可見差異"), none = Ui.Button("全部取消"); list.Controls.Add(Ui.Actions(all, none), 0, 3);
        all.Click += (_, _) => { foreach (DataGridViewRow row in objects.Rows) if (row.Tag is ObjectDifference item && item.CanSelect) selectedIds.Add(item.Id); RefreshObjects(); InvalidatePlan(); };
        none.Click += (_, _) => { selectedIds.Clear(); RefreshObjects(); InvalidatePlan(); };
        objects.CurrentCellDirtyStateChanged += (_, _) => { if (objects.IsCurrentCellDirty) objects.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        objects.CellValueChanged += (_, e) => { if (binding || e.RowIndex < 0 || e.ColumnIndex != 0 || objects.Rows[e.RowIndex].Tag is not ObjectDifference item) return; if (item.CanSelect && objects.Rows[e.RowIndex].Cells[0].Value is true) selectedIds.Add(item.Id); else selectedIds.Remove(item.Id); InvalidatePlan(); UpdateSelection(); };
        objects.SelectionChanged += async (_, _) => { if (!binding) await ShowSelectedObjectAsync(); };
        search.TextChanged += (_, _) => RefreshObjects(); typeFilter.SelectedIndexChanged += (_, _) => RefreshObjects(); stateFilter.SelectedIndexChanged += (_, _) => RefreshObjects();
        var tabs = new TabControl { Dock = DockStyle.Fill };
        foreach (var name in new[] { "項目", "屬性", "檢查 DB", "標準 DB" }) properties.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = name, Name = name });
        tabs.TabPages.Add(Ui.Tab("Git 差異", diff)); tabs.TabPages.Add(Ui.Tab("結構化差異", properties)); tabs.TabPages.Add(Ui.Tab("原始定義", raw)); tabs.TabPages.Add(Ui.Tab("範圍／失敗原因", scopeText));
        split.Panel1.Controls.Add(list); split.Panel2.Controls.Add(tabs); layout.Controls.Add(split, 0, 3);
        var planButton = Ui.Button("建立變更計畫 →", true); planButton.Click += async (_, _) => await GeneratePlanAsync(false); layout.Controls.Add(Ui.Actions(selectionLabel, planButton), 0, 4);
        return layout;
    }

    private Control BuildPlan()
    {
        var layout = Ui.Rows(new(SizeType.Absolute, 70), new(SizeType.Absolute, 44), new(SizeType.Absolute, 50), new(SizeType.Percent, 100));
        layout.Controls.Add(PageHeading("變更計畫與 SQL 草稿", "先閱讀風險與相依性。這裡沒有執行按鈕，也不會直接修改資料庫。"), 0, 0);
        Button rebuild = Ui.Button("重建計畫"), preflight = Ui.Button("唯讀資料預檢"), back = Ui.Button("返回差異");
        rebuild.Click += async (_, _) => await GeneratePlanAsync(false); preflight.Click += async (_, _) => await GeneratePlanAsync(true); back.Click += (_, _) => ShowPage("differences");
        exportSql.Click += async (_, _) => await ExportSqlAsync(); copySql.Click += (_, _) => { if (plan is not null && !plan.Blocked) Ui.Copy(this, plan.Sql); };
        exportSql.Enabled = copySql.Enabled = false; layout.Controls.Add(Ui.Actions(back, rebuild, preflight, exportSql, copySql), 0, 1);
        planLabel.Dock = DockStyle.Fill; planLabel.AutoSize = false; planLabel.AutoEllipsis = true; layout.Controls.Add(planLabel, 0, 2);
        foreach (var (name, weight) in new[] { ("風險", 10), ("物件", 22), ("操作", 22), ("說明", 46) }) planGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = name, FillWeight = weight });
        var split = new SplitContainer { Size = new Size(1000, 540), Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 200, SplitterWidth = 8, Panel1MinSize = 90, Panel2MinSize = 120 };
        split.Panel1.Controls.Add(planGrid); split.Panel2.Controls.Add(sql); layout.Controls.Add(split, 0, 3); return layout;
    }

    private Control BuildHistory()
    {
        var layout = Ui.Rows(new(SizeType.Absolute, 70), new(SizeType.Absolute, 44), new(SizeType.Percent, 45), new(SizeType.Percent, 55));
        layout.Controls.Add(PageHeading("歷史紀錄與操作手順", "自動只保存摘要；完整定義、快照與資料 SQL 只在你明確匯出時寫入。"), 0, 0);
        Button refresh = Ui.Button("重新載入紀錄"), snapshots = Ui.Button("離線比對兩份快照", true), workflow = Ui.Button("查看操作手順");
        refresh.Click += async (_, _) => await RunAsync(LoadHistoryAsync); snapshots.Click += async (_, _) => await CompareSnapshotsAsync(); workflow.Click += (_, _) => historyDetail.Text = WorkflowText;
        layout.Controls.Add(Ui.Actions(refresh, snapshots, workflow), 0, 1);
        foreach (var name in new[] { "時間", "標準", "檢查", "完整", "待處理" }) historyGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = name, Name = name });
        historyGrid.SelectionChanged += (_, _) =>
        {
            if (historyGrid.CurrentRow?.Tag is not HistoryEntry entry) return;
            var previous = historyGrid.Rows.Cast<DataGridViewRow>().Select(r => r.Tag).OfType<HistoryEntry>().FirstOrDefault(h => h.Time < entry.Time && h.Source == entry.Source && h.Target == entry.Target);
            var delta = previous is null ? null : WorkspaceStore.HistoryDelta(entry, previous);
            historyDetail.Text = $"時間：{entry.Time:O}\r\n{entry.Source} → {entry.Target}\r\n{entry.Options.Description}\r\n完整：{entry.Complete}；待處理：{entry.Required}；保留：{entry.Retained}；問題：{entry.Issues}\r\n" + (delta is { } d ? $"相較上一份同範圍完整紀錄：新增 {d.Added}，已解決 {d.Resolved} 個待處理物件。" : "沒有可比較的同範圍完整紀錄；不推論已解決數量。") + "\r\n\r\n" + string.Join("\r\n", entry.RequiredObjects);
        };
        layout.Controls.Add(historyGrid, 0, 2); historyDetail.WordWrap = true; historyDetail.Font = Font; historyDetail.Text = WorkflowText; layout.Controls.Add(historyDetail, 0, 3); return layout;
    }

    private void AddPage(string key, string label, Control content)
    {
        content.Dock = DockStyle.Fill; content.Visible = false; pages[key] = content; pagesHost.Controls.Add(content);
        var button = Ui.Button(label); button.AutoSize = false; button.Width = 166; button.Height = 48; button.TextAlign = ContentAlignment.MiddleLeft; button.BackColor = Ui.Sidebar; button.ForeColor = Color.FromArgb(206, 219, 229); button.FlatAppearance.BorderSize = 0; button.Margin = new Padding(0, 0, 0, 10);
        button.Click += async (_, _) => { ShowPage(key); if (key == "history" && !demo) await RunAsync(LoadHistoryAsync); }; navButtons[key] = button; navigation.Controls.Add(button);
    }
    internal void ShowPage(string key)
    {
        foreach (var pair in pages) pair.Value.Visible = pair.Key == key;
        pages[key].BringToFront();
        foreach (var pair in navButtons) { pair.Value.BackColor = pair.Key == key ? Ui.Accent : Ui.Sidebar; pair.Value.ForeColor = pair.Key == key ? Color.White : Color.FromArgb(206, 219, 229); }
    }
    private static Control PageHeading(string title, string subtitle)
    {
        var panel = Ui.Rows(new(SizeType.Absolute, 35), new(SizeType.Percent, 100)); var heading = Ui.Label(title); heading.Font = new Font("Microsoft JhengHei UI", 17, FontStyle.Bold); panel.Controls.Add(heading, 0, 0);
        var detail = Ui.Label(subtitle, true); detail.AutoSize = false; detail.Dock = DockStyle.Fill; detail.AutoEllipsis = true; panel.Controls.Add(detail, 0, 1); return panel;
    }
    private static CheckBox Check(string text, bool value) => new() { Text = text, Checked = value, AutoSize = true, Margin = new Padding(0, 7, 20, 5) };
    private static ComboBox Combo() => new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 3, 6, 4) };
    internal const string WorkflowText = "建議操作手順\r\n\r\n1. 環境與基準：輸入兩側連線並測試；需要批次檢查時，逐一加入已驗證目標。也可載入標準快照。\r\n2. 比對範圍：選擇物件類型、忽略規則及排除前綴，按開始比對。\r\n3. 差異檢視：先閱讀完成狀態；搜尋物件，檢視左右／合併 Diff，勾選要處理的差異。保留物件不能勾選部署。\r\n4. 變更計畫：檢查相依性與風險；必要時執行唯讀資料預檢。阻擋項目未處理前不會產生可執行 SQL。\r\n5. 匯出 SQL 草稿：由有權限的人員在正確目標、測試及備份確認後另行執行。工具本身不執行任何修改。\r\n6. 回到差異檢視重新比對，查看仍待處理項目；可匯出報告或保存結構快照。\r\n\r\n資料工具：獨立載入所有來源資料表；選取欄位、主鍵／唯一索引、條件與筆數。預設只補缺少資料，不覆寫既有資料。\r\n\r\n快捷鍵：F5 重新比對、Ctrl+F 搜尋、F7 下一處差異、Shift+F7 上一處、Esc 取消工作。\r\n\r\n歷史摘要保存在目前 Windows 使用者的 LocalAppData/WeYu/DbCheck；只保留最近 100 份。快照／SQL 可能含敏感定義或資料，請自行妥善保管。";
}
