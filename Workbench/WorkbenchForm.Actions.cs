using System.Xml.Linq;

namespace DbCheck.Workbench;

internal sealed partial class WorkbenchForm
{
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (busy || lifetime.IsCancellationRequested) return;
        busy = true; operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = operation.Token;
        pagesHost.Enabled = navigation.Enabled = false; cancel.Enabled = true; progress.Visible = true;
        status.ForeColor = Ui.Muted;
        try { await action(token); }
        catch (OperationCanceledException) { if (!IsDisposed) status.Text = "工作已取消；已完成的目標結果保留，未完成項目不視為一致。"; }
        catch (Exception ex)
        {
            if (!IsDisposed && !lifetime.IsCancellationRequested) { status.Text = SqlText.Failure(ex); status.ForeColor = Ui.Warning; Ui.Error(this, ex); }
        }
        finally
        {
            operation.Dispose(); operation = null; busy = false;
            if (!IsDisposed && !lifetime.IsCancellationRequested)
            { pagesHost.Enabled = navigation.Enabled = true; source.Enabled = !useSnapshot.Checked; cancel.Enabled = false; progress.Visible = false; }
        }
    }

    private ComparisonOptions ReadOptions()
    {
        var options = new ComparisonOptions
        {
            Scope = scopeChecks.Where(p => p.Value.Checked).Aggregate(ComparisonScope.None, (scope, pair) => scope | pair.Key),
            IgnoreCollation = ignoreCollation.Checked, IgnoreColumnOrder = ignoreOrdinal.Checked, IgnoreIndexNames = ignoreNames.Checked,
            ExcludeZZ = excludeZZ.Checked, ExcludedPrefixes = prefixes.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        };
        options.Validate(); return options;
    }
    private void ApplyOptions(ComparisonOptions options)
    {
        applyingOptions = true;
        try
        {
            foreach (var pair in scopeChecks) pair.Value.Checked = options.Scope.HasFlag(pair.Key);
            ignoreCollation.Checked = options.IgnoreCollation; ignoreOrdinal.Checked = options.IgnoreColumnOrder; ignoreNames.Checked = options.IgnoreIndexNames;
            excludeZZ.Checked = options.ExcludeZZ; prefixes.Text = string.Join(",", options.ExcludedPrefixes);
        }
        finally { applyingOptions = false; }
        ClearComparison(); UpdateScopeDescription();
    }
    private void UpdateScopeDescription()
    {
        try { status.Text = ReadOptions().Description; status.ForeColor = Ui.Muted; }
        catch (ArgumentException ex) { status.Text = ex.Message; status.ForeColor = Ui.Warning; }
    }

    private async Task CompareAsync(bool retryOnly) => await RunAsync(async token =>
    {
        var options = ReadOptions();
        if (demo) { LoadDemo(options); status.Text = "離線合成資料比對完成；未連接資料庫。"; return; }
        TargetEndpoint[] targets;
        if (retryOnly)
        {
            targets = failures.Keys.Where(endpoints.ContainsKey).Select(k => endpoints[k]).ToArray();
            if (targets.Length == 0) throw new InvalidOperationException("沒有可重試的失敗目標；可使用重新比對再次驗證。");
        }
        else
        {
            targets = targetQueue.Items.Cast<TargetEndpoint>().ToArray();
            if (targets.Length == 0)
            {
                if (target.VerifiedConnection is null || target.VerifiedLabel is null) throw new InvalidOperationException("請先測試檢查 DB，或加入已驗證的批次目標。");
                targets = [new(target.VerifiedLabel, target.VerifiedConnection)];
            }
            ClearComparison();
        }
        if (!useSnapshot.Checked && source.VerifiedConnection is null) throw new InvalidOperationException("請先測試標準 DB 連線。");
        if (useSnapshot.Checked && baseline is null) throw new InvalidOperationException("請先載入標準快照。");
        foreach (var item in targets) { endpoints[item.Label] = item; failures[item.Label] = "尚未完成"; }
        var reporter = new Progress<string>(text => { if (!IsDisposed && !token.IsCancellationRequested) status.Text = text; });
        try
        {
            CatalogSnapshot standard;
            try { standard = useSnapshot.Checked ? baseline! : await new CatalogService().ReadAsync(source.VerifiedConnection!, options, token, reporter); }
            catch (Exception ex)
            { foreach (var item in targets) failures[item.Label] = "標準讀取失敗：" + SqlText.Failure(ex); throw; }
            foreach (var item in targets)
            {
                token.ThrowIfCancellationRequested();
                status.Text = "比對目標：" + item.Label;
                try
                {
                    var actual = await new CatalogService().ReadAsync(item.Connection, options, token, reporter);
                    var report = await Task.Run(() => ComparisonEngine.Compare(standard, actual, options, token), token);
                    sessions[item.Label] = new(standard, actual, report);
                    if (report.Complete) failures.Remove(item.Label);
                    else failures[item.Label] = "部分完成；可檢视已完成的物件，並重試讀取失敗項目。";
                    try { await WorkspaceStore.SaveHistoryAsync(report, token); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { status.Text = "比對結果保留，但本機歷史摘要寫入失敗。"; }
                }
                catch (OperationCanceledException) { failures[item.Label] = "已取消，尚未完成。"; throw; }
                catch (Exception ex)
                { sessions.Remove(item.Label); failures[item.Label] = SqlText.Failure(ex); }
            }
            status.Text = $"完成 {sessions.Count} 個目標結果；{failures.Count} 個目標需重試／確認。未檢查範圍不代表一致。";
        }
        finally
        {
            if (!IsDisposed && !lifetime.IsCancellationRequested) { PopulateResults(); ShowPage("differences"); }
        }
    });

    private void PopulateResults()
    {
        binding = true;
        try
        {
            targetResults.Items.Clear();
            foreach (var name in sessions.Keys.Union(failures.Keys, StringComparer.Ordinal)) targetResults.Items.Add(new ResultChoice(name, !sessions.ContainsKey(name)));
            if (targetResults.Items.Count > 0) targetResults.SelectedIndex = 0;
        }
        finally { binding = false; }
        ActivateSelectedResult();
    }
    private void ActivateSelectedResult()
    {
        if (targetResults.SelectedItem is not ResultChoice choice) return;
        selectedIds.Clear(); InvalidatePlan();
        if (!sessions.TryGetValue(choice.Label, out active))
        {
            active = null; objects.Rows.Clear(); properties.Rows.Clear(); diff.Clear(); raw.Clear();
            summary.Text = "此目標未完成比對，不能判定一致。"; summary.ForeColor = Ui.Warning;
            scopeText.Text = failures.GetValueOrDefault(choice.Label, "未取得結果。"); return;
        }
        summary.Text = active.Report.Summary + $"   保留 {active.Report.Differences.Count(d => d.State == DifferenceState.Retained)} 個目標額外物件。";
        summary.ForeColor = active.Report.Complete ? Ui.Ink : Ui.Warning;
        scopeText.Text = $"標準：{active.Source.Label}\r\n擷取時間：{active.Source.CapturedAt:O}\r\n檢查：{active.Target.Label}\r\n擷取時間：{active.Target.CapturedAt:O}\r\n\r\n{active.Report.Options.Description}\r\n\r\n{active.Report.ConsistencyNote}\r\n\r\n讀取／範圍問題\r\n" + (active.Report.Issues.Count == 0 ? "沒有讀取問題。" : string.Join("\r\n", active.Report.Issues)) + "\r\n\r\n其他目標問題\r\n" + string.Join("\r\n", failures.Select(p => p.Key + "：" + p.Value));
        RefreshObjects();
    }
    private void RefreshObjects()
    {
        if (binding) return;
        var previous = (objects.CurrentRow?.Tag as ObjectDifference)?.Id;
        var text = search.Text.Trim();
        binding = true;
        try
        {
            objects.Rows.Clear();
            if (active is not null)
            {
                var visible = active.Report.Differences.Where(d => (text.Length == 0 || d.Key.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || d.Key.Schema.Contains(text, StringComparison.OrdinalIgnoreCase))
                    && (typeFilter.SelectedIndex <= 0 || Ui.Category(d.Category) == Convert.ToString(typeFilter.SelectedItem))
                    && (stateFilter.SelectedIndex switch { 1 => d.CanSelect, 2 => d.State == DifferenceState.Retained, 3 => d.State == DifferenceState.Unverifiable, _ => true }));
                foreach (var item in visible)
                {
                    var index = objects.Rows.Add(item.CanSelect && selectedIds.Contains(item.Id), item.Key.Sql, Ui.State(item.State));
                    var row = objects.Rows[index]; row.Tag = item; row.Cells[0].ReadOnly = !item.CanSelect;
                    row.Cells[1].ToolTipText = Ui.Category(item.Category) + " · " + item.Key.Sql + "\r\n" + item.Message;
                    if (!item.CanSelect) row.DefaultCellStyle.ForeColor = item.State == DifferenceState.Unverifiable ? Ui.Warning : Ui.Muted;
                }
            }
            if (objects.Rows.Count > 0)
            {
                var row = objects.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => (r.Tag as ObjectDifference)?.Id == previous) ?? objects.Rows[0];
                objects.CurrentCell = row.Cells[1];
            }
        }
        finally { binding = false; }
        UpdateSelection(); _ = ShowSelectedObjectAsync();
    }
    private async Task ShowSelectedObjectAsync()
    {
        if (IsDisposed || lifetime.IsCancellationRequested) return;
        if (objects.CurrentRow?.Tag is not ObjectDifference item) { properties.Rows.Clear(); raw.Clear(); diff.Clear(); return; }
        properties.Rows.Clear();
        foreach (var change in item.Properties) properties.Rows.Add(change.Subject + (change.Retained ? "（保留）" : ""), change.Property, change.Current ?? "（不存在）", change.Standard ?? "（不存在）");
        var currentModule = active?.Target.Modules.SingleOrDefault(m => m.Key == item.Key);
        var standardModule = active?.Source.Modules.SingleOrDefault(m => m.Key == item.Key);
        raw.Text = "檢查 DB（目前）\r\n" + (currentModule?.Definition ?? item.CurrentText) + "\r\n\r\n標準 DB（基準）\r\n" + (standardModule?.Definition ?? item.StandardText) + "\r\n\r\n" + item.Message;
        try { await diff.ShowDiffAsync(item.CurrentText, item.StandardText, lifetime.Token); }
        catch (Exception ex) when (ex is not OperationCanceledException) { if (!IsDisposed) status.Text = "差異顯示失敗：" + SqlText.Failure(ex); }
    }
    private void UpdateSelection()
    {
        var visibleSelected = objects.Rows.Cast<DataGridViewRow>().Count(r => r.Tag is ObjectDifference d && selectedIds.Contains(d.Id));
        selectionLabel.Text = $"已選 {selectedIds.Count} 個物件（目前篩選隱藏 {selectedIds.Count - visibleSelected} 個）";
    }
    private ObjectDifference[] Selected() => active?.Report.Differences.Where(d => d.CanSelect && selectedIds.Contains(d.Id)).ToArray() ?? [];
    private void InvalidatePlan()
    {
        plan = null; findings = null; sql.Clear(); planGrid.Rows.Clear(); exportSql.Enabled = copySql.Enabled = false;
        planLabel.Text = "選取或基準變更後需重新建立計畫；先前預檢結果不再沿用。";
    }
    private void ClearComparison()
    {
        active = null; sessions.Clear(); endpoints.Clear(); failures.Clear(); selectedIds.Clear();
        binding = true;
        try { targetResults.Items.Clear(); objects.Rows.Clear(); properties.Rows.Clear(); }
        finally { binding = false; }
        diff.Clear(); raw.Clear(); scopeText.Clear(); summary.Text = "設定已變更，請重新比對。"; InvalidatePlan(); UpdateSelection();
    }

    private async Task GeneratePlanAsync(bool runPreflight)
    {
        if (active is null || Selected().Length == 0) { Ui.Error(this, new InvalidOperationException("請先勾選至少一個待處理物件。")); return; }
        if (runPreflight && MessageBox.Show(this, "資料預檢會讀取檢查 DB 的資料，可能掃描整張表，並需要 SELECT 權限。確定執行唯讀預檢？", "資料預檢", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        await RunAsync(async token =>
        {
            var session = active!; var selected = Selected();
            if (runPreflight)
            {
                if (demo || targetResults.SelectedItem is not ResultChoice choice || !endpoints.TryGetValue(choice.Label, out var endpoint)) throw new InvalidOperationException("離線快照／示範結果不能執行資料預檢；請使用已驗證的即時目標。");
                // Do not retain a stale successful plan if preflight fails or is cancelled.
                InvalidatePlan();
                findings = await new PreflightService().RunAsync(endpoint.Connection, session, selected, token, new Progress<string>(text => { if (!IsDisposed && !token.IsCancellationRequested) status.Text = text; }));
            }
            plan = await Task.Run(() => ScriptPlanner.Build(session, selected, findings), token);
            token.ThrowIfCancellationRequested(); RenderPlan(); ShowPage("plan");
            status.Text = plan.Blocked ? "計畫有阻擋項目，未產生可執行 SQL。" : "SQL 草稿已產生，尚未執行。請檢閱所有待確認項目。";
        });
    }
    private void RenderPlan()
    {
        planGrid.Rows.Clear();
        if (plan is null) return;
        foreach (var step in plan.Steps)
        {
            var index = planGrid.Rows.Add(Ui.Risk(step.Risk), step.Object.Sql, step.Action, step.Note); planGrid.Rows[index].Tag = step;
            planGrid.Rows[index].Cells[3].ToolTipText = step.Note;
            if (step.Risk == RiskLevel.Blocked) planGrid.Rows[index].DefaultCellStyle.ForeColor = Color.FromArgb(170, 45, 45);
        }
        sql.Text = plan.Sql.Length > 2_000_000 ? plan.Sql[..2_000_000] + "\r\n-- 畫面預覽已截斷；另存包含完整草稿。" : plan.Sql;
        planLabel.Text = $"阻擋 {plan.Steps.Count(s => s.Risk == RiskLevel.Blocked)} · 待確認 {plan.Steps.Count(s => s.Risk == RiskLevel.Review)} · {Selected().Length} 個選取物件。" + (findings is null ? " 尚未執行資料預檢；不代表資料相容。" : " 已完成本次唯讀資料預檢；資料仍可能變動。");
        planLabel.ForeColor = plan.Blocked ? Ui.Warning : Ui.Muted;
        exportSql.Enabled = copySql.Enabled = !plan.Blocked && plan.Steps.Any(s => !string.IsNullOrEmpty(s.Sql));
    }
    private async Task ExportSqlAsync() => await RunAsync(async token =>
    {
        if (plan is null || plan.Blocked) throw new InvalidOperationException("請先處理計畫的阻擋項目。");
        using var dialog = new SaveFileDialog { Filter = "SQL 草稿|*.sql", FileName = "schema-review.sql", OverwritePrompt = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) { await WorkspaceStore.WriteTextAsync(dialog.FileName, plan.Sql, token); status.Text = "已匯出 SQL 草稿；未執行。"; }
    });
    private async Task ExportReportAsync() => await RunAsync(async token =>
    {
        if (active is null) throw new InvalidOperationException("請先選擇一份比對結果。");
        using var dialog = new SaveFileDialog { Filter = "HTML 報告|*.html|JSON 報告|*.json|CSV 差異|*.csv", FileName = "comparison-report.html", OverwritePrompt = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) { await WorkspaceStore.SaveReportAsync(dialog.FileName, active.Report, token); status.Text = "報告已匯出；包含物件定義，請妥善保管。"; }
    });
    private async Task SaveSnapshotAsync(bool standard) => await RunAsync(async token =>
    {
        if (active is null) throw new InvalidOperationException("請先完成比對。");
        using var dialog = new SaveFileDialog { Filter = "結構快照|*.json", FileName = standard ? "standard.snapshot.json" : "check.snapshot.json", OverwritePrompt = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) { await WorkspaceStore.SaveSnapshotAsync(dialog.FileName, standard ? active.Source : active.Target, token); status.Text = "結構快照已保存；不含帳密或資料列，但包含 SQL 定義。"; }
    });
    private async Task LoadBaselineAsync() => await RunAsync(async token =>
    {
        using var dialog = new OpenFileDialog { Filter = "結構快照|*.json", CheckFileExists = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        baseline = await WorkspaceStore.LoadSnapshotAsync(dialog.FileName, token); ApplyOptions(baseline.Options);
        useSnapshot.Checked = true; source.Enabled = false; snapshotLabel.Text = baseline.Label + $" · {baseline.CapturedAt:yyyy-MM-dd HH:mm}";
        status.Text = "標準快照已載入並套用其採集範圍；只應使用可信來源的快照。";
    });
    private async Task CompareSnapshotsAsync() => await RunAsync(async token =>
    {
        using var standardDialog = new OpenFileDialog { Title = "選擇標準快照", Filter = "結構快照|*.json", CheckFileExists = true };
        if (standardDialog.ShowDialog(this) != DialogResult.OK) return;
        using var actualDialog = new OpenFileDialog { Title = "選擇檢查快照", Filter = "結構快照|*.json", CheckFileExists = true };
        if (actualDialog.ShowDialog(this) != DialogResult.OK) return;
        var standard = await WorkspaceStore.LoadSnapshotAsync(standardDialog.FileName, token); var actual = await WorkspaceStore.LoadSnapshotAsync(actualDialog.FileName, token);
        ClearComparison(); ApplyOptions(standard.Options);
        var report = await Task.Run(() => ComparisonEngine.Compare(standard, actual, standard.Options, token), token);
        sessions[actual.Label] = new(standard, actual, report); PopulateResults(); ShowPage("differences"); status.Text = "離線比對完成；未連接資料庫。快照的採集時間與範圍請在結果中檢閱。";
    });

    private async Task SaveProfileAsync(EndpointEditor endpoint)
    {
        var name = Ui.Prompt(this, "保存環境", "只保存非敏感連線設定，不保存帳號與密碼。環境名稱：", endpoint.SelectedProfile ?? "");
        if (name is null) return;
        await RunAsync(async token => { await WorkspaceStore.SaveProfileAsync(name, endpoint.BuildConnection(), token); await RefreshProfilesAsync(token); status.Text = "環境已保存，帳號與密碼未保存。"; });
    }
    private async Task DeleteProfileAsync(EndpointEditor endpoint)
    {
        if (endpoint.SelectedProfile is not { } name) return;
        if (MessageBox.Show(this, "移除本機保存的環境「" + name + "」？不會刪除資料庫。", "移除環境", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        await RunAsync(async token => { await WorkspaceStore.DeleteProfileAsync(name, token); await RefreshProfilesAsync(token); });
    }
    private async Task RefreshProfilesAsync(CancellationToken token)
    {
        var profiles = await WorkspaceStore.LoadProfilesAsync(token); source.LoadProfiles(profiles); target.LoadProfiles(profiles);
    }
    private void LoadLocalConnections()
    {
        var sourceValue = Environment.GetEnvironmentVariable("WEYU_DBCHECK_SOURCE"); var targetValue = Environment.GetEnvironmentVariable("WEYU_DBCHECK_TARGET");
        var path = Path.Combine(AppContext.BaseDirectory, "app.config");
        if (File.Exists(path))
        {
            var config = XDocument.Load(path);
            string? Read(string name) => config.Root?.Element("connectionStrings")?.Elements("add").SingleOrDefault(e => (string?)e.Attribute("name") == name)?.Attribute("connectionString")?.Value;
            sourceValue ??= Read("MES-H5-DB"); targetValue ??= Read("Project-DB");
        }
        if (!string.IsNullOrWhiteSpace(sourceValue)) source.LoadConnection(sourceValue);
        if (!string.IsNullOrWhiteSpace(targetValue)) target.LoadConnection(targetValue);
    }
    private async Task LoadHistoryAsync(CancellationToken token)
    {
        var history = await WorkspaceStore.LoadHistoryAsync(token); historyGrid.Rows.Clear();
        foreach (var entry in history)
        {
            var index = historyGrid.Rows.Add(entry.Time.ToString("yyyy-MM-dd HH:mm:ss"), entry.Source, entry.Target, entry.Complete ? "是" : "部分", entry.Required);
            historyGrid.Rows[index].Tag = entry;
        }
        if (history.Count == 0) historyDetail.Text = WorkflowText;
    }
    private void LoadDemo(ComparisonOptions? options = null)
    {
        ClearComparison(); var session = DemoData.CreateSession(options); sessions[session.Target.Label] = session;
        PopulateResults(); ShowPage("differences");
        foreach (DataGridViewRow row in objects.Rows)
            if (row.Tag is ObjectDifference item && item.Category == ObjectCategory.View) { objects.CurrentCell = row.Cells[1]; diff.SetDocument(item.CurrentText, item.StandardText); break; }
        status.Text = "離線示範：所有環境、結構與資料均為合成範例；未連接任何資料庫。";
    }
    internal void PrepareDemoPage(string page)
    {
        if (!demo) throw new InvalidOperationException("Only available in synthetic demo mode.");
        if (active is null) LoadDemo();
        if (page == "plan")
        {
            selectedIds.Clear();
            foreach (var item in active!.Report.Differences.Where(d => d.CanSelect && d.Category is ObjectCategory.Table or ObjectCategory.View or ObjectCategory.Procedure)) selectedIds.Add(item.Id);
            plan = ScriptPlanner.Build(active, Selected()); RenderPlan();
        }
        ShowPage(page); PerformLayout();
    }
}
