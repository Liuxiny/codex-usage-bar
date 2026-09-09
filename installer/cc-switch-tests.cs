using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace CodexUsageBar
{
    internal static class CcSwitchTests
    {
        private static void Assert(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("CC Switch test: " + message); }
        internal static void Run()
        {
            SecurityProtocolType initialProtocol = ServicePointManager.SecurityProtocol;
            var initialValidation = ServicePointManager.ServerCertificateValidationCallback;
            try
            {
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)240; // Legacy EXE default: SSL3 | TLS1.0.
                CcSwitchClient.ConfigureHttps();
                Assert(ServicePointManager.SecurityProtocol == SecurityProtocolType.Tls12, "standalone legacy TLS defaults replaced");
                Assert(ServicePointManager.ServerCertificateValidationCallback == initialValidation, "certificate validation unchanged");
            }
            finally { ServicePointManager.SecurityProtocol = initialProtocol; }
            Assert(CcJavaScript.Evaluate("JSON.stringify([1,2,3].find(function(x){return x===2;}))", 1000) == "2", "Array.find support");
            Assert(CcJavaScript.Evaluate("JSON.stringify([typeof require,typeof fetch,typeof ActiveXObject,typeof process])", 1000) == "[\"undefined\",\"undefined\",\"undefined\",\"undefined\"]", "no host capabilities");
            var watch = Stopwatch.StartNew(); bool interrupted = false;
            try { CcJavaScript.Evaluate("while(true){}", 100); } catch (InvalidOperationException) { interrupted = true; }
            Assert(interrupted && watch.ElapsedMilliseconds < 4000, "infinite loop interrupted");
            Assert(CcJavaScript.Evaluate("JSON.stringify(3)", 1000) == "3", "runtime recovers after interrupt");
            string script;
            using (var stream = typeof(CcSwitchTests).Assembly.GetManifestResourceStream("package-quota.js"))
            using (var reader = new StreamReader(stream)) script = reader.ReadToEnd();
            var p = new CcProvider { Id = "test", Name = "Test provider", Code = script, BaseUrl = "https://quota.example", ApiKey = "test-key", AccessToken = "test-token", UserId = "42", TemplateType = "custom", Enabled = true };
            string code = CcSwitchClient.BuildScript(p);
            object request = CcJson.Parse(CcJavaScript.Evaluate("JSON.stringify((" + code + ").request)", 1000));
            Assert(CcJson.Text(request, "url") == "https://quota.example/api/user/self/package-quota", "URL substitution");
            Assert(CcJson.Text(CcJson.Get(request, "headers"), "Authorization") == "Bearer test-token", "token substitution");
            DateTime now = DateTime.UtcNow;
            string fixture = Fixture(now, now.AddDays(7), "pro", 80, true);
            UsageSnapshot data = CcSwitchClient.Extract(p, code, fixture, now);
            Assert(data.IsThirdParty && data.Windows.Count == 0 && data.YesterdayTokens == null, "no official usage mixing");
            Assert(data.UsageRows.Count == 3 && data.UsageRows[1].Remaining == 80 && data.UsageRows[1].Total == 100, "PRO weekly only");
            Assert(data.ThirdPartyQuotas.Count == 1 && data.ThirdPartyQuotas[0].WindowSeconds == 604800 && data.ThirdPartyQuotas[0].ResetsAt.HasValue, "package quota has structured reset metadata");
            Assert(data.ThirdPartyBalance.Remaining == 49.32 && data.ThirdPartyEstimate.Remaining.HasValue, "balance and estimate in separate presentation slots");
            Assert(data.UsageRows[0].Remaining == 49.32 && data.UsageRows[2].Remaining == 246.6, "wallet and estimate separate, not clamped to 100");
            data = CcSwitchClient.Extract(p, code, Fixture(now, now.AddDays(7), "plus", 0, false), now);
            Assert(data.UsageRows.Count == 4 && data.UsageRows[2].Valid && data.UsageRows[2].Remaining == 0 && !data.UsageRows[3].Valid, "zero quota valid, missing estimation independent");
            Assert(data.ThirdPartyQuotas.Count == 2 && data.ThirdPartyQuotas[0].WindowSeconds == 18000 && data.ThirdPartyEstimate == null, "two quota columns and unavailable estimate hidden");
            data = CcSwitchClient.Extract(p, code, Fixture(now.AddMinutes(-7), now.AddDays(7), "pro", 80, true), now);
            Assert(data.UsageRows[0].Valid && !data.UsageRows[1].Valid && !data.UsageRows[2].Valid, "stale quota preserves wallet only");
            Assert(data.ThirdPartyQuotas.Count == 0 && data.ThirdPartyEstimate == null, "stale quotas never become rings");
            data = CcSwitchClient.Extract(p, code, Fixture(now, now.AddSeconds(-1), "pro", 80, true), now);
            Assert(!data.UsageRows[1].Valid && data.UsageRows[1].InvalidMessage == "已到重置时间", "past reset unavailable");
            data = CcSwitchClient.Extract(p, code, "{\"success\":false,\"message\":\"查询失败\"}", now);
            Assert(data.UsageRows.Count == 1 && !data.UsageRows[0].Valid, "single invalid row");
            data = CcSwitchClient.ParseRows(p, CcJson.Parse("{\"total\":10,\"used\":3,\"unit\":\"USD\"}"), now);
            Assert(data.UsageRows[0].Remaining == 7, "derive remaining");
            string toml = "model_provider = \"active\"\n[model_providers.inactive]\nbase_url = \"https://wrong.example\"\n[model_providers.active]\nbase_url = \"https://right.example\"\n";
            Assert(CcSwitchReader.ConfigValue(toml, "base_url") == "https://right.example", "active TOML section only");
            Assert(CcSwitchReader.ConfigValue(toml.Replace("\"active\"", "\"missing\""), "base_url") == "", "never unrelated credentials");
            p.TemplateType = "general"; bool rejected = false;
            try { CcSwitchClient.ValidateUrl("https://wrong.example", p); } catch (InvalidOperationException) { rejected = true; }
            Assert(rejected, "noncustom same-origin constraint");
            p.TemplateType = "custom";
            Assert(CcSwitchClient.ValidateUrl("http://localhost:1234/quota", p).IsLoopback, "custom loopback queries");
            DatabaseAndSettings();
            HttpRoundTrip();
            using (var form = new OverlayForm())
            {
                form.ApplySnapshot(CcSwitchClient.Extract(p, code, fixture, now)); form.SetExpanded(true);
                Assert(form.Width >= 300 && form.Height > 170, "multiple row layout");
                int estimateHeight = form.Height;
                form.ApplySnapshot(CcSwitchClient.Extract(p, code, Fixture(now, now.AddDays(7), "pro", 80, false), now));
                Assert(form.Height < estimateHeight, "estimate section and separator removed when unavailable");
                UsageSnapshot zeroEstimate = CcSwitchClient.Extract(p, code, fixture, now);
                zeroEstimate.ThirdPartyEstimate.Remaining = 0;
                form.ApplySnapshot(zeroEstimate);
                Assert(form.Height == estimateHeight && zeroEstimate.ThirdPartyEstimate.Remaining == 0, "zero estimate remains visible");
                using (var bitmap = new Bitmap(form.Width, form.Height)) form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                form.ApplySnapshot(CcSwitchClient.Failure(p, "CC Switch HTTP 401"));
                Assert(form.Width >= 300, "failure remains displayable");
            }
        }
        internal static string Fixture(DateTime updated, DateTime reset, string plan, double remaining, bool estimate)
        {
            return CcJson.Encode(new { success = true, data = new { plan = plan, status = "available", updated_at = updated.ToString("o"), site_balance = new { status = "available", remaining = 49.32 }, windows = new object[] { new { name = "weekly", window_seconds = 604800, remaining_percent = remaining, reset_at = reset.ToString("o") }, new { name = "5h", window_seconds = 18000, remaining_percent = 70, reset_at = reset.ToString("o") } }, estimation = new { windows = estimate ? new object[] { new { name = "weekly", status = "ready", usd_per_percentage_point = 0.2 } } : new object[0] } } });
        }
        private static void DatabaseAndSettings()
        {
            string directory = Path.Combine(Path.GetTempPath(), "codexbar-cc-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                IntPtr db;
                Assert(sqlite3_open16(Path.Combine(directory, "cc-switch.db"), out db) == 0, "fixture database opened");
                try
                {
                    string sql = "CREATE TABLE providers(id TEXT,name TEXT,category TEXT,meta TEXT,settings_config TEXT,app_type TEXT,is_current INTEGER);" +
                        "INSERT INTO providers VALUES('official','Official','official','{}','{}','codex',1);" +
                        "INSERT INTO providers VALUES('custom','Custom',NULL,'{\"usage_script\":{\"enabled\":true,\"baseUrl\":\"https://example.com/\",\"code\":\"({})\",\"autoQueryInterval\":0}}','{}','codex',0);";
                    Assert(sqlite3_exec(db, Encoding.UTF8.GetBytes(sql + "\0"), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) == 0, "fixture database seeded");
                }
                finally { sqlite3_close(db); }
                File.WriteAllText(Path.Combine(directory, "settings.json"), "{\"currentProviderCodex\":\"custom\"}");
                CcProvider current = CcSwitchReader.ReadCurrent(directory);
                Assert(CcSwitchReader.RequireDataDirectory(Path.Combine(directory, "cc-switch.db")) == directory, "database file accepted as data directory");
                Assert(CcSwitchReader.NormalizeDirectory("\"" + directory + "\"") == directory, "quoted directory accepted");
                string beforeOverride = Environment.GetEnvironmentVariable("CODEXBAR_CC_SWITCH_HOME");
                try
                {
                    Environment.SetEnvironmentVariable("CODEXBAR_CC_SWITCH_HOME", directory);
                    Assert(CcSwitchReader.ConfigDirectory() == directory, "automatic directory discovery");
                }
                finally { Environment.SetEnvironmentVariable("CODEXBAR_CC_SWITCH_HOME", beforeOverride); }
                bool wrongDirectory = false;
                try { new UsageSourceSettings { Mode = "auto", DirectoryOverride = Path.Combine(directory, "installation-folder") }.Resolve(); }
                catch (InvalidOperationException ex) { wrongDirectory = UsageSourceSettings.SafeError(ex, true).Contains("cc-switch.db"); }
                Assert(wrongDirectory, "explicit wrong directory explains database requirement, never official fallback");
                Assert(current.Id == "custom" && current.Enabled && current.IntervalMinutes == 0 && current.BaseUrl == "https://example.com", "settings selection wins, interval and URL preserved");
                File.WriteAllText(Path.Combine(directory, "settings.json"), "{\"currentProviderCodex\":\"removed\"}");
                Assert(CcSwitchReader.ReadCurrent(directory).Official, "missing local selection falls back to database");
                string path = Path.Combine(directory, "usage-source.json");
                var settings = new UsageSourceSettings { Mode = "custom" };
                settings.Custom.Code = "({request:{},extractor:function(){}})"; settings.Custom.AccessToken = "do-not-store-plaintext-token";
                settings.Save(path);
                Assert(!File.ReadAllText(path).Contains("do-not-store-plaintext-token"), "DPAPI at rest");
                var loaded = UsageSourceSettings.Load(path);
                Assert(loaded.Resolve().AccessToken == "do-not-store-plaintext-token" && loaded.Mode == "custom", "independent configuration roundtrip without CC Switch");
                loaded.Custom.AccessToken = "changed"; loaded.Save(path);
                Assert(UsageSourceSettings.Load(path).Custom.AccessToken == "changed", "atomic overwrite");
            }
            finally { Directory.Delete(directory, true); }
        }
        private static void HttpRoundTrip()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            string received = null; Exception serverError = null;
            var thread = new Thread(delegate()
            {
                try
                {
                    using (TcpClient client = listener.AcceptTcpClient())
                    using (NetworkStream stream = client.GetStream())
                    {
                        stream.ReadTimeout = 3000;
                        var header = new StringBuilder(); int b;
                        while ((b = stream.ReadByte()) >= 0)
                        {
                            header.Append((char)b);
                            if (header.ToString().EndsWith("\r\n\r\n")) break;
                            if (header.Length > 8192) throw new InvalidOperationException("test request too large");
                        }
                        received = header.ToString();
                        const string json = "{\"balance\":49.32}";
                        byte[] bytes = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + Encoding.UTF8.GetByteCount(json) + "\r\nConnection: close\r\n\r\n" + json);
                        stream.Write(bytes, 0, bytes.Length);
                    }
                }
                catch (Exception ex) { serverError = ex; }
            });
            thread.IsBackground = true; thread.Start();
            try
            {
                var p = new CcProvider { Name = "Local test", Enabled = true, BaseUrl = "http://127.0.0.1:" + port, AccessToken = "fixture-token", TemplateType = "custom", TimeoutSeconds = 3, Code = "({request:{url:'{{baseUrl}}/quota',method:'GET',headers:{Authorization:'Bearer {{accessToken}}','User-Agent':'cc-switch/1.0'}},extractor:function(r){return {planName:'Wallet',remaining:r.balance,unit:'USD'};}})" };
                var client = new CcSwitchClient();
                UsageSnapshot result = client.Query(p);
                Assert(thread.Join(3500) && serverError == null, "HTTP fixture completed");
                Assert(received.StartsWith("GET /quota HTTP/") && received.Contains("Bearer fixture-token") && received.Contains("cc-switch/1.0"), "actual URL, token and User-Agent sent");
                Assert(result.UsageRows[0].Remaining == 49.32, "actual HTTP response extracted");
                listener.Stop();
                Assert(client.Reevaluate(p).UsageRows[0].Remaining == 49.32, "cached reevaluation needs no HTTP server");
                client.Clear(); Assert(client.Reevaluate(p) == null, "selection change clears cache");
            }
            finally { listener.Stop(); thread.Join(3500); }
        }
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] private static extern int sqlite3_open16(string path, out IntPtr db);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr callback, IntPtr state, IntPtr error);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close(IntPtr db);
    }
}
