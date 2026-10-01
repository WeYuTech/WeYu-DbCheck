namespace DbCheck.Workbench;

internal sealed class DataToolsControl : UserControl
{
    private sealed record TableChoice(TableModel Table) { public override string ToString() => Table.Key.Sql; }
    private sealed record KeyChoice(IndexModel Index) { public override string ToString() => Index.Name + (Index.IsPrimaryKey ? "（主鍵）" : "（唯一鍵）"); }
    private sealed record ColumnChoice(ColumnModel Column) { public override string ToString() => Column.Name + "  " + Column.SqlType; }
    private sealed record FilterChoice(ColumnModel? Column) { public override string ToString() => Column?.Name ?? "（不篩選）"; }
    private sealed record OperatorChoice(DataFilterOperator Value, string Text) { public override string ToString() => Text; }
    private readonly Func<string> sourceConnection;
    private readonly Func<string?> targetConnection;
    private readonly Func<Func<CancellationToken, Task>, Task> run;
    private readonly bool demo;
    private readonly ComboBox tables = Combo(), keys = Combo(), filterColumns = Combo(), filterOperators = Combo();
    private readonly TextBox search = Ui.TextBox(), filterValue = Ui.TextBox();
    private readonly NumericUpDown limit = new() { Minimum = 1, Maximum = DataService.MaximumRows, Value = 100, Dock = DockStyle.Fill };
    private readonly CheckBox descending = new() { Text = "主鍵逆排 DESC", AutoSize = true }, updates = new() { Text = "另外產生 UPDATE（預設關閉）", AutoSize = true };
    private readonly CheckedListBox columns = new() { Dock = DockStyle.Fill, CheckOnClick = true, BorderStyle = BorderStyle.FixedSingle, HorizontalScrollbar = true };
    private readonly Label status = Ui.Label("先載入標準 DB 的資料表；不需要結構有差異。", true);
    private readonly TextBox sql = Ui.CodeBox();
    private readonly DataGridView results = Ui.Grid();
    private readonly GitDiffView diff = new();
    private readonly Button save = Ui.Button("匯出 SQL"), copy = Ui.Button("複製 SQL");
    private CatalogSnapshot? catalog;
    private DataResult? result;
    private bool binding;

    public DataToolsControl(Func<string> sourceConnection, Func<string?> targetConnection, Func<Func<CancellationToken, Task>, Task> run, bool demo = false)
    {
        this.sourceConnection = sourceConnection; this.targetConnection = targetConnection; this.run = run; this.demo = demo;
        Dock = DockStyle.Fill; BackColor = Ui.Background;
        var title = Ui.Label("資料匯出與補齊"); title.Font = new Font(Font.FontFamily, 17, FontStyle.Bold);
        var hint = Ui.Label("使用步驟 1 的即時標準／檢查 DB，不使用離線快照或批次目標。上限是取樣範圍，不代表全表一致。", true);
        var header = Ui.Rows(new(SizeType.Absolute, 36), new(SizeType.Absolute, 38)); header.Controls.Add(title, 0, 0); header.Controls.Add(hint, 0, 1);
        var load = Ui.Button("載入／更新資料表", true); var export = Ui.Button("匯出來源資料"); var compare = Ui.Button("比對並產生補缺 SQL", true);
        var all = Ui.Button("欄位全選"); var none = Ui.Button("清除勾選");
        var collapse = Ui.Button("收合設定");
        var configuration = new SplitContainer { Size = new Size(1000, 235), Dock = DockStyle.Fill, SplitterDistance = 270, Panel1MinSize = 180, Panel2MinSize = 380, BackColor = Ui.Border };
        var selection = Ui.Rows(new(SizeType.Absolute, 36), new(SizeType.Percent, 100), new(SizeType.Absolute, 38));
        selection.Controls.Add(Ui.Label("輸出欄位（鍵欄位自動加入）"), 0, 0); selection.Controls.Add(columns, 0, 1); selection.Controls.Add(Ui.Actions(all, none), 0, 2);
        configuration.Panel1.Padding = new Padding(10); configuration.Panel1.BackColor = Color.White; configuration.Panel1.Controls.Add(selection);
        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 5, Padding = new Padding(12), BackColor = Color.White };
        fields.ColumnStyles.Add(new(SizeType.Absolute, 64)); fields.ColumnStyles.Add(new(SizeType.Percent, 50)); fields.ColumnStyles.Add(new(SizeType.Absolute, 64)); fields.ColumnStyles.Add(new(SizeType.Percent, 50));
        for (var i = 0; i < 5; i++) fields.RowStyles.Add(new(SizeType.Absolute, 40));
        void Field(string label, Control control, int row, int column = 0) { fields.Controls.Add(Ui.Label(label), column, row); fields.Controls.Add(control, column + 1, row); }
        search.PlaceholderText = "搜尋 schema／資料表";
        Field("搜尋", search, 0); Field("資料表", tables, 0, 2); Field("唯一鍵", keys, 1); Field("筆數", limit, 1, 2);
        Field("條件欄", filterColumns, 2); Field("運算子", filterOperators, 2, 2); Field("條件值", filterValue, 3);
        fields.Controls.Add(descending, 3, 3); fields.Controls.Add(updates, 0, 4); fields.SetColumnSpan(updates, 4);
        filterValue.PlaceholderText = "日期使用 ISO 8601；不接受 SQL WHERE 片段";
        foreach (var choice in new[] { new OperatorChoice(DataFilterOperator.Equal, "等於"), new(DataFilterOperator.NotEqual, "不等於"), new(DataFilterOperator.Greater, "大於"), new(DataFilterOperator.GreaterOrEqual, "大於等於"), new(DataFilterOperator.Less, "小於"), new(DataFilterOperator.LessOrEqual, "小於等於"), new(DataFilterOperator.Contains, "包含"), new(DataFilterOperator.StartsWith, "開頭為"), new(DataFilterOperator.IsNull, "IS NULL"), new(DataFilterOperator.IsNotNull, "IS NOT NULL") }) filterOperators.Items.Add(choice);
        filterOperators.SelectedIndex = 0; configuration.Panel2.Controls.Add(fields);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var review = new SplitContainer { Size = new Size(1000, 300), Dock = DockStyle.Fill, SplitterDistance = 270, Panel1MinSize = 160, Panel2MinSize = 300 };
        results.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "唯一鍵", Name = "Key", FillWeight = 65 });
        results.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "狀態", Name = "State", FillWeight = 35 });
        review.Panel1.Controls.Add(results); review.Panel2.Controls.Add(diff);
        tabs.TabPages.Add(Ui.Tab("資料差異", review)); tabs.TabPages.Add(Ui.Tab("SQL 草稿", sql));
        var layout = Ui.Rows(new(SizeType.Absolute, 74), new(SizeType.Absolute, 240), new(SizeType.Absolute, 52), new(SizeType.Absolute, 58), new(SizeType.Percent, 100));
        layout.Controls.Add(header, 0, 0); layout.Controls.Add(configuration, 0, 1); layout.Controls.Add(Ui.Actions(load, export, compare, save, copy, collapse), 0, 2);
        status.AutoSize = false; status.Dock = DockStyle.Fill; status.AutoEllipsis = true; layout.Controls.Add(status, 0, 3); layout.Controls.Add(tabs, 0, 4); Controls.Add(layout);
        collapse.Click += (_, _) => { configuration.Visible = !configuration.Visible; layout.RowStyles[1].Height = configuration.Visible ? 240 : 0; collapse.Text = configuration.Visible ? "收合設定" : "展開設定"; };
        load.Click += async (_, _) => await run(async token =>
        {
            catalog = demo ? DemoData.CreateSession().Source : await new CatalogService().ReadAsync(sourceConnection(), new ComparisonOptions { Scope = ComparisonScope.Tables }, token);
            if (!catalog.CompletedScopes.HasFlag(ComparisonScope.Tables)) throw new InvalidOperationException("資料表 metadata 未完整讀取，請確認權限。");
            FilterTables(); status.Text = $"標準：{catalog.Label}；已載入 {catalog.Tables.Count} 個資料表。無主鍵時可選有效唯一索引。";
        });
        export.Click += async (_, _) => await ExecuteAsync(false);
        compare.Click += async (_, _) => await ExecuteAsync(true);
        save.Click += async (_, _) => await run(async token => { if (result is null || result.Sql.Length == 0) return; using var dialog = new SaveFileDialog { Filter = "SQL 草稿|*.sql", FileName = "data-review.sql", OverwritePrompt = true }; if (dialog.ShowDialog(this) == DialogResult.OK) await WorkspaceStore.WriteTextAsync(dialog.FileName, result.Sql, token); });
        copy.Click += (_, _) => Ui.Copy(this, result?.Sql ?? "");
        all.Click += (_, _) => { for (var i = 0; i < columns.Items.Count; i++) columns.SetItemChecked(i, i < 256); };
        none.Click += (_, _) => { for (var i = 0; i < columns.Items.Count; i++) columns.SetItemChecked(i, false); };
        search.TextChanged += (_, _) => FilterTables(); tables.SelectedIndexChanged += (_, _) => SelectTable();
        keys.SelectedIndexChanged += (_, _) => InvalidateResult(); filterColumns.SelectedIndexChanged += (_, _) => InvalidateResult(); filterOperators.SelectedIndexChanged += (_, _) => InvalidateResult();
        filterValue.TextChanged += (_, _) => InvalidateResult(); limit.ValueChanged += (_, _) => InvalidateResult(); descending.CheckedChanged += (_, _) => InvalidateResult(); updates.CheckedChanged += (_, _) => InvalidateResult(); columns.ItemCheck += (_, _) => InvalidateResult();
        results.SelectionChanged += async (_, _) => { if (results.CurrentRow?.Tag is DataDifference item) await diff.ShowDiffAsync(item.Current, item.Standard); };
        save.Enabled = copy.Enabled = false;
        if (demo) { catalog = DemoData.CreateSession().Source; FilterTables(); }
    }

    private async Task ExecuteAsync(bool reconcile) => await run(async token =>
    {
        if (demo) { status.Text = "離線示範模式不會連接或讀取資料庫；正式操作請重新以一般模式啟動。"; return; }
        if (tables.SelectedItem is not TableChoice choice || keys.SelectedItem is not KeyChoice key) throw new InvalidOperationException("請先載入資料表並選擇有效唯一鍵。");
        var filterColumn = (filterColumns.SelectedItem as FilterChoice)?.Column;
        var filter = filterColumn is null ? null : new DataFilter(filterColumn.Name, ((OperatorChoice)filterOperators.SelectedItem!).Value, filterValue.Text);
        var request = new DataRequest(choice.Table.Key, columns.CheckedItems.Cast<ColumnChoice>().Select(c => c.Column.Name).ToArray(), DataService.KeyColumns(key.Index), (int)limit.Value, descending.Checked, filter);
        InvalidateResult();
        var source = sourceConnection();
        var fresh = await new CatalogService().ReadAsync(source, new ComparisonOptions { Scope = ComparisonScope.Tables }, token);
        var table = fresh.Tables.SingleOrDefault(t => t.Key == request.Table) ?? throw new InvalidOperationException("來源資料表已變更或讀取失敗，請重新載入。");
        if (reconcile)
        {
            var destination = targetConnection() ?? throw new InvalidOperationException("請先在步驟 1 測試檢查 DB 連線。");
            var target = await new CatalogService().ReadAsync(destination, new ComparisonOptions { Scope = ComparisonScope.Tables }, token);
            var targetTable = target.Tables.SingleOrDefault(t => t.Key == table.Key) ?? throw new InvalidOperationException("檢查 DB 沒有可讀取的同名表，請先檢查結構。");
            result = await new DataService().ReconcileAsync(source, destination, table, targetTable, target, request, updates.Checked, token, new Progress<string>(text => { if (!IsDisposed) status.Text = text; }));
        }
        else result = await new DataService().ExportAsync(source, table, request, null, token);
        results.Rows.Clear();
        foreach (var item in result.Differences) { var index = results.Rows.Add(item.Key, item.State); results.Rows[index].Tag = item; }
        sql.Text = result.Sql.Length > 2_000_000 ? result.Sql[..2_000_000] + "\r\n-- 畫面只顯示前 2,000,000 字元，匯出包含完整 SQL。" : result.Sql;
        status.Text = $"已讀取 {result.ReadCount} 筆；缺少 {result.Missing}、不同 {result.Changed}、相同 {result.Same}。\r\n" + string.Join(" ", result.Warnings);
        status.ForeColor = result.Warnings.Any(w => w.Contains("阻擋", StringComparison.Ordinal)) ? Ui.Warning : Ui.Muted;
        save.Enabled = copy.Enabled = result.Sql.Length > 0;
    });

    private void FilterTables()
    {
        if (catalog is null) return;
        var selected = (tables.SelectedItem as TableChoice)?.Table.Key;
        var text = search.Text.Trim();
        var visible = catalog.Tables.Where(t => text.Length == 0 || t.Key.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || t.Key.Schema.Contains(text, StringComparison.OrdinalIgnoreCase)).ToArray();
        binding = true;
        try { tables.Items.Clear(); tables.Items.AddRange(visible.Select(t => (object)new TableChoice(t)).ToArray()); if (visible.Length > 0) tables.SelectedIndex = Math.Max(0, Array.FindIndex(visible, t => t.Key == selected)); }
        finally { binding = false; }
        SelectTable();
    }
    private void SelectTable()
    {
        if (binding) return;
        binding = true;
        try
        {
            columns.Items.Clear(); keys.Items.Clear(); filterColumns.Items.Clear(); filterColumns.Items.Add(new FilterChoice(null)); filterColumns.SelectedIndex = 0;
            if (tables.SelectedItem is TableChoice choice)
            {
                foreach (var column in choice.Table.Columns.Where(c => c.IsWritable && DataService.Supported(c))) columns.Items.Add(new ColumnChoice(column), columns.Items.Count < 256);
                foreach (var index in DataService.UsableKeys(choice.Table)) keys.Items.Add(new KeyChoice(index));
                if (keys.Items.Count > 0) keys.SelectedIndex = 0;
                foreach (var column in choice.Table.Columns.Where(DataService.Supported)) filterColumns.Items.Add(new FilterChoice(column));
                if (keys.Items.Count == 0) status.Text = "此表沒有本工具可用的非 NULL 主鍵／唯一索引，不能可靠排序或配對；不會推測業務鍵。";
            }
        }
        finally { binding = false; }
        InvalidateResult();
    }
    public void InvalidateResult()
    {
        if (binding) return;
        result = null; sql.Clear(); results.Rows.Clear(); diff.Clear(); save.Enabled = copy.Enabled = false;
    }
    private static ComboBox Combo() => new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 3, 6, 5) };
}
