using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace CodexUsageBar
{
    internal sealed class PendingRequest : IDisposable
    {
        internal readonly ManualResetEvent Done = new ManualResetEvent(false);
        internal object Result;
        internal string Error;
        public void Dispose() { Done.Dispose(); }
    }

    internal sealed class AppServerClient : IDisposable
    {
        private readonly object _gate = new object();
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();
        private readonly Dictionary<string, PendingRequest> _pending = new Dictionary<string, PendingRequest>();
        private Process _process;
        private StreamWriter _input;
        private int _nextId;
        private bool _stopping;

        internal event Action RateLimitsUpdated;
        internal event Action Exited;

        internal bool IsAlive
        {
            get
            {
                lock (_gate)
                {
                    try { return _process != null && !_process.HasExited; }
                    catch { return false; }
                }
            }
        }

        internal void Start(string command)
        {
            Stop();
            _stopping = false;
            ProcessStartInfo startInfo = CodexCommandResolver.CreateStartInfo(command);
            Log.Write("starting app-server via " + command);
            var process = new Process();
            process.StartInfo = startInfo;
            process.EnableRaisingEvents = true;
            process.OutputDataReceived += OnOutput;
            process.ErrorDataReceived += OnError;
            process.Exited += OnExited;
            if (!process.Start()) throw new InvalidOperationException("Codex app-server did not start");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            lock (_gate)
            {
                _process = process;
                _input = process.StandardInput;
                _input.AutoFlush = true;
            }
            if (process.WaitForExit(80)) throw new InvalidOperationException("Codex app-server exited during startup");

            var clientInfo = new Dictionary<string, object>();
            clientInfo["name"] = "codex-usage-bar";
            clientInfo["title"] = "Codex Usage Bar";
            clientInfo["version"] = CompanionHost.Version;
            var capabilities = new Dictionary<string, object>();
            capabilities["experimentalApi"] = true;
            var parameters = new Dictionary<string, object>();
            parameters["clientInfo"] = clientInfo;
            parameters["capabilities"] = capabilities;
            Request("initialize", parameters, 10000);
            Notify("initialized", null);
            Log.Write("app-server initialized");
        }

        internal object Request(string method, object parameters, int timeoutMs)
        {
            int id = Interlocked.Increment(ref _nextId);
            string key = id.ToString(CultureInfo.InvariantCulture);
            using (var pending = new PendingRequest())
            {
                lock (_gate) _pending[key] = pending;
                try
                {
                    var message = new Dictionary<string, object>();
                    message["method"] = method;
                    message["id"] = id;
                    if (parameters != null) message["params"] = parameters;
                    Write(message);
                    if (!pending.Done.WaitOne(timeoutMs)) throw new TimeoutException("App-server request timed out: " + method);
                    if (!String.IsNullOrEmpty(pending.Error)) throw new InvalidOperationException(pending.Error);
                    return pending.Result;
                }
                finally
                {
                    lock (_gate) _pending.Remove(key);
                }
            }
        }

        internal void Notify(string method, object parameters)
        {
            var message = new Dictionary<string, object>();
            message["method"] = method;
            if (parameters != null) message["params"] = parameters;
            Write(message);
        }

        private void Write(Dictionary<string, object> message)
        {
            string line = _serializer.Serialize(message);
            lock (_gate)
            {
                if (_process == null || _input == null || _process.HasExited) throw new InvalidOperationException("Codex app-server stdin is not writable");
                _input.WriteLine(line);
            }
        }

        private void OnOutput(object sender, DataReceivedEventArgs e)
        {
            if (String.IsNullOrWhiteSpace(e.Data)) return;
            try
            {
                var message = _serializer.DeserializeObject(e.Data) as Dictionary<string, object>;
                if (message == null) return;
                object idValue;
                if (message.TryGetValue("id", out idValue) && idValue != null)
                {
                    string key = Convert.ToString(idValue, CultureInfo.InvariantCulture);
                    PendingRequest pending;
                    lock (_gate) _pending.TryGetValue(key, out pending);
                    if (pending == null) return;
                    object error;
                    object result;
                    if (message.TryGetValue("error", out error) && error != null) pending.Error = ExtractError(error);
                    else if (message.TryGetValue("result", out result)) pending.Result = result;
                    pending.Done.Set();
                    return;
                }
                object methodValue;
                string method = message.TryGetValue("method", out methodValue) ? Convert.ToString(methodValue, CultureInfo.InvariantCulture) : String.Empty;
                if (String.Equals(method, "account/rateLimits/updated", StringComparison.Ordinal))
                {
                    Action callback = RateLimitsUpdated;
                    if (callback != null) callback();
                }
            }
            catch (Exception ex)
            {
                Log.Write("ignored app-server output: " + ex.Message);
            }
        }

        private string ExtractError(object error)
        {
            var map = error as Dictionary<string, object>;
            object message;
            if (map != null && map.TryGetValue("message", out message)) return Convert.ToString(message, CultureInfo.InvariantCulture);
            return _serializer.Serialize(error);
        }

        private void OnError(object sender, DataReceivedEventArgs e)
        {
            if (!String.IsNullOrWhiteSpace(e.Data)) Log.Write("app-server: " + e.Data);
        }

        private void OnExited(object sender, EventArgs e)
        {
            List<PendingRequest> pending;
            lock (_gate)
            {
                pending = new List<PendingRequest>(_pending.Values);
                foreach (PendingRequest request in pending) request.Error = "Codex app-server exited";
            }
            foreach (PendingRequest request in pending) request.Done.Set();
            if (!_stopping)
            {
                Log.Write("app-server exited unexpectedly");
                Action callback = Exited;
                if (callback != null) callback();
            }
        }

        internal void Stop()
        {
            Process process;
            lock (_gate)
            {
                _stopping = true;
                process = _process;
                _process = null;
                try { if (_input != null) _input.Close(); } catch { }
                _input = null;
            }
            if (process == null) return;
            try
            {
                if (!process.HasExited && !process.WaitForExit(1200)) process.Kill();
            }
            catch { }
            try { process.Dispose(); } catch { }
        }

        public void Dispose() { Stop(); }
    }

    internal static class CodexCommandResolver
    {
        internal static List<string> Candidates()
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Add(result, seen, Environment.GetEnvironmentVariable("CODEX_EXECUTABLE"));

            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string binRoot = Path.Combine(local, "OpenAI", "Codex", "bin");
            try
            {
                if (Directory.Exists(binRoot))
                {
                    var directories = new List<DirectoryInfo>(new DirectoryInfo(binRoot).GetDirectories());
                    directories.Sort(delegate(DirectoryInfo left, DirectoryInfo right) { return right.LastWriteTimeUtc.CompareTo(left.LastWriteTimeUtc); });
                    foreach (DirectoryInfo directory in directories) Add(result, seen, Path.Combine(directory.FullName, "codex.exe"));
                    Add(result, seen, Path.Combine(binRoot, "codex.exe"));
                }
            }
            catch { }

            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            Add(result, seen, Path.Combine(roaming, "npm", "codex.cmd"));
            string pathValue = Environment.GetEnvironmentVariable("PATH") ?? String.Empty;
            foreach (string directory in pathValue.Split(Path.PathSeparator))
            {
                string clean = directory.Trim().Trim('"');
                if (clean.Length == 0) continue;
                Add(result, seen, Path.Combine(clean, "codex.cmd"));
                Add(result, seen, Path.Combine(clean, "codex.exe"));
                Add(result, seen, Path.Combine(clean, "codex.ps1"));
            }
            return result;
        }

        private static void Add(List<string> result, HashSet<string> seen, string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return;
            try
            {
                path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
                if (!File.Exists(path)) return;
                if (path.IndexOf("\\WindowsApps\\codex.exe", StringComparison.OrdinalIgnoreCase) >= 0) return;
                if (seen.Add(path)) result.Add(path);
            }
            catch { }
        }

        internal static ProcessStartInfo CreateStartInfo(string command)
        {
            string extension = Path.GetExtension(command).ToLowerInvariant();
            var info = new ProcessStartInfo();
            if (extension == ".cmd" || extension == ".bat")
            {
                info.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                info.Arguments = "/d /s /c \"chcp 65001>nul & call \\\"" + command + "\\\" app-server --listen stdio://\"";
            }
            else if (extension == ".ps1")
            {
                info.FileName = "powershell.exe";
                info.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + command + "\" app-server --listen stdio://";
            }
            else
            {
                info.FileName = command;
                info.Arguments = "app-server --listen stdio://";
            }
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.WindowStyle = ProcessWindowStyle.Hidden;
            info.RedirectStandardInput = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.StandardOutputEncoding = Encoding.UTF8;
            info.StandardErrorEncoding = Encoding.UTF8;
            return info;
        }
    }
}
