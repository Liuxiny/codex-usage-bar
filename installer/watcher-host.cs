using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: AssemblyTitle("Codex Usage Bar")]
[assembly: AssemblyProduct("Codex Usage Bar")]
[assembly: AssemblyCompany("Codex Usage Bar")]
[assembly: AssemblyVersion("0.6.1.0")]
[assembly: AssemblyFileVersion("0.6.1.0")]

namespace CodexUsageBar
{
    internal static class Log
    {
        private static readonly object Gate = new object();
        internal static bool Disabled;
        internal static string PathValue;

        internal static void Write(string message)
        {
            if (Disabled || String.IsNullOrEmpty(PathValue)) return;
            try
            {
                lock (Gate)
                {
                    string directory = Path.GetDirectoryName(PathValue);
                    Directory.CreateDirectory(directory);
                    if (File.Exists(PathValue) && new FileInfo(PathValue).Length > 1024 * 1024)
                        File.WriteAllText(PathValue, String.Empty, new UTF8Encoding(false));
                    File.AppendAllText(PathValue, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + message + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }
    }

    internal static class CompanionHost
    {
        internal const string Version = "0.6.1";
        internal const string MutexName = "Local\\CodexUsageBarCompanion";
        internal const string ExitEventName = "Local\\CodexUsageBarExit";

        [STAThread]
        private static int Main(string[] arguments)
        {
            if (HasArgument(arguments, "--self-test")) return SelfTests.Run();
            if (HasArgument(arguments, "--exit")) return SignalExit();

            string stateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageBar");
            Log.PathValue = Path.Combine(stateRoot, "companion.log");
            Directory.CreateDirectory(stateRoot);
            TryEnablePerMonitorDpi();

            bool created;
            using (var mutex = new Mutex(true, MutexName, out created))
            {
                if (!created)
                {
                    Log.Write("duplicate companion instance ignored");
                    return 0;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                try
                {
                    using (var context = new CompanionContext(stateRoot)) Application.Run(context);
                    return 0;
                }
                catch (Exception ex)
                {
                    Log.Write("fatal: " + ex);
                    return 1;
                }
                finally
                {
                    try { mutex.ReleaseMutex(); } catch { }
                }
            }
        }

        private static bool HasArgument(string[] arguments, string expected)
        {
            foreach (string argument in arguments)
                if (String.Equals(argument, expected, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static int SignalExit()
        {
            try
            {
                using (EventWaitHandle signal = EventWaitHandle.OpenExisting(ExitEventName)) signal.Set();
                return 0;
            }
            catch { return 0; }
        }

        private static void TryEnablePerMonitorDpi()
        {
            try { NativeMethods.SetProcessDpiAwarenessContext(new IntPtr(-4)); }
            catch { }
        }
    }

    internal sealed class CompanionContext : ApplicationContext
    {
        private readonly string _settingsPath;
        private readonly string _hostStatePath;
        private readonly string _configPath;
        private readonly AppSettings _settings;
        private Texts _texts;
        private readonly Control _dispatcher;
        private readonly NotifyIcon _tray;
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem _connectionItem;
        private readonly ToolStripMenuItem _connectionDetailItem;
        private readonly ToolStripMenuItem _showItem;
        private readonly ToolStripMenuItem _modeItem;
        private readonly ToolStripMenuItem _independentItem;
        private readonly ToolStripMenuItem _attachedItem;
        private readonly ToolStripMenuItem _languageItem;
        private readonly ToolStripMenuItem _followLanguageItem;
        private readonly ToolStripMenuItem _chineseLanguageItem;
        private readonly ToolStripMenuItem _englishLanguageItem;
        private readonly ToolStripMenuItem _startupItem;
        private readonly ToolStripMenuItem _refreshItem;
        private readonly ToolStripMenuItem _exitItem;
        private readonly OverlayForm _overlay;
        private readonly System.Windows.Forms.Timer _presentationTimer;
        private readonly System.Windows.Forms.Timer _themeDebounce;
        private readonly AutoResetEvent _workerWake = new AutoResetEvent(false);
        private readonly ManualResetEvent _stop = new ManualResetEvent(false);
        private readonly EventWaitHandle _exitSignal;
        private readonly RegisteredWaitHandle _exitRegistration;
        private readonly WindowEventMonitor _windowEvents;
        private readonly FileSystemWatcher _themeWatcher;
        private Thread _worker;
        private AppServerClient _client;
        private ThemeSet _themes;
        private UsageSnapshot _snapshot = new UsageSnapshot();
        private ConnectionKind _connection = ConnectionKind.NoCodex;
        private string _connectionDetail = String.Empty;
        private IntPtr _codexWindow = IntPtr.Zero;
        private int _manualRefresh;
        private int _rateUpdatePending;
        private int _exiting;
        private string _presentationState = String.Empty;

        internal CompanionContext(string stateRoot)
        {
            _settingsPath = Path.Combine(stateRoot, "settings.json");
            _hostStatePath = Path.Combine(stateRoot, "companion-state.json");
            string codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (String.IsNullOrWhiteSpace(codexHome)) codexHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            _configPath = Path.Combine(codexHome, "config.toml");
            _settings = AppSettings.Load(_settingsPath);
            _themes = ThemeReader.Load(_configPath);
            _texts = Texts.Resolve(_settings.Language, _themes.Language);

            _dispatcher = new Control();
            _dispatcher.CreateControl();
            _overlay = new OverlayForm();
            _overlay.ApplyTexts(_texts);
            _overlay.ApplyTheme(_themes.Active);
            _overlay.SetMode(_settings.Mode);
            _overlay.OverlaySizeChanged += RefreshPresentation;
            _overlay.IndependentPositionChanged += SaveIndependentPosition;

            _connectionItem = new ToolStripMenuItem();
            _connectionItem.Enabled = false;
            _connectionDetailItem = new ToolStripMenuItem();
            _connectionDetailItem.Enabled = false;
            _showItem = new ToolStripMenuItem(_texts.ShowWindow);
            _showItem.Click += ToggleVisibility;
            _modeItem = new ToolStripMenuItem(_texts.DisplayMode);
            _independentItem = new ToolStripMenuItem(_texts.Independent);
            _attachedItem = new ToolStripMenuItem(_texts.Attached);
            _independentItem.Click += delegate { SetDisplayMode(DisplayMode.Independent); };
            _attachedItem.Click += delegate { SetDisplayMode(DisplayMode.Attached); };
            _modeItem.DropDownItems.Add(_independentItem);
            _modeItem.DropDownItems.Add(_attachedItem);
            _languageItem = new ToolStripMenuItem(_texts.Language);
            _followLanguageItem = new ToolStripMenuItem(_texts.FollowSystem);
            _chineseLanguageItem = new ToolStripMenuItem(_texts.ChineseLanguage);
            _englishLanguageItem = new ToolStripMenuItem(_texts.EnglishLanguage);
            _followLanguageItem.Click += delegate { SetLanguageMode(LanguageMode.FollowCodex); };
            _chineseLanguageItem.Click += delegate { SetLanguageMode(LanguageMode.Chinese); };
            _englishLanguageItem.Click += delegate { SetLanguageMode(LanguageMode.English); };
            _languageItem.DropDownItems.Add(_followLanguageItem);
            _languageItem.DropDownItems.Add(_chineseLanguageItem);
            _languageItem.DropDownItems.Add(_englishLanguageItem);
            _startupItem = new ToolStripMenuItem(_texts.StartWithWindows);
            _startupItem.Click += ToggleStartup;
            _refreshItem = new ToolStripMenuItem(_texts.Refresh);
            _refreshItem.Click += RequestRefresh;
            _exitItem = new ToolStripMenuItem(_texts.Exit);
            _exitItem.Click += delegate { BeginExit(); };

            _menu = new ContextMenuStrip();
            _menu.Items.Add(_connectionItem);
            _menu.Items.Add(_connectionDetailItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_refreshItem);
            _menu.Items.Add(_showItem);
            _menu.Items.Add(_modeItem);
            _menu.Items.Add(_languageItem);
            _menu.Items.Add(_startupItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_exitItem);
            _menu.AutoClose = true;
            _menu.Opening += delegate { UpdateMenu(); };
            _menu.LostFocus += delegate
            {
                try { _menu.BeginInvoke((MethodInvoker)CloseMenuIfInactive); } catch { }
            };

            Icon icon = null;
            try { icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            if (icon == null) icon = SystemIcons.Application;
            _tray = new NotifyIcon();
            _tray.Icon = icon;
            _tray.Text = "Codex Usage Bar";
            _tray.ContextMenuStrip = _menu;
            _tray.Visible = true;
            _tray.MouseUp += OnTrayMouseUp;

            _presentationTimer = new System.Windows.Forms.Timer();
            _presentationTimer.Interval = 5000;
            _presentationTimer.Tick += delegate { RefreshPresentation(); };
            _presentationTimer.Start();

            _themeDebounce = new System.Windows.Forms.Timer();
            _themeDebounce.Interval = 350;
            _themeDebounce.Tick += delegate
            {
                _themeDebounce.Stop();
                ReloadTheme();
            };

            _themeWatcher = CreateThemeWatcher();
            _windowEvents = new WindowEventMonitor(PostPresentationRefresh);
            bool created;
            _exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, CompanionHost.ExitEventName, out created);
            _exitRegistration = ThreadPool.RegisterWaitForSingleObject(_exitSignal, delegate(object state, bool timedOut) { PostExit(); }, null, Timeout.Infinite, true);

            WriteHostState();
            UpdateMenu();
            _worker = new Thread(new ThreadStart(delegate
            {
                try { ConnectionWorker(); }
                catch (Exception ex)
                {
                    Log.Write("connection worker stopped: " + ex);
                    PostConnection(ConnectionKind.Failed, _texts.InterfaceUnavailable, new UsageSnapshot());
                }
            }));
            _worker.IsBackground = true;
            _worker.Name = "Codex Usage Bar App Server";
            _worker.Start();
            Log.Write("companion active v" + CompanionHost.Version + " mode=" + _settings.Mode + " visible=" + _settings.Visible);
        }

        private FileSystemWatcher CreateThemeWatcher()
        {
            try
            {
                string directory = Path.GetDirectoryName(_configPath);
                if (!Directory.Exists(directory)) return null;
                var watcher = new FileSystemWatcher(directory, Path.GetFileName(_configPath));
                watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size;
                FileSystemEventHandler changed = delegate { PostThemeReload(); };
                RenamedEventHandler renamed = delegate { PostThemeReload(); };
                watcher.Changed += changed;
                watcher.Created += changed;
                watcher.Deleted += changed;
                watcher.Renamed += renamed;
                watcher.EnableRaisingEvents = true;
                return watcher;
            }
            catch (Exception ex)
            {
                Log.Write("config watcher unavailable: " + ex.Message);
                return null;
            }
        }

        private void PostThemeReload()
        {
            try { _dispatcher.BeginInvoke((MethodInvoker)delegate { _themeDebounce.Stop(); _themeDebounce.Start(); }); }
            catch { }
        }

        private void ReloadTheme()
        {
            _themes = ThemeReader.Load(_configPath);
            _overlay.ApplyTheme(_themes.Active);
            if (_settings.Language == LanguageMode.FollowCodex) ApplyLanguage();
            RefreshPresentation();
            Log.Write("config reloaded appearance=" + _themes.Appearance + " language=" + (_themes.Language.Length == 0 ? "system" : _themes.Language));
        }

        private void OnTrayMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            UpdateMenu();
            _menu.Show(Cursor.Position);
            _menu.Focus();
        }

        private void CloseMenuIfInactive()
        {
            if (!_menu.ContainsFocus && !_modeItem.DropDown.ContainsFocus && !_languageItem.DropDown.ContainsFocus)
                _menu.Close(ToolStripDropDownCloseReason.AppFocusChange);
        }

        private void ToggleVisibility(object sender, EventArgs e)
        {
            _settings.Visible = !_settings.Visible;
            _settings.Save(_settingsPath);
            RefreshPresentation();
            UpdateMenu();
        }

        private void SetDisplayMode(DisplayMode mode)
        {
            if (_settings.Mode == mode) return;
            _settings.Mode = mode;
            _overlay.SetMode(mode);
            _settings.Save(_settingsPath);
            RefreshPresentation();
            UpdateMenu();
            Log.Write("display mode=" + mode);
        }

        private void SetLanguageMode(LanguageMode mode)
        {
            if (_settings.Language == mode) return;
            _settings.Language = mode;
            _settings.Save(_settingsPath);
            ApplyLanguage();
            Log.Write("language mode=" + mode + " resolved=" + (_texts.Chinese ? "zh" : "en"));
        }

        private void ApplyLanguage()
        {
            _texts = Texts.Resolve(_settings.Language, _themes.Language);
            _overlay.ApplyTexts(_texts);
            if (_connection == ConnectionKind.Connected && _snapshot.DisplayWindows.Count == 0) _connectionDetail = _texts.NoData;
            UpdateMenu();
            UpdateTrayText();
            RefreshPresentation();
        }

        private void RequestRefresh(object sender, EventArgs e)
        {
            Interlocked.Exchange(ref _manualRefresh, 1);
            _workerWake.Set();
            Log.Write("manual refresh requested");
        }

        private void ToggleStartup(object sender, EventArgs e)
        {
            try { StartupRegistration.SetEnabled(!StartupRegistration.IsEnabled()); }
            catch (Exception ex) { Log.Write("startup toggle failed: " + ex.Message); }
            UpdateMenu();
        }

        private void SaveIndependentPosition(int x, int y)
        {
            if (_settings.Mode != DisplayMode.Independent) return;
            _settings.IndependentX = x;
            _settings.IndependentY = y;
            _settings.Save(_settingsPath);
        }

        private void UpdateMenu()
        {
            _connectionItem.Text = _texts.Connection(_connection);
            _connectionDetailItem.Visible = !String.IsNullOrEmpty(_connectionDetail);
            _connectionDetailItem.Text = _connectionDetail;
            _showItem.Text = _texts.ShowWindow;
            _showItem.Checked = _settings.Visible;
            _modeItem.Text = _texts.DisplayMode;
            _independentItem.Text = _texts.Independent;
            _attachedItem.Text = _texts.Attached;
            _independentItem.Checked = _settings.Mode == DisplayMode.Independent;
            _attachedItem.Checked = _settings.Mode == DisplayMode.Attached;
            _languageItem.Text = _texts.Language;
            _followLanguageItem.Text = _texts.FollowSystem;
            _chineseLanguageItem.Text = _texts.ChineseLanguage;
            _englishLanguageItem.Text = _texts.EnglishLanguage;
            _followLanguageItem.Checked = _settings.Language == LanguageMode.FollowCodex;
            _chineseLanguageItem.Checked = _settings.Language == LanguageMode.Chinese;
            _englishLanguageItem.Checked = _settings.Language == LanguageMode.English;
            _startupItem.Text = _texts.StartWithWindows;
            _startupItem.Checked = StartupRegistration.IsEnabled();
            _refreshItem.Text = _texts.Refresh;
            _refreshItem.Enabled = _connection != ConnectionKind.Connecting;
            _exitItem.Text = _texts.Exit;
        }

        private void SetConnectionUi(ConnectionKind kind, string detail, UsageSnapshot snapshot)
        {
            _connection = kind;
            _connectionDetail = detail ?? String.Empty;
            if (snapshot != null)
            {
                _snapshot = snapshot;
                _overlay.ApplySnapshot(snapshot);
            }
            UpdateMenu();
            UpdateTrayText();
            RefreshPresentation();
        }

        private void UpdateTrayText()
        {
            string text = "Codex Usage Bar · " + (_connection == ConnectionKind.Connected ? (_texts.Chinese ? "已连接" : "Connected") : (_texts.Chinese ? "未连接" : "Disconnected"));
            LimitWindow tightest = _snapshot.Tightest;
            if (_connection == ConnectionKind.Connected && tightest != null)
                text += " · " + Math.Round(tightest.Remaining, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture) + "%";
            if (text.Length > 63) text = text.Substring(0, 63);
            try { _tray.Text = text; } catch { }
        }

        private void PostConnection(ConnectionKind kind, string detail, UsageSnapshot snapshot)
        {
            try { _dispatcher.BeginInvoke((MethodInvoker)delegate { SetConnectionUi(kind, detail, snapshot); }); }
            catch { }
        }

        private void RefreshPresentation()
        {
            bool dataReady = _connection == ConnectionKind.Connected && _snapshot != null && _snapshot.DisplayWindows.Count > 0;
            if (!_settings.Visible || !dataReady)
            {
                HideOverlay(!_settings.Visible ? "user-hidden" : "connection-or-data-unavailable");
                return;
            }
            if (_settings.Mode == DisplayMode.Independent)
            {
                PositionIndependent();
                ShowOverlay("independent");
                return;
            }

            bool codexForeground = CodexLocator.IsForegroundCodex();
            IntPtr next = CodexLocator.FindBestWindow();
            if (next != IntPtr.Zero) _codexWindow = next;
            Rectangle client;
            if (!CodexLocator.TryClientBounds(_codexWindow, out client))
            {
                HideOverlay("codex-window-unavailable");
                return;
            }
            int x = client.Left + Math.Max(0, (client.Width - _overlay.Width) / 2);
            int y = client.Top + Math.Max(0, (OverlayForm.ToolbarHeight - Math.Min(_overlay.Height, OverlayForm.ToolbarHeight)) / 2);
            _overlay.SetProgrammaticLocation(x, y);
            _overlay.BringAboveCodex(_codexWindow, false);
            ShowOverlay(codexForeground ? "attached-active" : "attached-background");
            if (codexForeground) _overlay.BringAboveCodex(_codexWindow, true);
        }

        private void ShowOverlay(string state)
        {
            if (!_overlay.Visible) _overlay.Show();
            NotePresentation("shown:" + state);
        }

        private void HideOverlay(string reason)
        {
            if (_overlay.Visible) _overlay.Hide();
            NotePresentation("hidden:" + reason);
        }

        private void NotePresentation(string state)
        {
            if (String.Equals(_presentationState, state, StringComparison.Ordinal)) return;
            _presentationState = state;
            Log.Write("overlay " + state);
        }

        private void PositionIndependent()
        {
            Point desired;
            if (_settings.IndependentX == Int32.MinValue || _settings.IndependentY == Int32.MinValue)
            {
                Rectangle work = Screen.PrimaryScreen.WorkingArea;
                desired = new Point(work.Right - _overlay.Width - 16, work.Bottom - _overlay.Height - 16);
            }
            else desired = new Point(_settings.IndependentX, _settings.IndependentY);
            Rectangle nearest = Screen.FromPoint(desired).WorkingArea;
            int x = Math.Max(nearest.Left, Math.Min(desired.X, nearest.Right - _overlay.Width));
            int y = Math.Max(nearest.Top, Math.Min(desired.Y, nearest.Bottom - _overlay.Height));
            _overlay.SetProgrammaticLocation(x, y);
        }

        private void PostPresentationRefresh()
        {
            try { _dispatcher.BeginInvoke((MethodInvoker)RefreshPresentation); }
            catch { }
        }

        private void ConnectionWorker()
        {
            int failures = 0;
            DateTime nextRate = DateTime.MinValue;
            DateTime nextUsage = DateTime.MinValue;
            UsageSnapshot snapshot = new UsageSnapshot();
            while (!_stop.WaitOne(0))
            {
                if (!CodexLocator.HasRunningProcess())
                {
                    StopClient();
                    snapshot = new UsageSnapshot();
                    PostConnection(ConnectionKind.NoCodex, String.Empty, snapshot);
                    failures = 0;
                    WaitWorker(3000);
                    continue;
                }

                if (_client == null || !_client.IsAlive)
                {
                    StopClient();
                    PostConnection(ConnectionKind.Connecting, String.Empty, null);
                    string error;
                    if (!TryConnect(out error))
                    {
                        failures++;
                        PostConnection(ConnectionKind.Failed, FriendlyError(error), new UsageSnapshot());
                        WaitWorker(RetryDelayMs(failures));
                        continue;
                    }
                    failures = 0;
                    try
                    {
                        snapshot = RefreshRate(new UsageSnapshot());
                        try { snapshot = RefreshUsage(snapshot); } catch (Exception ex) { Log.Write("initial usage read failed: " + ex.Message); }
                        string detail = snapshot.Windows.Count == 0 ? _texts.NoData : String.Empty;
                        PostConnection(ConnectionKind.Connected, detail, snapshot);
                        nextRate = DateTime.UtcNow.AddMinutes(2);
                        nextUsage = DateTime.UtcNow.AddMinutes(10);
                    }
                    catch (Exception ex)
                    {
                        Log.Write("initial interface validation failed: " + ex.Message);
                        StopClient();
                        failures++;
                        PostConnection(ConnectionKind.Failed, FriendlyError(ex.Message), new UsageSnapshot());
                        WaitWorker(RetryDelayMs(failures));
                        continue;
                    }
                }

                if (!CodexLocator.HasRunningProcess()) continue;
                bool manual = Interlocked.Exchange(ref _manualRefresh, 0) != 0;
                bool updated = Interlocked.Exchange(ref _rateUpdatePending, 0) != 0;
                try
                {
                    if (manual || updated || DateTime.UtcNow >= nextRate)
                    {
                        snapshot = RefreshRate(snapshot);
                        nextRate = DateTime.UtcNow.AddMinutes(2);
                    }
                    if (manual || DateTime.UtcNow >= nextUsage)
                    {
                        try { snapshot = RefreshUsage(snapshot); }
                        catch (Exception ex) { Log.Write("usage read failed: " + ex.Message); }
                        nextUsage = DateTime.UtcNow.AddMinutes(10);
                    }
                    string detail = snapshot.Windows.Count == 0 ? _texts.NoData : String.Empty;
                    PostConnection(ConnectionKind.Connected, detail, snapshot);
                }
                catch (Exception ex)
                {
                    Log.Write("app-server connection lost: " + ex.Message);
                    StopClient();
                    PostConnection(ConnectionKind.Failed, FriendlyError(ex.Message), new UsageSnapshot());
                    failures++;
                    WaitWorker(RetryDelayMs(failures));
                    continue;
                }
                WaitWorker(5000);
            }
            StopClient();
        }

        private bool TryConnect(out string error)
        {
            error = _texts.CliMissing;
            List<string> candidates = CodexCommandResolver.Candidates();
            foreach (string candidate in candidates)
            {
                var client = new AppServerClient();
                client.RateLimitsUpdated += delegate { Interlocked.Exchange(ref _rateUpdatePending, 1); _workerWake.Set(); };
                client.Exited += delegate { _workerWake.Set(); };
                try
                {
                    client.Start(candidate);
                    _client = client;
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    Log.Write("app-server candidate failed " + candidate + ": " + ex.Message);
                    client.Dispose();
                }
            }
            return false;
        }

        private UsageSnapshot RefreshRate(UsageSnapshot previous)
        {
            object response = _client.Request("account/rateLimits/read", null, 10000);
            var next = CloneSnapshot(previous);
            next.Windows.Clear();
            next.Windows.AddRange(AppDataParser.ParseRateLimits(response));
            Log.Write("rate limits refreshed windows=" + next.Windows.Count + " displayed=" + next.DisplayWindows.Count);
            return next;
        }

        private UsageSnapshot RefreshUsage(UsageSnapshot previous)
        {
            object response = _client.Request("account/usage/read", null, 10000);
            var next = CloneSnapshot(previous);
            AppDataParser.ParseUsage(response, next);
            Log.Write("token usage refreshed");
            return next;
        }

        private static UsageSnapshot CloneSnapshot(UsageSnapshot source)
        {
            var copy = new UsageSnapshot();
            if (source != null)
            {
                copy.Windows.AddRange(source.Windows);
                copy.YesterdayTokens = source.YesterdayTokens;
                copy.LifetimeTokens = source.LifetimeTokens;
            }
            return copy;
        }

        internal static int RetryDelayMs(int failures)
        {
            if (failures <= 1) return 1000;
            if (failures == 2) return 2000;
            if (failures == 3) return 5000;
            if (failures == 4) return 15000;
            return 30000;
        }

        private string FriendlyError(string error)
        {
            string value = error ?? String.Empty;
            if (value.IndexOf("auth", StringComparison.OrdinalIgnoreCase) >= 0 || value.IndexOf("login", StringComparison.OrdinalIgnoreCase) >= 0) return _texts.AuthFailed;
            if (value.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0 || value.IndexOf("missing", StringComparison.OrdinalIgnoreCase) >= 0) return _texts.CliMissing;
            return _texts.InterfaceUnavailable;
        }

        private void StopClient()
        {
            AppServerClient client = _client;
            _client = null;
            if (client != null) client.Dispose();
        }

        private void WaitWorker(int milliseconds)
        {
            WaitHandle.WaitAny(new WaitHandle[] { _stop, _workerWake }, milliseconds);
        }

        private void WriteHostState()
        {
            try
            {
                var state = new Dictionary<string, object>();
                state["version"] = CompanionHost.Version;
                state["pid"] = Process.GetCurrentProcess().Id;
                state["path"] = Application.ExecutablePath;
                File.WriteAllText(_hostStatePath, new JavaScriptSerializer().Serialize(state), new UTF8Encoding(false));
            }
            catch { }
        }

        private void PostExit()
        {
            try { _dispatcher.BeginInvoke((MethodInvoker)BeginExit); }
            catch { }
        }

        private void BeginExit()
        {
            if (Interlocked.Exchange(ref _exiting, 1) != 0) return;
            Log.Write("exit requested");
            _tray.Visible = false;
            _overlay.Hide();
            _stop.Set();
            _workerWake.Set();
            StopClient();
            if (_worker != null && _worker.IsAlive) _worker.Join(3500);
            ExitThread();
        }

        protected override void ExitThreadCore()
        {
            _stop.Set();
            _workerWake.Set();
            base.ExitThreadCore();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _presentationTimer.Stop(); } catch { }
                try { _themeDebounce.Stop(); } catch { }
                if (_themeWatcher != null) try { _themeWatcher.Dispose(); } catch { }
                try { _windowEvents.Dispose(); } catch { }
                try { _exitRegistration.Unregister(null); } catch { }
                try { _exitSignal.Dispose(); } catch { }
                try { _tray.Visible = false; _tray.Dispose(); } catch { }
                try { _menu.Dispose(); } catch { }
                try { _overlay.Dispose(); } catch { }
                try { _dispatcher.Dispose(); } catch { }
                try { File.Delete(_hostStatePath); } catch { }
                _workerWake.Dispose();
                _stop.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal static class SelfTests
    {
        internal static int Run()
        {
            Log.Disabled = true;
            try
            {
                ThemeSet theme = ThemeReader.Parse("[desktop]\nappearanceTheme=\"dark\"\nlanguage=\"zh-Hant\"\n[desktop.appearanceDarkChromeTheme]\naccent=\"#3dcd6e\"\nink=\"#fcfcfc\"\nsurface=\"#111111\"\n[desktop.appearanceDarkChromeTheme.fonts]\nui=\"Inter\"");
                Assert(theme.Appearance == "dark", "theme selection");
                Assert(theme.Dark.Accent.R == 61 && theme.Dark.Accent.G == 205 && theme.Dark.Accent.B == 110, "accent parsing");
                Assert(theme.Dark.Surface.R == 17, "surface parsing");
                Assert(Texts.Resolve(LanguageMode.FollowCodex, theme.Language).Chinese, "Codex Chinese-family language");
                Assert(!Texts.Resolve(LanguageMode.FollowCodex, "fr-FR").Chinese, "unsupported language falls back to English");
                Assert(Texts.Resolve(LanguageMode.Chinese, "en-US").Chinese, "language override");
                Assert(new Texts(true).Attached == "跟随 Codex" && new Texts(true).FollowSystem == "跟随系统", "Chinese menu labels");
                Assert(new Texts(false).Attached == "Follow Codex" && new Texts(false).FollowSystem == "Follow system", "English menu labels");

                string sample = "{\"rateLimits\":{\"primary\":{\"usedPercent\":25,\"windowDurationMins\":300,\"resetsAt\":1787144400},\"secondary\":{\"usedPercent\":82,\"windowDurationMins\":10080,\"resetsAt\":1787749200},\"tertiary\":{\"usedPercent\":10,\"windowDurationMins\":1440,\"resetsAt\":1787800000}}}";
                object parsed = new JavaScriptSerializer().DeserializeObject(sample);
                List<LimitWindow> windows = AppDataParser.ParseRateLimits(parsed);
                Assert(windows.Count == 3, "raw rate window count");
                Assert(Math.Abs(windows[0].Remaining - 75) < 0.001, "remaining calculation");
                var snapshot = new UsageSnapshot();
                snapshot.Windows.AddRange(windows);
                Assert(snapshot.DisplayWindows.Count == 2, "five-hour and weekly filtering");
                Assert(snapshot.DisplayWindows[0].WindowDurationMins == 300 && snapshot.DisplayWindows[1].WindowDurationMins == 10080,
                    "five-hour and weekly ordering");
                var weeklyOnly = new UsageSnapshot();
                weeklyOnly.Windows.Add(snapshot.DisplayWindows[1]);
                Assert(weeklyOnly.DisplayWindows.Count == 1 && weeklyOnly.DisplayWindows[0].WindowDurationMins == 10080,
                    "weekly-only fallback");

                string yesterday = DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                string usageSample = "{\"summary\":{\"lifetimeTokens\":4567},\"dailyUsageBuckets\":[{\"startDate\":\"" + yesterday + "\",\"tokens\":1234}]}";
                AppDataParser.ParseUsage(new JavaScriptSerializer().DeserializeObject(usageSample), snapshot);
                Assert(snapshot.YesterdayTokens == 1234 && snapshot.LifetimeTokens == 4567, "yesterday token parsing");
                using (var owner = new Form())
                using (var overlay = new OverlayForm())
                {
                    overlay.ApplySnapshot(snapshot);
                    int collapsedWidth = overlay.Width;
                    Assert(overlay.Height == OverlayForm.ToolbarHeight - 2, "collapsed toolbar height");
                    overlay.SetExpanded(true);
                    Assert(overlay.Width == collapsedWidth, "stable dynamic width");
                    overlay.SetMode(DisplayMode.Attached);
                    IntPtr overlayHandle = overlay.Handle;
                    overlay.BringAboveCodex(owner.Handle, true);
                    Assert(NativeMethods.GetWindow(overlayHandle, NativeMethods.GW_OWNER) == owner.Handle, "attached owner level");
                    overlay.SetMode(DisplayMode.Independent);
                    Assert(NativeMethods.GetWindow(overlayHandle, NativeMethods.GW_OWNER) == IntPtr.Zero, "independent owner cleared");
                }
                Assert(Formatters.CompactTokens(999) == "999", "token 999");
                Assert(Formatters.CompactTokens(1250) == "1.3K", "token 1250");
                Assert(Formatters.CompactTokens(999950) == "1M", "token promotion");
                Assert(CompanionContext.RetryDelayMs(1) == 1000 && CompanionContext.RetryDelayMs(5) == 30000, "retry policy");
                return 0;
            }
            catch { return 1; }
        }

        private static void Assert(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("self-test failed: " + name);
        }
    }
}
