using System.Runtime.InteropServices;
using Microsoft.Data.SqlClient;

namespace DbCheck.Workbench;

internal static class Ui
{
    public static readonly Color Background = Color.FromArgb(244, 247, 250);
    public static readonly Color Ink = Color.FromArgb(25, 42, 60);
    public static readonly Color Muted = Color.FromArgb(99, 115, 135);
    public static readonly Color Border = Color.FromArgb(217, 225, 235);
    public static readonly Color Accent = Color.FromArgb(18, 122, 113);
    public static readonly Color Sidebar = Color.FromArgb(19, 35, 51);
    public static readonly Color Warning = Color.FromArgb(153, 80, 12);

    public static Button Button(string text, bool primary = false)
    {
        var button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(88, 34), Padding = new Padding(10, 3, 10, 3), Margin = new Padding(0, 0, 8, 6), FlatStyle = FlatStyle.Flat, BackColor = primary ? Accent : Color.White, ForeColor = primary ? Color.White : Ink, Cursor = Cursors.Hand, UseVisualStyleBackColor = false };
        button.FlatAppearance.BorderColor = primary ? Accent : Border;
        return button;
    }
    public static Label Label(string text, bool muted = false) => new() { Text = text, AutoSize = true, ForeColor = muted ? Muted : Ink, Margin = new Padding(0, 5, 0, 6) };
    public static TextBox TextBox(bool readOnly = false) => new() { Dock = DockStyle.Fill, ReadOnly = readOnly, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White, ForeColor = Ink, Margin = new Padding(0, 3, 0, 6) };
    public static TextBox CodeBox() => new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = false, ScrollBars = ScrollBars.Both, Font = new Font("Consolas", 10), BackColor = Color.White, ForeColor = Ink, BorderStyle = BorderStyle.FixedSingle };
    public static DataGridView Grid() => new BufferedGrid { Dock = DockStyle.Fill, BackgroundColor = Color.White, BorderStyle = BorderStyle.None, AutoGenerateColumns = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, RowHeadersVisible = false, MultiSelect = false, ReadOnly = true, SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, EnableHeadersVisualStyles = false, ColumnHeadersHeight = 36, ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Background, ForeColor = Ink, Padding = new Padding(5) }, DefaultCellStyle = new DataGridViewCellStyle { ForeColor = Ink, BackColor = Color.White, SelectionBackColor = Color.FromArgb(225, 241, 238), SelectionForeColor = Ink, Padding = new Padding(5) }, GridColor = Border, RowTemplate = { Height = 32 } };
    public static FlowLayoutPanel Actions(params Control[] controls)
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true, Margin = new Padding(0), Padding = new Padding(0, 5, 0, 0) };
        panel.Controls.AddRange(controls); return panel;
    }
    public static TableLayoutPanel Rows(params RowStyle[] styles)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = styles.Length, Margin = new Padding(0) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); panel.RowStyles.AddRange(styles); return panel;
    }
    public static TabPage Tab(string title, Control content)
    {
        var page = new TabPage(title) { BackColor = Color.White, Padding = new Padding(8) }; page.Controls.Add(content); return page;
    }
    public static void Error(IWin32Window owner, Exception error) => MessageBox.Show(owner, SqlText.Failure(error), "作業未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    public static void Copy(IWin32Window owner, string text)
    {
        if (text.Length == 0) return;
        try { Clipboard.SetText(text); }
        catch (ExternalException) { MessageBox.Show(owner, "剪貼簿暫時被其他程式占用，請重試或另存檔案。", "複製", MessageBoxButtons.OK, MessageBoxIcon.Information); }
    }
    public static string? Prompt(IWin32Window owner, string title, string message, string initial = "", bool secret = false)
    {
        using var dialog = new Form { Text = title, Width = 620, Height = 220, MinimumSize = new Size(520, 220), StartPosition = FormStartPosition.CenterParent, Font = new Font("Microsoft JhengHei UI", 10), MinimizeBox = false, MaximizeBox = false, BackColor = Background };
        var layout = Rows(new(SizeType.AutoSize), new(SizeType.Absolute, 42), new(SizeType.Absolute, 48)); layout.Padding = new Padding(18);
        var box = TextBox(); box.Text = initial; box.UseSystemPasswordChar = secret;
        var ok = Button("確定", true); ok.DialogResult = DialogResult.OK;
        var cancel = Button("取消"); cancel.DialogResult = DialogResult.Cancel;
        layout.Controls.Add(Label(message), 0, 0); layout.Controls.Add(box, 0, 1); layout.Controls.Add(Actions(ok, cancel), 0, 2);
        dialog.Controls.Add(layout); dialog.AcceptButton = ok; dialog.CancelButton = cancel;
        return dialog.ShowDialog(owner) == DialogResult.OK ? box.Text : null;
    }
    public static string State(DifferenceState state) => state switch { DifferenceState.Missing => "缺少", DifferenceState.Changed => "不同", DifferenceState.Retained => "保留", _ => "無法比對" };
    public static string Category(ObjectCategory category) => category switch { ObjectCategory.Table => "Table", ObjectCategory.View => "View", ObjectCategory.Function => "Function", ObjectCategory.Procedure => "SP", ObjectCategory.Trigger => "DML Trigger", _ => "範圍" };
    public static string Risk(RiskLevel risk) => risk switch { RiskLevel.Blocked => "阻擋", RiskLevel.Review => "待確認", _ => "資訊" };
}

internal sealed class BufferedGrid : DataGridView { public BufferedGrid() { DoubleBuffered = true; } }

internal sealed class EndpointEditor : UserControl
{
    private readonly TextBox server = Ui.TextBox(), database = Ui.TextBox(), user = Ui.TextBox(), password = Ui.TextBox();
    private readonly CheckBox integrated = new() { Text = "Windows 驗證", AutoSize = true };
    private readonly ComboBox profiles = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly Label status = Ui.Label("尚未測試連線", true);
    private SqlConnectionStringBuilder settings = new() { Encrypt = true, TrustServerCertificate = false, ConnectTimeout = 15 };
    private int revision;
    private bool loading;
    public string? VerifiedConnection { get; private set; }
    public string? VerifiedLabel { get; private set; }
    public event EventHandler? Changed;
    public event EventHandler? TestRequested;
    public event EventHandler? SaveRequested;
    public event EventHandler? DeleteRequested;
    public string? SelectedProfile => (profiles.SelectedItem as ConnectionProfile)?.Name;

    public EndpointEditor(string title, string hint)
    {
        Dock = DockStyle.Fill; BackColor = Color.White; Padding = new Padding(18); Margin = new Padding(0, 0, 14, 0);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 9, Margin = new Padding(0) };
        layout.ColumnStyles.Add(new(SizeType.Absolute, 72)); layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        foreach (var height in new[] { 34, 32, 38, 38, 38, 34, 38, 42, 48 }) layout.RowStyles.Add(new(SizeType.Absolute, height));
        var heading = Ui.Label(title); heading.Font = new Font(Font, FontStyle.Bold);
        layout.Controls.Add(heading, 0, 0); layout.SetColumnSpan(heading, 2);
        var description = Ui.Label(hint, true); layout.Controls.Add(description, 0, 1); layout.SetColumnSpan(description, 2);
        layout.Controls.Add(Ui.Label("環境"), 0, 2); layout.Controls.Add(profiles, 1, 2);
        layout.Controls.Add(Ui.Label("伺服器"), 0, 3); layout.Controls.Add(server, 1, 3);
        layout.Controls.Add(Ui.Label("資料庫"), 0, 4); layout.Controls.Add(database, 1, 4);
        layout.Controls.Add(integrated, 1, 5);
        var credentials = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
        credentials.ColumnStyles.Add(new(SizeType.Percent, 48)); credentials.ColumnStyles.Add(new(SizeType.Percent, 52));
        user.PlaceholderText = "SQL 帳號"; password.PlaceholderText = "密碼（不保存）"; password.UseSystemPasswordChar = true;
        user.Margin = new Padding(0, 3, 8, 3); credentials.Controls.Add(user, 0, 0); credentials.Controls.Add(password, 1, 0);
        layout.Controls.Add(Ui.Label("帳密"), 0, 6); layout.Controls.Add(credentials, 1, 6);
        var test = Ui.Button("測試連線", true); var save = Ui.Button("保存環境"); var paste = Ui.Button("貼入連線"); var delete = Ui.Button("移除環境");
        test.Click += (_, _) => TestRequested?.Invoke(this, EventArgs.Empty); save.Click += (_, _) => SaveRequested?.Invoke(this, EventArgs.Empty); delete.Click += (_, _) => DeleteRequested?.Invoke(this, EventArgs.Empty);
        paste.Click += (_, _) => { var text = Ui.Prompt(this, "貼入連線字串", "內容遮罩，只會保留在本次記憶體；保存環境時不保存帳密。", secret: true); if (text is null) return; try { LoadConnection(text); } catch (ArgumentException ex) { Ui.Error(this, ex); } };
        // A compact overflow-friendly action row keeps credentials readable on smaller displays.
        layout.Controls.Add(Ui.Actions(test, save, paste, delete), 0, 7); layout.SetColumnSpan(layout.GetControlFromPosition(0, 7)!, 2);
        status.Dock = DockStyle.Fill; status.AutoSize = false; status.AutoEllipsis = true; layout.Controls.Add(status, 0, 8); layout.SetColumnSpan(status, 2);
        Controls.Add(layout);
        foreach (var box in new[] { server, database, user, password }) box.TextChanged += (_, _) => InvalidateVerification();
        integrated.CheckedChanged += (_, _) => { user.Enabled = password.Enabled = !integrated.Checked; InvalidateVerification(); };
        profiles.SelectedIndexChanged += (_, _) => { if (!loading && profiles.SelectedItem is ConnectionProfile profile) LoadConnection(profile.ConnectionString); };
    }

    public void LoadProfiles(IReadOnlyList<ConnectionProfile> entries)
    {
        var selected = SelectedProfile;
        loading = true;
        try
        {
            profiles.Items.Clear(); profiles.Items.AddRange(entries.Cast<object>().ToArray());
            if (selected is not null) profiles.SelectedIndex = entries.ToList().FindIndex(p => p.Name == selected);
        }
        finally { loading = false; }
    }
    public void LoadConnection(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        loading = true;
        try
        {
            server.Text = builder.DataSource; database.Text = builder.InitialCatalog; user.Text = builder.UserID; password.Text = builder.Password; integrated.Checked = builder.IntegratedSecurity;
            builder.Remove("Password"); builder.Remove("User ID"); settings = builder;
            user.Enabled = password.Enabled = !integrated.Checked;
        }
        finally { loading = false; }
        InvalidateVerification();
    }
    public string BuildConnection()
    {
        if (string.IsNullOrWhiteSpace(server.Text) || string.IsNullOrWhiteSpace(database.Text)) throw new ArgumentException("請填寫伺服器與資料庫名稱。");
        var builder = new SqlConnectionStringBuilder(settings.ConnectionString) { DataSource = server.Text.Trim(), InitialCatalog = database.Text.Trim(), IntegratedSecurity = integrated.Checked, ApplicationName = "WEYU-DBCheck" };
        if (!integrated.Checked) { builder.UserID = user.Text; builder.Password = password.Text; }
        else { builder.Remove("User ID"); builder.Remove("Password"); }
        return builder.ConnectionString;
    }
    public async Task TestAsync(CancellationToken token)
    {
        var version = revision; var connection = BuildConnection();
        status.Text = "正在測試連線與 VIEW DEFINITION 權限…"; status.ForeColor = Ui.Muted;
        try
        {
            var label = await CatalogService.TestAsync(connection, token);
            if (IsDisposed || token.IsCancellationRequested || version != revision) return;
            VerifiedConnection = connection; VerifiedLabel = label; status.Text = "✓ " + label; status.ForeColor = Ui.Accent;
        }
        catch
        {
            if (!IsDisposed && version == revision) { VerifiedConnection = null; VerifiedLabel = null; status.Text = "測試未完成，請確認連線與權限。"; status.ForeColor = Ui.Warning; }
            throw;
        }
    }
    private void InvalidateVerification()
    {
        if (loading) return;
        revision++; VerifiedConnection = null; VerifiedLabel = null; status.Text = "設定已變更，請重新測試。"; status.ForeColor = Ui.Muted;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
