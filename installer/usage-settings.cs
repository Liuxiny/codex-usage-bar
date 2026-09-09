using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace CodexUsageBar
{
    internal sealed class UsageSourceSettings
    {
        internal string Mode = "auto", DirectoryOverride = "";
        internal CcProvider Custom = new CcProvider { Id = "standalone", Name = "Custom", Enabled = true, Code = "", BaseUrl = "", ApiKey = "", TemplateType = "custom" };
        internal static UsageSourceSettings Load(string path)
        {
            var settings = new UsageSourceSettings();
            if (!File.Exists(path)) return settings;
            object root = CcJson.Parse(File.ReadAllText(path));
            settings.Mode = CcJson.Text(root, "mode");
            if (settings.Mode != "auto" && settings.Mode != "official" && settings.Mode != "cc-switch" && settings.Mode != "custom") throw new InvalidOperationException("Invalid usage source mode");
            settings.DirectoryOverride = CcJson.Text(root, "ccSwitchDirectory");
            string encrypted = CcJson.Text(root, "protectedConfiguration");
            if (encrypted.Length > 0)
            {
                byte[] bytes = ProtectedData.Unprotect(Convert.FromBase64String(encrypted), null, DataProtectionScope.CurrentUser);
                object data;
                try { data = CcJson.Parse(Encoding.UTF8.GetString(bytes)); }
                finally { Array.Clear(bytes, 0, bytes.Length); }
                settings.Custom.Name = CcJson.Text(data, "name");
                settings.Custom.Code = CcJson.Text(data, "code");
                settings.Custom.BaseUrl = CcJson.Text(data, "baseUrl");
                settings.Custom.ApiKey = CcJson.Text(data, "apiKey");
                settings.Custom.AccessToken = CcJson.Get(data, "accessToken") as string;
                settings.Custom.UserId = CcJson.Get(data, "userId") as string;
                settings.Custom.IntervalMinutes = (int)Math.Max(0, Math.Min(1440, CcJson.Number(data, "interval") ?? 2));
                settings.Custom.TimeoutSeconds = (int)Math.Max(2, Math.Min(30, CcJson.Number(data, "timeout") ?? 10));
            }
            using (var sha = SHA256.Create()) settings.Custom.Fingerprint = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(CcJson.Encode(root))));
            return settings;
        }
        internal void Save(string path)
        {
            var data = new Dictionary<string, object> { {"name", Custom.Name}, {"code", Custom.Code}, {"baseUrl", Custom.BaseUrl}, {"apiKey", Custom.ApiKey}, {"accessToken", Custom.AccessToken}, {"userId", Custom.UserId}, {"interval", Custom.IntervalMinutes}, {"timeout", Custom.TimeoutSeconds} };
            byte[] bytes = Encoding.UTF8.GetBytes(CcJson.Encode(data));
            string encrypted;
            try { encrypted = Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)); }
            finally { Array.Clear(bytes, 0, bytes.Length); }
            var root = new Dictionary<string, object> { {"schemaVersion", 1}, {"mode", Mode}, {"ccSwitchDirectory", DirectoryOverride}, {"protectedConfiguration", encrypted} };
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, CcJson.Encode(root), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        internal CcProvider Resolve()
        {
            if (Mode == "official") return null;
            if (Mode == "custom") return Custom;
            CcProvider provider = String.IsNullOrWhiteSpace(DirectoryOverride) ? CcSwitchReader.ReadCurrent() : CcSwitchReader.ReadCurrent(CcSwitchReader.RequireDataDirectory(DirectoryOverride));
            if (provider == null && Mode == "cc-switch") throw new InvalidOperationException("CC Switch has no current Codex provider; check its data directory");
            return provider;
        }
        internal static string SafeError(Exception error, bool chinese = false)
        {
            if (error.Message == "CC Switch data directory must contain cc-switch.db (not the installation folder)")
                return chinese ? "请选择包含 cc-switch.db 的数据文件夹，不是 CC Switch 安装目录。可点击“自动查找”。" : error.Message;
            if (error.Message == "CC Switch network error: SecureChannelFailure")
                return chinese ? "HTTPS 安全连接失败（TLS 1.2）。请检查系统时间、站点证书或代理设置。" : "HTTPS connection failed with TLS 1.2. Check the system clock, site certificate and proxy settings.";
            if (error is InvalidOperationException && error.Message.StartsWith("CC Switch")) return error.Message;
            if (error is InvalidOperationException && error.Message.StartsWith("Enable a usage")) return error.Message;
            if (error is CryptographicException) return "Cannot decrypt usage settings for this Windows user";
            return "Usage query/configuration unavailable (" + error.GetType().Name + ")";
        }
    }

    internal sealed class UsageSettingsForm : Form
    {
        private readonly string _path;
        private readonly bool _zh;
        private readonly ComboBox _mode = new ThemeComboBox();
        private readonly TextBox _directory = new TextBox(), _name = new TextBox(), _url = new TextBox(), _key = new TextBox { UseSystemPasswordChar = true }, _token = new TextBox { UseSystemPasswordChar = true }, _user = new TextBox();
        private readonly NumericUpDown _interval = new NumericUpDown { Minimum = 0, Maximum = 1440, Value = 2 }, _timeout = new NumericUpDown { Minimum = 2, Maximum = 30, Value = 10 };
        private readonly TextBox _code = new TextBox { Multiline = true, AcceptsReturn = true, AcceptsTab = true, ScrollBars = ScrollBars.Both, WordWrap = false };
        private readonly TextBox _result = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
        private readonly Button _test = new ThemeButton(), _save = new ThemeButton { Primary = true };
        private readonly Button _detect = new ThemeButton(), _browse = new ThemeButton();
        private readonly Label _directoryHint = new Label();
        private ThemePalette _theme;
        private Font _uiFont, _headingFont, _sectionFont;
        private readonly List<Font> _ownedFonts = new List<Font>();
        private TableLayoutPanel _parametersLayout;
        private Control _customPanel, _directoryGroup;
        private Label _connectionInfo, _scriptPlaceholder, _scriptTitle;
        private Button _preset;
        internal UsageSettingsForm(string path, bool chinese) : this(path, chinese, ThemePalette.CreateDefault(true)) { }
        internal UsageSettingsForm(string path, bool chinese, ThemePalette theme)
        {
            _path = path; _zh = chinese;
            Text = L("用量数据源设置", "Usage source settings");
            Font = new Font(chinese ? "Microsoft YaHei UI" : "Segoe UI", 9f);
            _theme = theme;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1000, 790); MinimumSize = new Size(900, 720);
            ShowInTaskbar = true; AutoScaleMode = AutoScaleMode.Dpi;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(22) };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            Controls.Add(layout);
            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            header.Controls.Add(new Label { Text = L("用量数据源", "Usage sources"), Tag = "title", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            header.Controls.Add(new Label { Text = L("连接你的服务商，在 Codex 中查看余额与套餐用量。", "Connect your provider to see balances and plan usage in Codex."), Tag = "muted", Dock = DockStyle.Fill }, 0, 1);
            layout.Controls.Add(header, 0, 0);
            var source = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 2, 0, 12) };
            source.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104)); source.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            source.Controls.Add(new Label { Text = L("数据源", "Source"), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            _mode.Items.AddRange(new object[] { L("自动跟随 CC Switch", "Auto: follow CC Switch"), L("官方用量", "Official usage"), L("CC Switch", "CC Switch"), L("独立配置", "Standalone") });
            _mode.Dock = DockStyle.Fill; _mode.Margin = new Padding(0, 3, 0, 0);
            source.Controls.Add(_mode, 1, 0); layout.Controls.Add(source, 0, 1);
            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
            layout.Controls.Add(body, 0, 2);
            var parameters = new ThemeCard { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 12, 0) };
            body.Controls.Add(parameters, 0, 0);
            _parametersLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
            _parametersLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            _parametersLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 156));
            _parametersLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _parametersLayout.Controls.Add(new Label { Text = L("连接设置", "Connection"), Tag = "section", Dock = DockStyle.Fill }, 0, 0);
            parameters.Controls.Add(_parametersLayout);
            var directoryPanel = new TableLayoutPanel { ColumnCount = 1, RowCount = 4, Dock = DockStyle.Fill, Margin = Padding.Empty };
            directoryPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
            directoryPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            directoryPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
            directoryPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            directoryPanel.Controls.Add(new Label { Text = L("CC Switch 数据目录", "CC Switch data folder"), Dock = DockStyle.Fill }, 0, 0);
            directoryPanel.Controls.Add(new InputSurface(_directory, false), 0, 1);
            var directoryActions = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            _detect.Text = L("自动查找", "Find automatically"); _browse.Text = L("浏览…", "Browse…");
            _detect.Size = new Size(110, 32); _browse.Size = new Size(80, 32);
            _detect.Margin = new Padding(0, 2, 8, 0); _browse.Margin = new Padding(0, 2, 0, 0);
            directoryActions.Controls.Add(_detect); directoryActions.Controls.Add(_browse);
            directoryPanel.Controls.Add(directoryActions, 0, 2);
            _directoryHint.Text = L("选择包含 cc-switch.db 的文件夹，\n不是程序安装目录。留空可自动查找。", "Choose the folder containing cc-switch.db,\nnot the installation folder. Blank = automatic.");
            _directoryHint.Tag = "muted"; _directoryHint.Dock = DockStyle.Fill;
            directoryPanel.Controls.Add(_directoryHint, 0, 3);
            _directoryGroup = directoryPanel; _parametersLayout.Controls.Add(directoryPanel, 0, 1);
            var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 8, Margin = Padding.Empty };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            AddRow(fields, 0, L("显示名称", "Name"), new InputSurface(_name, false));
            AddRow(fields, 1, "Base URL", new InputSurface(_url, false));
            AddRow(fields, 2, "API Key", new InputSurface(_key, false));
            AddRow(fields, 3, "Access Token", new InputSurface(_token, false));
            AddRow(fields, 4, "User ID", new InputSurface(_user, false));
            AddRow(fields, 5, L("刷新 / 分钟", "Refresh / min"), new InputSurface(_interval, false));
            AddRow(fields, 6, L("超时 / 秒", "Timeout / sec"), new InputSurface(_timeout, false));
            var fieldHint = new Label { Text = L("凭据按接口需要填写。\n刷新设为 0 时，仅在启动或手动操作时查询。", "Fill credentials required by your provider.\nRefresh 0 queries on startup or manually."), Tag = "muted", Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
            fields.Controls.Add(fieldHint, 0, 7); fields.SetColumnSpan(fieldHint, 2);
            _customPanel = fields; _parametersLayout.Controls.Add(fields, 0, 2);
            _connectionInfo = new Label { Dock = DockStyle.Fill, Tag = "muted", Padding = new Padding(0, 22, 0, 0) };
            _parametersLayout.Controls.Add(_connectionInfo, 0, 2);
            var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 65)); right.RowStyles.Add(new RowStyle(SizeType.Percent, 35)); body.Controls.Add(right, 1, 0);
            var scriptCard = new ThemeCard { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 12) };
            var scriptLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            scriptLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); scriptLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var scriptHeader = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            scriptHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); scriptHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 124));
            _scriptTitle = new Label { Text = L("查询脚本", "Query script"), Tag = "section", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
            scriptHeader.Controls.Add(_scriptTitle, 0, 0);
            var preset = new ThemeButton { Text = L("填入套餐模板", "Use template"), Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 7) };
            _preset = preset; scriptHeader.Controls.Add(preset, 1, 0);
            scriptLayout.Controls.Add(scriptHeader, 0, 0);
            _code.Font = new Font("Consolas", 12, FontStyle.Regular, GraphicsUnit.Pixel);
            scriptLayout.Controls.Add(new InputSurface(_code, true), 0, 1);
            _scriptPlaceholder = new Label { Dock = DockStyle.Fill, Tag = "muted", Padding = new Padding(0, 18, 0, 0) };
            scriptLayout.Controls.Add(_scriptPlaceholder, 0, 1);
            scriptCard.Controls.Add(scriptLayout); right.Controls.Add(scriptCard, 0, 0);
            var resultCard = new ThemeCard { Dock = DockStyle.Fill, Margin = Padding.Empty };
            var resultLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            resultLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); resultLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            resultLayout.Controls.Add(new Label { Text = L("查询结果", "Query result"), Tag = "section", Dock = DockStyle.Fill }, 0, 0);
            resultLayout.Controls.Add(new InputSurface(_result, true), 0, 1);
            resultCard.Controls.Add(resultLayout); right.Controls.Add(resultCard, 0, 1);
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 16, 0, 0) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 292));
            footer.Controls.Add(new Label { Text = L("凭据仅在本机加密保存", "Credentials are encrypted on this device"), Tag = "muted", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Margin = Padding.Empty };
            _save.Text = L("保存", "Save"); _save.Size = new Size(78, 36);
            _test.Text = L("测试查询", "Test query"); _test.Size = new Size(96, 36);
            var cancel = new ThemeButton { Text = L("取消", "Cancel"), Size = new Size(78, 36), DialogResult = DialogResult.Cancel };
            actions.Controls.Add(_save); actions.Controls.Add(cancel); actions.Controls.Add(_test);
            footer.Controls.Add(actions, 1, 0); layout.Controls.Add(footer, 0, 3);
            CancelButton = cancel;
            UsageSourceSettings settings;
            try { settings = UsageSourceSettings.Load(path); }
            catch (Exception ex) { settings = new UsageSourceSettings(); _result.Text = UsageSourceSettings.SafeError(ex, _zh); }
            _mode.SelectedIndex = settings.Mode == "official" ? 1 : settings.Mode == "cc-switch" ? 2 : settings.Mode == "custom" ? 3 : 0;
            _directory.Text = settings.DirectoryOverride; _name.Text = settings.Custom.Name; _url.Text = settings.Custom.BaseUrl;
            if (String.IsNullOrWhiteSpace(_directory.Text)) FindDirectory(false);
            _detect.Click += delegate { FindDirectory(true); };
            _browse.Click += delegate
            {
                using (var dialog = new FolderBrowserDialog { Description = L("选择包含 cc-switch.db 的 CC Switch 数据文件夹", "Select the CC Switch data folder containing cc-switch.db"), ShowNewFolderButton = false })
                {
                    if (Directory.Exists(_directory.Text)) dialog.SelectedPath = _directory.Text;
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    {
                        _directory.Text = dialog.SelectedPath;
                        try { CcSwitchReader.RequireDataDirectory(_directory.Text); _result.Text = L("数据目录有效。点击“测试查询”验证当前服务商。", "Data folder found. Test the current provider query."); }
                        catch (Exception ex) { _result.Text = UsageSourceSettings.SafeError(ex, _zh); }
                    }
                }
            };
            _key.Text = settings.Custom.ApiKey; _token.Text = settings.Custom.AccessToken; _user.Text = settings.Custom.UserId;
            _interval.Value = settings.Custom.IntervalMinutes; _timeout.Value = settings.Custom.TimeoutSeconds; _code.Text = EditorLines(settings.Custom.Code);
            preset.Click += delegate
            {
                if (_code.TextLength > 0 && MessageBox.Show(this, L("用套餐模板替换当前编辑框中的脚本？", "Replace the script in this editor with the package template?"), Text, MessageBoxButtons.OKCancel) != DialogResult.OK) return;
                using (var stream = typeof(UsageSettingsForm).Assembly.GetManifestResourceStream("package-quota.js"))
                using (var reader = new StreamReader(stream)) _code.Text = EditorLines(reader.ReadToEnd());
            };
            _mode.SelectedIndexChanged += delegate { SetEnabled(); }; SetEnabled();
            _save.Click += delegate
            {
                try
                {
                    UsageSourceSettings value = ReadForm();
                    if (value.Mode == "custom") Validate(value.Custom);
                    else if (value.Mode != "official" && !String.IsNullOrWhiteSpace(value.DirectoryOverride)) CcSwitchReader.RequireDataDirectory(value.DirectoryOverride);
                    value.Save(_path); DialogResult = DialogResult.OK; Close();
                }
                catch (Exception ex) { _result.Text = UsageSourceSettings.SafeError(ex, _zh); }
            };
            _test.Click += delegate { Test(); };
            if (_result.TextLength == 0) _result.Text = L("点击“测试查询”，验证连接并查看用量。", "Test the connection to see your usage here.");
            ApplyTheme(theme);
        }
        private string L(string zh, string en) { return _zh ? zh : en; }
        private void FindDirectory(bool report)
        {
            try
            {
                string path = CcSwitchReader.ConfigDirectory();
                if (File.Exists(Path.Combine(path, "cc-switch.db")))
                {
                    _directory.Text = path;
                    if (report) _result.Text = L("已找到 CC Switch 数据目录：", "Found CC Switch data folder: ") + path;
                }
                else if (report) _result.Text = L("未找到 cc-switch.db。请通过“浏览”选择数据文件夹，或使用“独立配置”。默认位置：", "No cc-switch.db found. Browse for the data folder or use Standalone. Default: ") + path;
            }
            catch (Exception ex) { if (report) _result.Text = UsageSourceSettings.SafeError(ex, _zh); }
        }
        internal void ApplyTheme(ThemePalette theme)
        {
            _theme = theme;
            Font next = NativeTheme.UiFont(theme, 0, FontStyle.Regular);
            if (_uiFont != null && _uiFont.Equals(next)) next.Dispose();
            else
            {
                _uiFont = next;
                _headingFont = NativeTheme.UiFont(theme, 9, FontStyle.Bold);
                _sectionFont = NativeTheme.UiFont(theme, 1, FontStyle.Bold);
                _ownedFonts.Add(_uiFont); _ownedFonts.Add(_headingFont); _ownedFonts.Add(_sectionFont);
            }
            Font = _uiFont; BackColor = theme.Surface;
            NativeTheme.Apply(this, theme); ApplyHeadingFonts(this);
            if (IsHandleCreated) NativeTheme.TitleBar(this, theme.Dark);
        }
        private void ApplyHeadingFonts(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                if ((child.Tag as string) == "title") child.Font = _headingFont;
                else if ((child.Tag as string) == "section") child.Font = _sectionFont;
                ApplyHeadingFonts(child);
            }
        }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); NativeTheme.TitleBar(this, _theme != null && _theme.Dark); }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) { foreach (Font font in _ownedFonts) font.Dispose(); _ownedFonts.Clear(); }
        }
        internal static string EditorLines(string text) { return (text ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n"); }
        private void AddRow(TableLayoutPanel panel, int row, string title, Control control)
        {
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            panel.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            control.Dock = DockStyle.Fill; panel.Controls.Add(control, 1, row);
        }
        private void SetEnabled()
        {
            bool custom = _mode.SelectedIndex == 3;
            foreach (Control control in new Control[] { _name, _url, _key, _token, _user, _interval, _timeout, _code }) control.Enabled = custom;
            _directory.Enabled = _mode.SelectedIndex == 0 || _mode.SelectedIndex == 2;
            _detect.Enabled = _browse.Enabled = _directory.Enabled;
            _directoryGroup.Visible = _directory.Enabled;
            _parametersLayout.RowStyles[1].Height = _directory.Enabled ? 156 : 0;
            _customPanel.Visible = custom;
            _connectionInfo.Visible = !custom;
            _connectionInfo.Text = _mode.SelectedIndex == 1
                ? L("使用 Codex 官方账户用量。\n\n无需填写第三方凭据。", "Uses your official Codex account usage.\n\nNo third-party credentials required.")
                : L("查询参数与脚本由 CC Switch 管理。\n\n切换服务商或修改脚本后，CodexBar 会自动更新。", "CC Switch manages the query credentials and script.\n\nProvider and script changes are picked up automatically.");
            _code.Parent.Visible = custom;
            _scriptPlaceholder.Visible = !custom;
            _scriptTitle.Text = custom ? L("查询脚本", "Query script") : L("连接说明", "Connection guide");
            _preset.Visible = custom;
            _scriptPlaceholder.Text = _mode.SelectedIndex == 1
                ? L("官方用量由 Codex App Server 提供。\n\n保存后，悬浮窗将显示官方账户的用量。", "Official usage comes from Codex App Server.\n\nSave to display your official account usage.")
                : L("1. 在 CC Switch 中选中 Codex 服务商。\n\n2. 启用并保存该服务商的用量查询脚本。\n\n3. 点击下方“测试查询”，成功后保存。\n\n未安装 CC Switch？可切换为“独立配置”。", "1. Select a Codex provider in CC Switch.\n\n2. Enable and save its usage query script.\n\n3. Test the query below, then save.\n\nNo CC Switch? Choose Standalone instead.");
        }
        private UsageSourceSettings ReadForm()
        {
            return new UsageSourceSettings { Mode = new[] { "auto", "official", "cc-switch", "custom" }[_mode.SelectedIndex], DirectoryOverride = _directory.Text.Trim(), Custom = new CcProvider { Id = "standalone", Name = _name.Text.Trim(), Enabled = true, Code = _code.Text, BaseUrl = _url.Text.Trim().TrimEnd('/'), ApiKey = _key.Text.Trim(), AccessToken = _token.Text, UserId = _user.Text, TemplateType = "custom", IntervalMinutes = (int)_interval.Value, TimeoutSeconds = (int)_timeout.Value } };
        }
        private static void Validate(CcProvider provider)
        {
            if (String.IsNullOrWhiteSpace(provider.Code)) throw new InvalidOperationException("Enable a usage query script in CC Switch or enter one here");
            object request = CcJson.Parse(CcJavaScript.Evaluate("JSON.stringify((" + CcSwitchClient.BuildScript(provider) + ").request)", 5000));
            CcSwitchClient.ValidateUrl(CcJson.Text(request, "url"), provider);
        }
        private void Test()
        {
            UsageSourceSettings settings = ReadForm();
            _test.Enabled = _save.Enabled = false; _result.Text = L("正在查询…", "Querying…");
            var worker = new BackgroundWorker();
            worker.DoWork += delegate(object sender, DoWorkEventArgs e)
            {
                try
                {
                    CcProvider provider = settings.Resolve();
                    if (provider == null || provider.Official) { e.Result = L("当前选择官方用量，由主窗口 App Server 查询。", "Official usage is queried by the main App Server connection."); return; }
                    UsageSnapshot snapshot = new CcSwitchClient().Query(provider);
                    var lines = new StringBuilder(provider.Name);
                    foreach (CcUsageRow row in snapshot.UsageRows) lines.AppendLine().Append(row.Name).Append(" · ").Append(row.ValueText(_zh)).Append(" · ").Append(row.Extra);
                    e.Result = lines.ToString();
                }
                catch (Exception ex) { e.Result = UsageSourceSettings.SafeError(ex, _zh); }
            };
            worker.RunWorkerCompleted += delegate(object sender, RunWorkerCompletedEventArgs e)
            {
                if (!IsDisposed) { _result.Text = e.Error == null ? Convert.ToString(e.Result) : "Query failed"; _test.Enabled = _save.Enabled = true; }
                worker.Dispose();
            };
            worker.RunWorkerAsync();
        }
    }
}
