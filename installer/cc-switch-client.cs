using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace CodexUsageBar
{
    // Protocol adapted from farion1231/cc-switch (MIT); see THIRD-PARTY-NOTICES.md.
    internal static class CcJson
    {
        internal static object Parse(string text) { return new JavaScriptSerializer { MaxJsonLength = 2097152 }.DeserializeObject(text); }
        internal static string Encode(object value) { return new JavaScriptSerializer { MaxJsonLength = 2097152 }.Serialize(value); }
        internal static object Get(object obj, string key)
        {
            var map = obj as Dictionary<string, object>; object value;
            return map != null && map.TryGetValue(key, out value) ? value : null;
        }
        internal static string Text(object obj, string key) { return Get(obj, key) as string ?? String.Empty; }
        internal static double? Number(object obj, string key)
        {
            object value = Get(obj, key);
            if (!(value is int || value is long || value is double || value is decimal || value is float)) return null;
            double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return Double.IsNaN(number) || Double.IsInfinity(number) ? (double?)null : number;
        }
    }

    internal sealed class CcUsageRow
    {
        internal string Name, Extra, Unit, InvalidMessage;
        internal string Kind;
        internal double? WindowSeconds;
        internal long? ResetsAt;
        internal bool Valid;
        internal double? Total, Used, Remaining;
        internal string ValueText(bool chinese)
        {
            if (!Valid) return String.IsNullOrEmpty(InvalidMessage) ? (chinese ? "暂不可用" : "Unavailable") : InvalidMessage;
            if (Remaining.HasValue) return Remaining.Value.ToString("0.##", CultureInfo.InvariantCulture) + " " + Unit;
            if (Used.HasValue) return (chinese ? "已用 " : "Used ") + Used.Value.ToString("0.##", CultureInfo.InvariantCulture) + " " + Unit;
            if (Total.HasValue) return (chinese ? "总计 " : "Total ") + Total.Value.ToString("0.##", CultureInfo.InvariantCulture) + " " + Unit;
            return chinese ? "暂不可用" : "Unavailable";
        }
    }

    internal sealed class CcProvider
    {
        internal string Id, Name, Fingerprint, Code, BaseUrl, ApiKey, AccessToken, UserId, TemplateType;
        internal bool Official, Enabled;
        internal int TimeoutSeconds = 10, IntervalMinutes = 2;
    }

    internal static class CcSwitchReader
    {
        internal static string ConfigDirectory()
        {
            string custom = Environment.GetEnvironmentVariable("CODEXBAR_CC_SWITCH_HOME");
            if (!String.IsNullOrWhiteSpace(custom)) return Path.GetFullPath(custom);
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string normal = Path.Combine(home, ".cc-switch");
            // CC Switch's Tauri store can override the data directory.
            string store = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "com.ccswitch.desktop", "app_paths.json");
            if (File.Exists(store))
            {
                string configured = CcJson.Text(CcJson.Parse(File.ReadAllText(store)), "app_config_dir_override");
                if (!String.IsNullOrWhiteSpace(configured))
                {
                    if (configured == "~") configured = home;
                    else if (configured.StartsWith("~/") || configured.StartsWith("~\\")) configured = Path.Combine(home, configured.Substring(2));
                    return Path.GetFullPath(configured);
                }
            }
            string legacyHome = Environment.GetEnvironmentVariable("HOME");
            if (!File.Exists(Path.Combine(normal, "cc-switch.db")) && !String.IsNullOrWhiteSpace(legacyHome))
            {
                string legacy = Path.Combine(legacyHome, ".cc-switch");
                if (File.Exists(Path.Combine(legacy, "cc-switch.db"))) return legacy;
            }
            return normal;
        }

        internal static CcProvider ReadCurrent() { return ReadCurrent(ConfigDirectory()); }
        internal static string NormalizeDirectory(string directory)
        {
            string value = Environment.ExpandEnvironmentVariables((directory ?? "").Trim().Trim('"'));
            if (value.Length == 0) return value;
            if (String.Equals(Path.GetFileName(value), "cc-switch.db", StringComparison.OrdinalIgnoreCase)) value = Path.GetDirectoryName(value);
            return Path.GetFullPath(value);
        }
        internal static string RequireDataDirectory(string directory)
        {
            string value = NormalizeDirectory(directory);
            if (!File.Exists(Path.Combine(value, "cc-switch.db")))
                throw new InvalidOperationException("CC Switch data directory must contain cc-switch.db (not the installation folder)");
            return value;
        }
        internal static CcProvider ReadCurrent(string directory)
        {
            string database = Path.Combine(directory, "cc-switch.db");
            if (!File.Exists(database)) return null;
            string settingsFile = Path.Combine(directory, "settings.json");
            string selected = File.Exists(settingsFile) ? CcJson.Text(CcJson.Parse(File.ReadAllText(settingsFile)), "currentProviderCodex") : "";
            using (var db = new CcSqlite(database))
            {
                string[] row = String.IsNullOrEmpty(selected) ? null : db.Provider(selected);
                if (row == null) row = db.Provider(null);
                if (row == null) return null;
                object meta = CcJson.Parse(row[3]);
                object script = CcJson.Get(meta, "usage_script");
                object config = CcJson.Parse(row[4]);
                var provider = new CcProvider { Id = row[0], Name = row[1], Official = row[2] == "official" || row[0] == "codex-official" || CcJson.Text(meta, "providerType") == "codex_oauth" };
                provider.Enabled = Object.Equals(CcJson.Get(script, "enabled"), true);
                provider.Code = CcJson.Text(script, "code");
                provider.TemplateType = CcJson.Text(script, "templateType");
                provider.BaseUrl = CcJson.Text(script, "baseUrl").Trim();
                if (provider.BaseUrl.Length == 0) provider.BaseUrl = ConfigValue(CcJson.Text(config, "config"), "base_url");
                provider.BaseUrl = provider.BaseUrl.TrimEnd('/');
                provider.ApiKey = CcJson.Text(script, "apiKey").Trim();
                if (provider.ApiKey.Length == 0) provider.ApiKey = CcJson.Text(CcJson.Get(config, "auth"), "OPENAI_API_KEY");
                if (provider.ApiKey.Length == 0) provider.ApiKey = ConfigValue(CcJson.Text(config, "config"), "experimental_bearer_token");
                provider.AccessToken = CcJson.Get(script, "accessToken") as string;
                provider.UserId = CcJson.Get(script, "userId") as string;
                provider.TimeoutSeconds = (int)Math.Max(2, Math.Min(30, CcJson.Number(script, "timeout") ?? 10));
                provider.IntervalMinutes = (int)Math.Max(0, Math.Min(1440, CcJson.Number(script, "autoQueryInterval") ?? 2));
                using (var sha = SHA256.Create()) provider.Fingerprint = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(CcJson.Encode(row))));
                return provider;
            }
        }

        // Read only the active model provider, then the top-level fallback. Never take
        // credentials from an unrelated model_providers section.
        internal static string ConfigValue(string toml, string key)
        {
            var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            string section = ""; sections[section] = new Dictionary<string, string>();
            foreach (string raw in (toml ?? "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("["))
                {
                    int end = line.IndexOf(']');
                    if (end < 0) continue;
                    section = Regex.Replace(line.Substring(1, end - 1), "[\\\"']", "");
                    if (!sections.ContainsKey(section)) sections[section] = new Dictionary<string, string>();
                    continue;
                }
                Match m = Regex.Match(line, "^([A-Za-z_]+)\\s*=\\s*(\"(?:\\\\.|[^\"\\\\])*\"|'[^']*')\\s*(?:#.*)?$");
                if (!m.Success) continue;
                string value = m.Groups[2].Value;
                sections[section][m.Groups[1].Value] = value[0] == '\'' ? value.Substring(1, value.Length - 2) : CcJson.Parse(value) as string;
            }
            string active, result; Dictionary<string, string> provider;
            if (sections[""].TryGetValue("model_provider", out active) && sections.TryGetValue("model_providers." + active, out provider) && provider.TryGetValue(key, out result)) return result ?? "";
            return sections[""].TryGetValue(key, out result) ? result ?? "" : "";
        }
    }

    internal sealed class CcSqlite : IDisposable
    {
        private IntPtr _db;
        internal CcSqlite(string path)
        {
            if (sqlite3_open_v2(Encoding.UTF8.GetBytes(path + "\0"), out _db, 1, IntPtr.Zero) != 0)
            { Dispose(); throw new InvalidOperationException("CC Switch database unavailable"); }
            sqlite3_busy_timeout(_db, 1000);
        }
        internal string[] Provider(string id)
        {
            IntPtr statement;
            string sql = "SELECT id,name,category,meta,settings_config FROM providers WHERE app_type='codex' AND " + (id == null ? "is_current=1" : "id=?1") + " LIMIT 1";
            if (sqlite3_prepare16_v2(_db, sql, -1, out statement, IntPtr.Zero) != 0) throw new InvalidOperationException("Unsupported CC Switch database schema");
            try
            {
                if (id != null && sqlite3_bind_text16(statement, 1, id, id.Length * 2, new IntPtr(-1)) != 0) throw new InvalidOperationException("CC Switch selection failed");
                int code = sqlite3_step(statement);
                if (code == 101) return null;
                if (code != 100) throw new InvalidOperationException("CC Switch database busy");
                var result = new string[5];
                for (int i = 0; i < result.Length; i++) result[i] = Marshal.PtrToStringUni(sqlite3_column_text16(statement, i)) ?? "";
                return result;
            }
            finally { sqlite3_finalize(statement); }
        }
        public void Dispose() { if (_db != IntPtr.Zero) { sqlite3_close(_db); _db = IntPtr.Zero; } }
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2(byte[] path, out IntPtr db, int flags, IntPtr vfs);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close(IntPtr db);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_busy_timeout(IntPtr db, int ms);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] private static extern int sqlite3_prepare16_v2(IntPtr db, string sql, int bytes, out IntPtr statement, IntPtr tail);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] private static extern int sqlite3_bind_text16(IntPtr statement, int index, string value, int bytes, IntPtr destructor);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr statement);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_text16(IntPtr statement, int column);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr statement);
    }

    internal static class CcJavaScript
    {
        // Windows JSRT: no host objects, filesystem, network, require or ActiveX.
        // Runtime interrupt is enabled and the heap is bounded before evaluating code.
        internal static string Evaluate(string script, int timeoutMs)
        {
            if (script.Length > 2097152) throw new InvalidOperationException("CC Switch script too large");
            IntPtr runtime = IntPtr.Zero, context, value, text; UIntPtr length;
            object gate = new object(); bool active = true;
            System.Threading.Timer timer = null;
            try
            {
                Check(JsCreateRuntime(2, IntPtr.Zero, out runtime));
                Check(JsSetRuntimeMemoryLimit(runtime, new UIntPtr(16 * 1024 * 1024)));
                Check(JsCreateContext(runtime, out context));
                Check(JsSetCurrentContext(context));
                timer = new System.Threading.Timer(delegate { lock (gate) { if (active) JsDisableRuntimeExecution(runtime); } }, null, timeoutMs, Timeout.Infinite);
                Check(JsRunScript(script, UIntPtr.Zero, "cc-switch-usage", out value));
                Check(JsStringToPointer(value, out text, out length));
                int count = checked((int)length.ToUInt64());
                if (count > 2097152) throw new InvalidOperationException("CC Switch result too large");
                return Marshal.PtrToStringUni(text, count);
            }
            finally
            {
                lock (gate) { active = false; }
                if (timer != null) timer.Dispose();
                JsSetCurrentContext(IntPtr.Zero);
                if (runtime != IntPtr.Zero) JsDisposeRuntime(runtime);
            }
        }
        private static void Check(int error)
        {
            // Never expose engine exception text: it may contain interpolated credentials.
            if (error != 0) throw new InvalidOperationException("CC Switch JavaScript failed (0x" + error.ToString("X", CultureInfo.InvariantCulture) + ")");
        }
        [DllImport("Chakra.dll")] private static extern int JsCreateRuntime(int attributes, IntPtr callback, out IntPtr runtime);
        [DllImport("Chakra.dll")] private static extern int JsSetRuntimeMemoryLimit(IntPtr runtime, UIntPtr size);
        [DllImport("Chakra.dll")] private static extern int JsCreateContext(IntPtr runtime, out IntPtr context);
        [DllImport("Chakra.dll")] private static extern int JsSetCurrentContext(IntPtr context);
        [DllImport("Chakra.dll", CharSet = CharSet.Unicode)] private static extern int JsRunScript(string script, UIntPtr cookie, string url, out IntPtr result);
        [DllImport("Chakra.dll")] private static extern int JsStringToPointer(IntPtr value, out IntPtr text, out UIntPtr length);
        [DllImport("Chakra.dll")] private static extern int JsDisableRuntimeExecution(IntPtr runtime);
        [DllImport("Chakra.dll")] private static extern int JsDisposeRuntime(IntPtr runtime);
    }

    internal sealed class CcSwitchClient
    {
        private HttpWebRequest _activeRequest;
        private volatile bool _cancelled;
        internal void Cancel() { _cancelled = true; HttpWebRequest request = _activeRequest; if (request != null) request.Abort(); }
        private string _response, _script;
        private DateTime _received;
        internal UsageSnapshot Query(CcProvider provider)
        {
            if (!provider.Enabled || String.IsNullOrWhiteSpace(provider.Code)) throw new InvalidOperationException("Enable a usage query script in CC Switch");
            string script = BuildScript(provider);
            var request = CcJson.Parse(CcJavaScript.Evaluate("JSON.stringify((" + script + ").request)", 5000));
            string response = Send(request, provider);
            UsageSnapshot snapshot = Extract(provider, script, response, DateTime.UtcNow);
            _script = script; _response = response; _received = DateTime.UtcNow;
            return snapshot;
        }
        internal UsageSnapshot Reevaluate(CcProvider provider)
        {
            if (_response == null) return null;
            // Running the extractor again updates Date.now()-based freshness and countdowns
            // without sending another HTTP request or inventing reset information.
            if (DateTime.UtcNow - _received > TimeSpan.FromMinutes(Math.Max(6, provider.IntervalMinutes * 2)))
                return Failure(provider, "Cached usage expired; refresh required");
            return Extract(provider, _script, _response, _received);
        }
        internal void Clear() { _response = null; _script = null; }
        internal static string BuildScript(CcProvider p)
        {
            // Match CC Switch's literal template substitution, including JS expressions.
            string code = p.Code.Replace("{{apiKey}}", p.ApiKey ?? "").Replace("{{baseUrl}}", p.BaseUrl ?? "");
            if (p.AccessToken != null) code = code.Replace("{{accessToken}}", p.AccessToken);
            if (p.UserId != null) code = code.Replace("{{userId}}", p.UserId);
            return code.Trim().TrimEnd(';');
        }
        internal static UsageSnapshot Extract(CcProvider provider, string script, string response, DateTime received)
        {
            // JSON is parsed inside JS, never inserted as executable response text.
            string result = CcJavaScript.Evaluate("JSON.stringify((" + script + ").extractor(JSON.parse(" + CcJson.Encode(response) + ")))", 5000);
            UsageSnapshot snapshot = ParseRows(provider, CcJson.Parse(result), received);
            AttachPackageMetadata(snapshot, CcJson.Parse(response));
            return snapshot;
        }
        private static void AttachPackageMetadata(UsageSnapshot snapshot, object response)
        {
            // Keep the extractor's values/validity authoritative. The known package
            // schema only supplies structured dates/identity, never parses formatted dates.
            object data = CcJson.Get(response, "data");
            object[] windows = CcJson.Get(data, "windows") as object[];
            if (!Object.Equals(CcJson.Get(response, "success"), true) || windows == null) return;
            string plan = CcJson.Text(data, "plan");
            string prefix = (String.IsNullOrEmpty(plan) ? "套餐" : plan).ToUpperInvariant() + " · ";
            foreach (CcUsageRow row in snapshot.UsageRows)
            {
                if (row.Name == prefix + "站内余额") row.Kind = "balance";
                if (row.Name == prefix + "余额折合周额度") row.Kind = "estimate";
                double seconds = row.Name == prefix + "5小时" ? 18000 : row.Name == prefix + "周" ? 604800 : 0;
                if (seconds == 0) continue;
                row.Kind = "quota"; row.WindowSeconds = seconds;
                foreach (object window in windows)
                    if (CcJson.Number(window, "window_seconds") == seconds) { row.ResetsAt = ParseReset(CcJson.Get(window, "reset_at")); break; }
            }
        }
        private static long? ParseReset(object value)
        {
            DateTimeOffset date;
            if (value is string && DateTimeOffset.TryParse((string)value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date))
                return (long)(date.UtcDateTime - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            if (value is int || value is long) return Convert.ToInt64(value, CultureInfo.InvariantCulture);
            return null;
        }
        internal static UsageSnapshot ParseRows(CcProvider provider, object result, DateTime received)
        {
            var snapshot = new UsageSnapshot { SourceName = provider.Name, IsThirdParty = true, QueriedAt = received };
            object[] items = result as object[] ?? new object[] { result };
            if (items.Length == 0 || items.Length > 32) throw new InvalidOperationException("CC Switch must return 1-32 usage rows");
            foreach (object item in items)
            {
                if (!(item is Dictionary<string, object>)) throw new InvalidOperationException("Invalid CC Switch usage row");
                var row = new CcUsageRow { Name = CcJson.Text(item, "planName"), Extra = CcJson.Text(item, "extra"), Unit = CcJson.Text(item, "unit"), InvalidMessage = CcJson.Text(item, "invalidMessage"), Valid = !Object.Equals(CcJson.Get(item, "isValid"), false), Total = CcJson.Number(item, "total"), Used = CcJson.Number(item, "used"), Remaining = CcJson.Number(item, "remaining") };
                row.Kind = CcJson.Text(item, "kind"); row.WindowSeconds = CcJson.Number(item, "windowSeconds"); row.ResetsAt = ParseReset(CcJson.Get(item, "resetAt"));
                if (!row.Remaining.HasValue && row.Total.HasValue && row.Used.HasValue) row.Remaining = row.Total - row.Used;
                if (!row.Remaining.HasValue && !row.Used.HasValue && !row.Total.HasValue) row.Valid = false;
                snapshot.UsageRows.Add(row);
            }
            return snapshot;
        }
        internal static UsageSnapshot Failure(CcProvider provider, string message)
        {
            return new UsageSnapshot { IsThirdParty = true, SourceName = provider == null ? "CC Switch" : provider.Name, SourceError = message };
        }
        internal static Uri ValidateUrl(string url, CcProvider provider)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || (uri.Scheme != "https" && uri.Scheme != "http") || !String.IsNullOrEmpty(uri.UserInfo)) throw new InvalidOperationException("Invalid CC Switch request URL");
            if (provider.TemplateType != "custom")
            {
                if (uri.Scheme != "https" && !uri.IsLoopback) throw new InvalidOperationException("CC Switch request requires HTTPS");
                Uri origin;
                if (!String.IsNullOrEmpty(provider.BaseUrl) && (!Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out origin) || origin.Scheme != uri.Scheme || origin.Host != uri.Host || origin.Port != uri.Port)) throw new InvalidOperationException("CC Switch request origin differs from baseUrl");
            }
            return uri;
        }
        private string Send(object config, CcProvider provider)
        {
            Uri uri = ValidateUrl(CcJson.Text(config, "url"), provider);
            ConfigureHttps();
            string method = CcJson.Text(config, "method");
            if (!Regex.IsMatch(method, "^[A-Za-z]+$")) throw new InvalidOperationException("Invalid CC Switch HTTP method");
            var request = (HttpWebRequest)WebRequest.Create(uri);
            request.Method = method.ToUpperInvariant();
            request.Timeout = request.ReadWriteTimeout = provider.TimeoutSeconds * 1000;
            request.AllowAutoRedirect = false; // Do not forward credentials to a redirect destination.
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            var headers = CcJson.Get(config, "headers") as Dictionary<string, object>;
            if (headers != null) foreach (var header in headers)
            {
                string value = header.Value as string;
                if (value == null) throw new InvalidOperationException("Invalid CC Switch HTTP header");
                switch (header.Key.ToLowerInvariant())
                {
                    case "user-agent": request.UserAgent = value; break;
                    case "accept": request.Accept = value; break;
                    case "content-type": request.ContentType = value; break;
                    default: request.Headers[header.Key] = value; break;
                }
            }
            try
            {
                _activeRequest = request;
                if (_cancelled) throw new InvalidOperationException("CC Switch query cancelled");
                using (var abort = new System.Threading.Timer(delegate { request.Abort(); }, null, provider.TimeoutSeconds * 1000, Timeout.Infinite))
                {
                    string body = CcJson.Get(config, "body") as string;
                    if (body != null)
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(body); request.ContentLength = bytes.Length;
                        using (Stream stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
                    }
                    using (var response = (HttpWebResponse)request.GetResponse())
                    {
                        if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300) throw new InvalidOperationException("CC Switch HTTP " + (int)response.StatusCode);
                        using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                        {
                            var text = new StringBuilder(); var buffer = new char[4096]; int count;
                            while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                text.Append(buffer, 0, count);
                                if (text.Length > 1048576) throw new InvalidOperationException("CC Switch response exceeds 1 MiB");
                            }
                            return text.ToString();
                        }
                    }
                }
            }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                string error = response == null ? "CC Switch network error: " + ex.Status : "CC Switch HTTP " + (int)response.StatusCode;
                if (response != null) response.Dispose();
                throw new InvalidOperationException(error);
            }
            finally { _activeRequest = null; }
        }
        internal static void ConfigureHttps()
        {
            // csc's legacy .NET Framework target defaults to SSL3/TLS 1.0 in a
            // standalone EXE, even when a PowerShell-hosted test negotiates TLS 1.2.
            // Keep normal certificate validation; do not enable obsolete protocols.
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        }
    }
}
