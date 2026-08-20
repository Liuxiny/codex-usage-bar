using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Codex Usage Bar Watcher Host")]
[assembly: System.Reflection.AssemblyProduct("Codex Usage Bar")]
[assembly: System.Reflection.AssemblyCompany("Codex Usage Bar")]
[assembly: System.Reflection.AssemblyVersion("0.4.16.0")]
[assembly: System.Reflection.AssemblyFileVersion("0.4.16.0")]

namespace CodexUsageBar
{
    internal static class WatcherHost
    {
        private const string Version = "0.4.16";
        private static readonly string StateRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexUsageBar");
        private static readonly string EngineScript = Path.Combine(StateRoot, "engine", "codex-usage-bar.ps1");
        private static readonly string InstallState = Path.Combine(StateRoot, "install.json");
        private static readonly string InjectorState = Path.Combine(StateRoot, "state.json");
        private static readonly string HostState = Path.Combine(StateRoot, "watcher-host.json");
        private static readonly string FuseState = Path.Combine(StateRoot, "watcher-fuse.json");
        private static readonly string RefreshRequest = Path.Combine(StateRoot, "refresh.request");
        private static readonly string CurrentLocaleFile = Path.Combine(StateRoot, "locale.current");
        private static readonly string UserLocaleDir = Path.Combine(StateRoot, "locales");
        private static readonly string EngineLocaleDir = Path.Combine(StateRoot, "engine", "locales");
        private static readonly string LogPath = Path.Combine(StateRoot, "watcher-host.log");
        private static readonly HashSet<int> HandledPids = new HashSet<int>();
        private static readonly object Gate = new object();
        private static readonly ManualResetEvent StopRequested = new ManualResetEvent(false);
        private static readonly AutoResetEvent Wake = new AutoResetEvent(false);
        private static bool _fused;
        private static int _attachInProgress;
        private static int _refreshInProgress;

        [STAThread]
        private static int Main()
        {
            Directory.CreateDirectory(StateRoot);
            bool createdNew;
            using (var mutex = new Mutex(true, "Local\\CodexUsageBarWatcherHost", out createdNew))
            {
                if (!createdNew)
                {
                    Log("watcher host already running; duplicate instance exiting");
                    return 0;
                }

                WriteHostState();
                _fused = File.Exists(FuseState);
                Log("watcher host active pid=" + Process.GetCurrentProcess().Id + " fused=" + _fused);

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                Thread watcherThread = null;
                TrayApplicationContext context = null;
                try
                {
                    context = new TrayApplicationContext();
                    watcherThread = new Thread(WatchLoop);
                    watcherThread.Name = "Codex Usage Bar Watcher";
                    watcherThread.IsBackground = true;
                    watcherThread.Start();
                    Application.Run(context);
                    return 0;
                }
                catch (Exception ex)
                {
                    Log("watcher host fatal error: " + ex);
                    return 1;
                }
                finally
                {
                    StopRequested.Set();
                    Wake.Set();
                    if (watcherThread != null && watcherThread.IsAlive)
                    {
                        try { watcherThread.Join(5000); } catch { }
                    }
                    if (context != null)
                    {
                        try { context.Dispose(); } catch { }
                    }
                    try { File.Delete(HostState); } catch { }
                    try { mutex.ReleaseMutex(); } catch { }
                }
            }
        }

        private static string NormalizeLocaleCode(string value)
        {
            return (value ?? string.Empty).Trim().Replace('_', '-').ToLowerInvariant();
        }

        private static string ReadDocumentLocale()
        {
            try
            {
                if (!File.Exists(CurrentLocaleFile)) return string.Empty;
                return (File.ReadAllText(CurrentLocaleFile, Encoding.UTF8) ?? string.Empty).Trim();
            }
            catch { return string.Empty; }
        }

        private static string ResolveLocaleCode(string raw)
        {
            string code = NormalizeLocaleCode(raw);
            if (code == "zh" || code.StartsWith("zh-", StringComparison.Ordinal)) return "zh";
            if (!string.IsNullOrEmpty(code) && FindLocaleFile(code) != null) return code;
            int dash = code.IndexOf('-');
            string primary = dash > 0 ? code.Substring(0, dash) : code;
            if (!string.IsNullOrEmpty(primary) && FindLocaleFile(primary) != null) return primary;
            return "en";
        }

        private static string FindLocaleFile(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;
            string name = NormalizeLocaleCode(code) + ".json";
            string user = Path.Combine(UserLocaleDir, name);
            if (File.Exists(user)) return user;
            string engine = Path.Combine(EngineLocaleDir, name);
            if (File.Exists(engine)) return engine;
            return null;
        }

        private static Dictionary<string, object> TryLoadLocaleCatalogFile(string file)
        {
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return null;
            try
            {
                var serializer = new JavaScriptSerializer();
                object parsed = serializer.DeserializeObject(File.ReadAllText(file, Encoding.UTF8));
                return parsed as Dictionary<string, object>;
            }
            catch (Exception ex)
            {
                Log("locale catalog ignored (" + file + "): " + ex.Message);
                return null;
            }
        }

        private static Dictionary<string, object> LoadLocaleCatalog(string code)
        {
            string normalized = NormalizeLocaleCode(code);
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(normalized))
            {
                candidates.Add(Path.Combine(UserLocaleDir, normalized + ".json"));
                candidates.Add(Path.Combine(EngineLocaleDir, normalized + ".json"));
            }
            if (!string.Equals(normalized, "en", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(Path.Combine(UserLocaleDir, "en.json"));
                candidates.Add(Path.Combine(EngineLocaleDir, "en.json"));
            }
            foreach (string file in candidates)
            {
                var catalog = TryLoadLocaleCatalogFile(file);
                if (catalog != null) return catalog;
            }
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        private static string LocaleMessage(Dictionary<string, object> catalog, string path, string fallback)
        {
            object current = catalog;
            foreach (string part in path.Split('.'))
            {
                var map = current as Dictionary<string, object>;
                if (map == null || !map.TryGetValue(part, out current)) return fallback;
            }
            return current as string ?? fallback;
        }

        private sealed class TrayApplicationContext : ApplicationContext
        {
            private readonly NotifyIcon _tray;
            private readonly ContextMenuStrip _menu;
            private readonly ToolStripMenuItem _refreshItem;
            private readonly ToolStripMenuItem _exitItem;
            private readonly Control _dispatcher;
            private readonly System.Windows.Forms.Timer _localeTimer;
            private string _localeCode = string.Empty;
            private int _exiting;

            internal TrayApplicationContext()
            {
                _dispatcher = new Control();
                _dispatcher.CreateControl();

                _refreshItem = new ToolStripMenuItem("Refresh");
                _exitItem = new ToolStripMenuItem("Exit");
                _refreshItem.Click += OnRefresh;
                _exitItem.Click += OnExit;

                _menu = new ContextMenuStrip();
                _menu.Items.Add(_refreshItem);
                _menu.Items.Add(new ToolStripSeparator());
                _menu.Items.Add(_exitItem);

                Icon icon = null;
                try { icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
                if (icon == null) icon = SystemIcons.Application;

                _tray = new NotifyIcon();
                _tray.Icon = icon;
                _tray.Text = "Codex Usage Bar";
                _tray.ContextMenuStrip = _menu;
                _tray.Visible = true;
                _tray.DoubleClick += OnRefresh;

                ApplyLocale(true);
                _localeTimer = new System.Windows.Forms.Timer();
                _localeTimer.Interval = 1000;
                _localeTimer.Tick += delegate { ApplyLocale(false); };
                _localeTimer.Start();
                Log("tray icon active locale=" + _localeCode);
            }

            private void ApplyLocale(bool force)
            {
                string next = ResolveLocaleCode(ReadDocumentLocale());
                if (!force && string.Equals(next, _localeCode, StringComparison.OrdinalIgnoreCase)) return;
                _localeCode = next;
                var catalog = LoadLocaleCatalog(next);
                _refreshItem.Text = LocaleMessage(catalog, "tray.refresh", "Refresh");
                _exitItem.Text = LocaleMessage(catalog, "tray.exit", "Exit");
            }

            private void OnRefresh(object sender, EventArgs e)
            {
                ApplyLocale(true);
                if (Interlocked.CompareExchange(ref _refreshInProgress, 1, 0) != 0) return;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        RequestManualRefresh();
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _refreshInProgress, 0);
                    }
                });
            }

            private void OnExit(object sender, EventArgs e)
            {
                if (Interlocked.Exchange(ref _exiting, 1) != 0) return;
                _refreshItem.Enabled = false;
                _exitItem.Enabled = false;
                _tray.Visible = false;
                StopRequested.Set();
                Wake.Set();
                Log("tray exit requested");

                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        StopLiveUsageBar();
                    }
                    catch (Exception ex)
                    {
                        Log("tray exit cleanup failed: " + ex.Message);
                    }
                    finally
                    {
                        try
                        {
                            _dispatcher.BeginInvoke((MethodInvoker)delegate { ExitThread(); });
                        }
                        catch
                        {
                            try { Application.Exit(); } catch { }
                        }
                    }
                });
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    try { _localeTimer.Stop(); } catch { }
                    try { _localeTimer.Dispose(); } catch { }
                    try { _tray.Visible = false; } catch { }
                    try { _tray.Dispose(); } catch { }
                    try { _menu.Dispose(); } catch { }
                    try { _dispatcher.Dispose(); } catch { }
                }
                base.Dispose(disposing);
            }
        }

        private static void WatchLoop()
        {
            ManagementEventWatcher processWatcher = null;
            try
            {
                try
                {
                    processWatcher = new ManagementEventWatcher(
                        new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace WHERE ProcessName = 'ChatGPT.exe'"));
                    processWatcher.EventArrived += delegate { Wake.Set(); };
                    processWatcher.Start();
                    Log("process-start event watcher active");
                }
                catch (Exception ex)
                {
                    Log("process-start event watcher unavailable; using 3-second health polling: " + ex.Message);
                    if (processWatcher != null)
                    {
                        try { processWatcher.Dispose(); } catch { }
                        processWatcher = null;
                    }
                }

                var waits = new WaitHandle[] { StopRequested, Wake };
                while (!StopRequested.WaitOne(0))
                {
                    var current = GetCodexPids();
                    if (current.Count == 0)
                    {
                        lock (Gate) { HandledPids.Clear(); }
                        if (_fused)
                        {
                            ClearFuse();
                            Log("restart fuse cleared after Codex fully exited");
                        }
                    }
                    else if (!_fused && !HasHandledOverlap(current))
                    {
                        RunManagedAttach(true);
                    }

                    int timeout = processWatcher != null ? 3000 : 3000;
                    if (WaitHandle.WaitAny(waits, timeout) == 0) break;
                }
            }
            catch (Exception ex)
            {
                Log("watch loop error: " + ex);
            }
            finally
            {
                if (processWatcher != null)
                {
                    try { processWatcher.Stop(); } catch { }
                    try { processWatcher.Dispose(); } catch { }
                }
                Log("watch loop stopped");
            }
        }

        private static void RequestManualRefresh()
        {
            try
            {
                Directory.CreateDirectory(StateRoot);
                File.WriteAllText(RefreshRequest, DateTimeOffset.Now.ToString("o"), new UTF8Encoding(false));
                Log("manual refresh requested from tray");
            }
            catch (Exception ex)
            {
                Log("could not create refresh request: " + ex.Message);
                return;
            }

            // The normal path is zero-disruption: the existing injector consumes
            // refresh.request and immediately re-reads both rate limits and token
            // usage. If the recorded injector is gone, attempt attach-only recovery;
            // tray refresh never restarts Codex.
            if (!IsRecordedInjectorAlive())
            {
                var current = GetCodexPids();
                if (current.Count == 0)
                {
                    Log("manual refresh deferred: Codex is not running");
                    return;
                }
                Log("manual refresh found no live injector; attempting attach-only recovery");
                RunManagedAttach(false);
            }
        }

        private static bool IsRecordedInjectorAlive()
        {
            try
            {
                if (!File.Exists(InjectorState)) return false;
                string json = File.ReadAllText(InjectorState, Encoding.UTF8);
                Match match = Regex.Match(json, "\\\"injectorPid\\\"\\s*:\\s*(\\d+)", RegexOptions.IgnoreCase);
                int pid;
                if (!match.Success || !Int32.TryParse(match.Groups[1].Value, out pid) || pid <= 0) return false;
                using (Process process = Process.GetProcessById(pid))
                {
                    if (process.HasExited) return false;
                    return String.Equals(process.ProcessName, "node", StringComparison.OrdinalIgnoreCase) ||
                           String.Equals(process.ProcessName, "node.exe", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { return false; }
        }

        private static void StopLiveUsageBar()
        {
            if (!File.Exists(EngineScript))
            {
                Log("tray exit: engine missing; host will exit without DOM cleanup");
                return;
            }
            int port = ReadPort();
            int exitCode;
            bool completed = RunPowerShellEngine("-Stop -CdpPort " + port, 25000, out exitCode);
            if (!completed)
                Log("tray exit: engine stop timed out or could not start");
            else
                Log("tray exit: engine stop exit code=" + exitCode);
        }

        private static bool RunManagedAttach(bool allowRestart)
        {
            if (Interlocked.CompareExchange(ref _attachInProgress, 1, 0) != 0)
            {
                Log("managed attach already in progress; request coalesced");
                return false;
            }

            try
            {
                if (!File.Exists(EngineScript))
                {
                    Log("managed engine is missing; watcher will wait for reinstall");
                    if (allowRestart) SetFuse("engine-missing");
                    return false;
                }

                var before = GetCodexPids();
                if (before.Count == 0) return false;

                int port = ReadPort();
                string action = "-Launch -ManagedAttach -CdpPort " + port;
                if (allowRestart) action = "-Launch -RestartExisting -ManagedAttach -CdpPort " + port;

                Log((allowRestart ? "managed attach" : "attach-only recovery") +
                    " starting for Codex pids=" + Join(before) + " port=" + port);

                int exitCode;
                bool completed = RunPowerShellEngine(action, 70000, out exitCode);
                if (!completed)
                {
                    if (allowRestart)
                    {
                        SetFuse("managed-attach-timeout");
                        Log("managed attach timed out; automatic restarts disabled until Codex exits");
                    }
                    else
                    {
                        Log("attach-only recovery timed out; Codex was not restarted");
                    }
                    return false;
                }

                if (exitCode != 0)
                {
                    var afterFailure = GetCodexPids();
                    if (afterFailure.Count == 0)
                    {
                        Log("managed attach ended after Codex exited; no fuse retained");
                        return false;
                    }
                    if (allowRestart)
                    {
                        SetFuse("managed-attach-exit-" + exitCode);
                        Log("managed attach failed with exit code " + exitCode +
                            "; automatic restarts disabled until Codex fully exits");
                    }
                    else
                    {
                        Log("attach-only recovery failed with exit code " + exitCode + "; no restart attempted");
                    }
                    return false;
                }

                var after = GetCodexPids();
                lock (Gate)
                {
                    HandledPids.Clear();
                    foreach (int pid in after) HandledPids.Add(pid);
                }
                ClearFuse();
                Log("managed attach succeeded; handled Codex pids=" + Join(after));
                return true;
            }
            catch (Exception ex)
            {
                var remaining = GetCodexPids();
                if (allowRestart && remaining.Count > 0)
                {
                    SetFuse("managed-attach-exception");
                    Log("managed attach exception; automatic restarts disabled until Codex exits: " + ex.Message);
                }
                else
                {
                    Log("attach exception without restart: " + ex.Message);
                }
                return false;
            }
            finally
            {
                Interlocked.Exchange(ref _attachInProgress, 0);
            }
        }

        private static bool RunPowerShellEngine(string engineArguments, int timeoutMs, out int exitCode)
        {
            exitCode = -1;
            string systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string powershell = Path.Combine(systemDir, "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(powershell)) powershell = "powershell.exe";

            string args = "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy RemoteSigned -File " +
                Quote(EngineScript) + " " + engineArguments;

            var psi = new ProcessStartInfo();
            psi.FileName = powershell;
            psi.Arguments = args;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;

            try
            {
                using (var child = Process.Start(psi))
                {
                    if (child == null) return false;
                    if (!child.WaitForExit(timeoutMs))
                    {
                        try { child.Kill(); } catch { }
                        return false;
                    }
                    exitCode = child.ExitCode;
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log("PowerShell engine start failed: " + ex.Message);
                return false;
            }
        }

        private static bool HasHandledOverlap(HashSet<int> current)
        {
            lock (Gate)
            {
                foreach (int pid in current)
                    if (HandledPids.Contains(pid)) return true;
            }
            return false;
        }

        private static HashSet<int> GetCodexPids()
        {
            var result = new HashSet<int>();
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, ExecutablePath FROM Win32_Process WHERE Name = 'ChatGPT.exe'"))
                using (var objects = searcher.Get())
                {
                    foreach (ManagementObject item in objects)
                    {
                        try
                        {
                            string path = item["ExecutablePath"] as string;
                            if (!IsCodexPackagePath(path)) continue;
                            uint rawPid = Convert.ToUInt32(item["ProcessId"]);
                            if (rawPid > 0 && rawPid <= Int32.MaxValue) result.Add((int)rawPid);
                        }
                        catch { }
                        finally { try { item.Dispose(); } catch { } }
                    }
                }
            }
            catch { }
            return result;
        }

        private static bool IsCodexPackagePath(string path)
        {
            if (String.IsNullOrEmpty(path)) return false;
            string normalized = path.Replace('/', '\\');
            return normalized.IndexOf("\\WindowsApps\\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) >= 0 &&
                   normalized.EndsWith("\\app\\ChatGPT.exe", StringComparison.OrdinalIgnoreCase);
        }

        private static int ReadPort()
        {
            const int fallback = 9335;
            try
            {
                if (!File.Exists(InstallState)) return fallback;
                string json = File.ReadAllText(InstallState, Encoding.UTF8);
                Match match = Regex.Match(json, "\\\"cdpPort\\\"\\s*:\\s*(\\d+)", RegexOptions.IgnoreCase);
                int value;
                if (match.Success && Int32.TryParse(match.Groups[1].Value, out value) && value >= 1024 && value <= 65535)
                    return value;
            }
            catch { }
            return fallback;
        }

        private static void WriteHostState()
        {
            try
            {
                string hostPath = Process.GetCurrentProcess().MainModule.FileName;
                string json = "{\r\n" +
                    "  \"schemaVersion\": 2,\r\n" +
                    "  \"version\": \"" + Version + "\",\r\n" +
                    "  \"watcherPid\": " + Process.GetCurrentProcess().Id + ",\r\n" +
                    "  \"hostPath\": \"" + JsonEscape(hostPath) + "\",\r\n" +
                    "  \"tray\": true,\r\n" +
                    "  \"startedAt\": \"" + DateTimeOffset.Now.ToString("o") + "\"\r\n" +
                    "}\r\n";
                File.WriteAllText(HostState, json, new UTF8Encoding(false));
            }
            catch (Exception ex) { Log("could not write watcher host state: " + ex.Message); }
        }

        private static void SetFuse(string reason)
        {
            _fused = true;
            try
            {
                string json = "{\r\n" +
                    "  \"schemaVersion\": 1,\r\n" +
                    "  \"reason\": \"" + JsonEscape(reason) + "\",\r\n" +
                    "  \"createdAt\": \"" + DateTimeOffset.Now.ToString("o") + "\"\r\n" +
                    "}\r\n";
                File.WriteAllText(FuseState, json, new UTF8Encoding(false));
            }
            catch { }
        }

        private static void ClearFuse()
        {
            _fused = false;
            try { if (File.Exists(FuseState)) File.Delete(FuseState); } catch { }
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static string Join(HashSet<int> values)
        {
            var list = new List<string>();
            foreach (int value in values) list.Add(value.ToString());
            return String.Join(",", list.ToArray());
        }

        private static string JsonEscape(string value)
        {
            if (value == null) return "";
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
        }

        private static void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(StateRoot);
                File.AppendAllText(LogPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
