using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace CodexUsageBar
{
    internal enum DisplayMode
    {
        Independent,
        Attached
    }

    internal enum ConnectionKind
    {
        NoCodex,
        Connecting,
        Connected,
        Failed
    }

    internal enum LanguageMode
    {
        FollowCodex,
        Chinese,
        English
    }

    internal sealed class AppSettings
    {
        public bool Visible = true;
        public DisplayMode Mode = DisplayMode.Attached;
        public LanguageMode Language = LanguageMode.FollowCodex;
        public int IndependentX = Int32.MinValue;
        public int IndependentY = Int32.MinValue;

        internal static AppSettings Load(string path)
        {
            var settings = new AppSettings();
            if (!File.Exists(path)) return settings;
            try
            {
                var serializer = new JavaScriptSerializer();
                var root = serializer.DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
                if (root == null) return settings;
                object value;
                if (root.TryGetValue("visible", out value) && value is bool) settings.Visible = (bool)value;
                if (root.TryGetValue("mode", out value))
                {
                    string mode = Convert.ToString(value, CultureInfo.InvariantCulture);
                    settings.Mode = String.Equals(mode, "independent", StringComparison.OrdinalIgnoreCase)
                        ? DisplayMode.Independent : DisplayMode.Attached;
                }
                if (root.TryGetValue("language", out value))
                {
                    string language = Convert.ToString(value, CultureInfo.InvariantCulture);
                    if (String.Equals(language, "zh", StringComparison.OrdinalIgnoreCase)) settings.Language = LanguageMode.Chinese;
                    else if (String.Equals(language, "en", StringComparison.OrdinalIgnoreCase)) settings.Language = LanguageMode.English;
                }
                int number;
                if (root.TryGetValue("independentX", out value) && Int32.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out number)) settings.IndependentX = number;
                if (root.TryGetValue("independentY", out value) && Int32.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out number)) settings.IndependentY = number;
            }
            catch (Exception ex)
            {
                Log.Write("settings ignored: " + ex.Message);
            }
            return settings;
        }

        internal void Save(string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var serializer = new JavaScriptSerializer();
                var root = new Dictionary<string, object>();
                root["schemaVersion"] = 2;
                root["visible"] = Visible;
                root["mode"] = Mode == DisplayMode.Independent ? "independent" : "attached";
                root["language"] = Language == LanguageMode.Chinese ? "zh" : Language == LanguageMode.English ? "en" : "auto";
                if (IndependentX != Int32.MinValue) root["independentX"] = IndependentX;
                if (IndependentY != Int32.MinValue) root["independentY"] = IndependentY;
                string temporary = path + ".tmp";
                File.WriteAllText(temporary, serializer.Serialize(root), new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    try { File.Replace(temporary, path, null); }
                    catch
                    {
                        File.Copy(temporary, path, true);
                        File.Delete(temporary);
                    }
                }
                else File.Move(temporary, path);
            }
            catch (Exception ex)
            {
                Log.Write("settings save failed: " + ex.Message);
            }
        }
    }

    internal sealed class ThemePalette
    {
        public bool Dark;
        public Color Surface;
        public Color Ink;
        public Color Accent;
        public Color SecondaryInk;
        public Color TertiaryInk;
        public Color HoverSurface;
        public Color SoftSurface;
        public Color Separator;
        public string FontFamily;

        internal static ThemePalette CreateDefault(bool dark)
        {
            Color ink = dark ? Color.FromArgb(254, 254, 254) : Color.FromArgb(3, 3, 3);
            var theme = new ThemePalette();
            theme.Dark = dark;
            theme.Surface = dark ? Color.FromArgb(23, 23, 23) : Color.FromArgb(246, 246, 246);
            theme.Ink = ink;
            theme.Accent = Color.FromArgb(255, 99, 99);
            theme.FontFamily = "Inter";
            theme.RecalculateDerived();
            return theme;
        }

        internal void RecalculateDerived()
        {
            SecondaryInk = WithAlpha(Ink, Dark ? 0.699 : 0.73);
            TertiaryInk = WithAlpha(Ink, Dark ? 0.484 : 0.53);
            HoverSurface = WithAlpha(Ink, Dark ? 0.075 : 0.064);
            SoftSurface = WithAlpha(Ink, Dark ? 0.05 : 0.056);
            Separator = WithAlpha(Ink, Dark ? 0.15 : 0.137);
        }

        private static Color WithAlpha(Color color, double alpha)
        {
            return Color.FromArgb((int)Math.Round(alpha * 255), color.R, color.G, color.B);
        }
    }

    internal sealed class ThemeSet
    {
        public ThemePalette Light = ThemePalette.CreateDefault(false);
        public ThemePalette Dark = ThemePalette.CreateDefault(true);
        public string Appearance = "system";
        public string Language = String.Empty;

        internal ThemePalette Active
        {
            get
            {
                if (String.Equals(Appearance, "light", StringComparison.OrdinalIgnoreCase)) return Light;
                if (String.Equals(Appearance, "dark", StringComparison.OrdinalIgnoreCase)) return Dark;
                return ThemeReader.SystemUsesLightTheme() ? Light : Dark;
            }
        }
    }

    internal static class ThemeReader
    {
        private static readonly Regex SectionPattern = new Regex("^\\s*\\[([^]]+)\\]\\s*$", RegexOptions.Compiled);
        private static readonly Regex ValuePattern = new Regex("^\\s*([A-Za-z0-9_-]+)\\s*=\\s*(.+?)\\s*$", RegexOptions.Compiled);

        internal static ThemeSet Load(string path)
        {
            if (!File.Exists(path)) return new ThemeSet();
            try { return Parse(File.ReadAllText(path, Encoding.UTF8)); }
            catch (Exception ex)
            {
                Log.Write("theme config ignored: " + ex.Message);
                return new ThemeSet();
            }
        }

        internal static ThemeSet Parse(string text)
        {
            var set = new ThemeSet();
            string section = String.Empty;
            string[] lines = (text ?? String.Empty).Replace("\r\n", "\n").Split('\n');
            foreach (string rawLine in lines)
            {
                string line = StripComment(rawLine).Trim();
                if (line.Length == 0) continue;
                Match sectionMatch = SectionPattern.Match(line);
                if (sectionMatch.Success)
                {
                    section = sectionMatch.Groups[1].Value.Trim();
                    continue;
                }
                Match valueMatch = ValuePattern.Match(line);
                if (!valueMatch.Success) continue;
                string key = valueMatch.Groups[1].Value;
                string value = Unquote(valueMatch.Groups[2].Value.Trim());
                bool languageSection = section.Length == 0 || String.Equals(section, "desktop", StringComparison.OrdinalIgnoreCase);
                bool languageKey = String.Equals(key, "language", StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(key, "locale", StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(key, "uiLanguage", StringComparison.OrdinalIgnoreCase);
                if (languageSection && languageKey)
                {
                    set.Language = value;
                    continue;
                }
                if (String.Equals(section, "desktop", StringComparison.OrdinalIgnoreCase) && String.Equals(key, "appearanceTheme", StringComparison.OrdinalIgnoreCase))
                {
                    if (value == "light" || value == "dark" || value == "system") set.Appearance = value;
                    continue;
                }
                ThemePalette theme = null;
                if (section.StartsWith("desktop.appearanceLightChromeTheme", StringComparison.OrdinalIgnoreCase)) theme = set.Light;
                if (section.StartsWith("desktop.appearanceDarkChromeTheme", StringComparison.OrdinalIgnoreCase)) theme = set.Dark;
                if (theme == null) continue;
                Color parsed;
                if (String.Equals(key, "surface", StringComparison.OrdinalIgnoreCase) && TryColor(value, out parsed)) theme.Surface = parsed;
                else if (String.Equals(key, "ink", StringComparison.OrdinalIgnoreCase) && TryColor(value, out parsed))
                {
                    theme.Ink = parsed;
                    theme.RecalculateDerived();
                }
                else if (String.Equals(key, "accent", StringComparison.OrdinalIgnoreCase) && TryColor(value, out parsed)) theme.Accent = parsed;
                else if (section.EndsWith(".fonts", StringComparison.OrdinalIgnoreCase) && String.Equals(key, "ui", StringComparison.OrdinalIgnoreCase) && value.Length > 0) theme.FontFamily = value;
            }
            return set;
        }

        internal static bool SystemUsesLightTheme()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object value = key == null ? null : key.GetValue("AppsUseLightTheme");
                    if (value != null) return Convert.ToInt32(value, CultureInfo.InvariantCulture) != 0;
                }
            }
            catch { }
            return false;
        }

        private static bool TryColor(string value, out Color color)
        {
            color = Color.Empty;
            try
            {
                if (!Regex.IsMatch(value ?? String.Empty, "^#[0-9a-fA-F]{6}$")) return false;
                color = ColorTranslator.FromHtml(value);
                return true;
            }
            catch { return false; }
        }

        private static string StripComment(string value)
        {
            bool quoted = false;
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] == '"' && (i == 0 || value[i - 1] != '\\')) quoted = !quoted;
                if (value[i] == '#' && !quoted) return value.Substring(0, i);
            }
            return value;
        }

        private static string Unquote(string value)
        {
            if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
                return value.Substring(1, value.Length - 2).Replace("\\\"", "\"").Replace("\\\\", "\\");
            if (value.Length >= 2 && value[0] == '\'' && value[value.Length - 1] == '\'')
                return value.Substring(1, value.Length - 2);
            return value;
        }
    }

    internal sealed class LimitWindow
    {
        public string Key;
        public double UsedPercent;
        public double Remaining;
        public long? ResetsAt;
        public double? WindowDurationMins;
    }

    internal sealed class UsageSnapshot
    {
        public readonly List<LimitWindow> Windows = new List<LimitWindow>();
        public long? YesterdayTokens;
        public long? LifetimeTokens;

        internal List<LimitWindow> DisplayWindows
        {
            get
            {
                LimitWindow fiveHour = null;
                LimitWindow weekly = null;
                foreach (LimitWindow window in Windows)
                {
                    if (MatchesDuration(window, 300)) fiveHour = MoreRestrictive(fiveHour, window);
                    else if (MatchesDuration(window, 10080)) weekly = MoreRestrictive(weekly, window);
                    else if (!window.WindowDurationMins.HasValue)
                    {
                        string key = window.Key ?? String.Empty;
                        if (key.IndexOf("primary", StringComparison.OrdinalIgnoreCase) >= 0)
                            fiveHour = MoreRestrictive(fiveHour, window);
                        else if (key.IndexOf("secondary", StringComparison.OrdinalIgnoreCase) >= 0)
                            weekly = MoreRestrictive(weekly, window);
                    }
                }
                var result = new List<LimitWindow>();
                if (fiveHour != null) result.Add(fiveHour);
                if (weekly != null) result.Add(weekly);
                return result;
            }
        }

        internal LimitWindow Tightest
        {
            get
            {
                LimitWindow result = null;
                foreach (LimitWindow window in DisplayWindows)
                    if (result == null || window.Remaining < result.Remaining) result = window;
                return result;
            }
        }

        private static bool MatchesDuration(LimitWindow window, double expectedMinutes)
        {
            return window != null && window.WindowDurationMins.HasValue &&
                Math.Abs(window.WindowDurationMins.Value - expectedMinutes) <= Math.Max(5, expectedMinutes * 0.02);
        }

        private static LimitWindow MoreRestrictive(LimitWindow current, LimitWindow candidate)
        {
            return current == null || candidate.Remaining < current.Remaining ? candidate : current;
        }
    }

    internal static class AppDataParser
    {
        internal static List<LimitWindow> ParseRateLimits(object response)
        {
            var result = new List<LimitWindow>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            object root = response;
            var map = response as Dictionary<string, object>;
            object selected;
            if (map != null && map.TryGetValue("rateLimitsByLimitId", out selected) && selected != null) root = selected;
            else if (map != null && map.TryGetValue("rateLimits", out selected) && selected != null) root = selected;
            Visit(root, "rateLimits", result, seen, 0);
            return result;
        }

        private static void Visit(object node, string path, List<LimitWindow> result, HashSet<string> seen, int depth)
        {
            if (node == null || depth > 12) return;
            var map = node as Dictionary<string, object>;
            if (map != null)
            {
                object usedValue;
                double used;
                if (map.TryGetValue("usedPercent", out usedValue) && TryDouble(usedValue, out used))
                {
                    used = Math.Max(0, Math.Min(100, used));
                    object resetsValue;
                    object durationValue;
                    long resets;
                    double duration;
                    long? resetsAt = map.TryGetValue("resetsAt", out resetsValue) && TryLong(resetsValue, out resets) ? (long?)resets : null;
                    double? durationMins = map.TryGetValue("windowDurationMins", out durationValue) && TryDouble(durationValue, out duration) ? (double?)duration : null;
                    string identity = used.ToString("0.###", CultureInfo.InvariantCulture) + "|" +
                        (resetsAt.HasValue ? resetsAt.Value.ToString(CultureInfo.InvariantCulture) : "") + "|" +
                        (durationMins.HasValue ? durationMins.Value.ToString("0.###", CultureInfo.InvariantCulture) : "");
                    if (seen.Add(identity))
                    {
                        var item = new LimitWindow();
                        item.Key = path;
                        item.UsedPercent = Math.Round(used, 3);
                        item.Remaining = Math.Round(100 - used, 3);
                        item.ResetsAt = resetsAt;
                        item.WindowDurationMins = durationMins;
                        result.Add(item);
                    }
                    return;
                }
                foreach (KeyValuePair<string, object> pair in map)
                {
                    if (String.Equals(pair.Key, "rateLimitResetCredits", StringComparison.OrdinalIgnoreCase)) continue;
                    Visit(pair.Value, path + "." + pair.Key, result, seen, depth + 1);
                }
                return;
            }
            var list = node as object[];
            if (list != null)
            {
                for (int i = 0; i < list.Length; i++) Visit(list[i], path + "[" + i + "]", result, seen, depth + 1);
                return;
            }
            var arrayList = node as ArrayList;
            if (arrayList != null)
                for (int i = 0; i < arrayList.Count; i++) Visit(arrayList[i], path + "[" + i + "]", result, seen, depth + 1);
        }

        internal static void ParseUsage(object response, UsageSnapshot snapshot)
        {
            var root = response as Dictionary<string, object>;
            if (root == null) return;
            object summaryValue;
            var summary = root.TryGetValue("summary", out summaryValue) ? summaryValue as Dictionary<string, object> : null;
            if (summary != null)
            {
                object lifetime;
                long number;
                if (summary.TryGetValue("lifetimeTokens", out lifetime) && TryLong(lifetime, out number) && number >= 0) snapshot.LifetimeTokens = number;
            }
            object bucketsValue;
            var buckets = root.TryGetValue("dailyUsageBuckets", out bucketsValue) ? bucketsValue as object[] : null;
            if (buckets == null)
            {
                var array = bucketsValue as ArrayList;
                if (array != null) buckets = array.ToArray();
            }
            if (buckets == null) return;
            string yesterday = DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            foreach (object value in buckets)
            {
                var bucket = value as Dictionary<string, object>;
                if (bucket == null) continue;
                object dateValue;
                object tokenValue;
                long tokens;
                if (bucket.TryGetValue("startDate", out dateValue) && Convert.ToString(dateValue, CultureInfo.InvariantCulture) == yesterday &&
                    bucket.TryGetValue("tokens", out tokenValue) && TryLong(tokenValue, out tokens) && tokens >= 0)
                {
                    snapshot.YesterdayTokens = tokens;
                    break;
                }
            }
        }

        private static bool TryDouble(object value, out double result)
        {
            return Double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out result);
        }

        private static bool TryLong(object value, out long result)
        {
            double number;
            if (!TryDouble(value, out number) || Double.IsNaN(number) || Double.IsInfinity(number))
            {
                result = 0;
                return false;
            }
            result = (long)Math.Round(number, MidpointRounding.AwayFromZero);
            return true;
        }
    }

    internal static class Formatters
    {
        internal static string CompactTokens(long? value)
        {
            if (!value.HasValue || value.Value < 0) return "—";
            string[] units = { "", "K", "M", "B" };
            double number = value.Value;
            int index = 0;
            while (number >= 1000 && index < units.Length - 1)
            {
                number /= 1000;
                index++;
            }
            double rounded = index == 0 ? Math.Round(number, 0, MidpointRounding.AwayFromZero) : Math.Round(number, 1, MidpointRounding.AwayFromZero);
            if (rounded >= 1000 && index < units.Length - 1)
            {
                rounded /= 1000;
                index++;
            }
            return rounded.ToString(index == 0 || rounded == Math.Truncate(rounded) ? "0" : "0.#", CultureInfo.InvariantCulture) + units[index];
        }

        internal static string ResetTime(long? unixSeconds, bool chinese, bool compact)
        {
            if (!unixSeconds.HasValue) return chinese ? "重置时间未知" : "Reset unknown";
            DateTime local;
            try
            {
                local = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(unixSeconds.Value).ToLocalTime();
            }
            catch { return chinese ? "重置时间未知" : "Reset unknown"; }
            DateTime today = DateTime.Today;
            if (compact)
            {
                if (local.Date == today) return local.ToString("HH:mm", CultureInfo.InvariantCulture);
                if (local.Date == today.AddDays(1)) return chinese ? "明天 " + local.ToString("HH:mm", CultureInfo.InvariantCulture) : "Tomorrow " + local.ToString("HH:mm", CultureInfo.InvariantCulture);
                return local.Year == today.Year ? local.ToString(chinese ? "M月d日 HH:mm" : "M/d HH:mm", CultureInfo.InvariantCulture) : local.ToString(chinese ? "yyyy年M月d日" : "yyyy/M/d", CultureInfo.InvariantCulture);
            }
            if (local.Date == today) return (chinese ? "今天 " : "Today ") + local.ToString("HH:mm", CultureInfo.InvariantCulture) + (chinese ? " 重置" : " reset");
            if (local.Date == today.AddDays(1)) return (chinese ? "明天 " : "Tomorrow ") + local.ToString("HH:mm", CultureInfo.InvariantCulture) + (chinese ? " 重置" : " reset");
            if (local.Year == today.Year)
                return local.ToString(chinese ? "M月d日 HH:mm '重置'" : "M/d HH:mm 'reset'", CultureInfo.InvariantCulture);
            return local.ToString(chinese ? "yyyy年M月d日 HH:mm '重置'" : "yyyy/M/d HH:mm 'reset'", CultureInfo.InvariantCulture);
        }
    }

    internal sealed class Texts
    {
        public readonly bool Chinese;
        internal Texts() : this(IsChineseFamily(CultureInfo.CurrentUICulture.Name)) { }
        internal Texts(bool chinese) { Chinese = chinese; }

        internal static Texts Resolve(LanguageMode mode, string codexLanguage)
        {
            if (mode == LanguageMode.Chinese) return new Texts(true);
            if (mode == LanguageMode.English) return new Texts(false);
            if (!String.IsNullOrWhiteSpace(codexLanguage)) return new Texts(IsChineseFamily(codexLanguage));
            return new Texts(IsChineseFamily(CultureInfo.CurrentUICulture.Name));
        }

        internal static bool IsChineseFamily(string language)
        {
            string value = (language ?? String.Empty).Trim().Replace('_', '-');
            return value.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ||
                value.IndexOf("Chinese", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("中文", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        internal string Connection(ConnectionKind kind)
        {
            if (Chinese)
            {
                if (kind == ConnectionKind.NoCodex) return "Codex 连接：未检测到 Codex";
                if (kind == ConnectionKind.Connecting) return "Codex 连接：正在连接";
                if (kind == ConnectionKind.Connected) return "Codex 连接：成功";
                return "Codex 连接：失败";
            }
            if (kind == ConnectionKind.NoCodex) return "Codex: app not detected";
            if (kind == ConnectionKind.Connecting) return "Codex: connecting";
            if (kind == ConnectionKind.Connected) return "Codex: connected";
            return "Codex: connection failed";
        }
        internal string ShowWindow { get { return Chinese ? "显示悬浮窗" : "Show floating window"; } }
        internal string DisplayMode { get { return Chinese ? "展示方式" : "Display mode"; } }
        internal string Language { get { return Chinese ? "语言" : "Language"; } }
        internal string FollowCodex { get { return Chinese ? "跟随 Codex" : "Follow Codex"; } }
        internal string ChineseLanguage { get { return Chinese ? "中文" : "Chinese"; } }
        internal string EnglishLanguage { get { return "English"; } }
        internal string Independent { get { return Chinese ? "独立展示" : "Independent"; } }
        internal string Attached { get { return Chinese ? "吸附 Codex 窗口" : "Attach to Codex"; } }
        internal string Refresh { get { return Chinese ? "立即刷新" : "Refresh now"; } }
        internal string Exit { get { return Chinese ? "退出" : "Exit"; } }
        internal string Remaining { get { return Chinese ? "剩余" : "remaining"; } }
        internal string FiveHour { get { return Chinese ? "5 小时" : "5 hours"; } }
        internal string Weekly { get { return Chinese ? "周" : "Weekly"; } }
        internal string Yesterday { get { return Chinese ? "昨日" : "Yesterday"; } }
        internal string Lifetime { get { return Chinese ? "累计" : "Total"; } }
        internal string NoData { get { return Chinese ? "接口已连接，但暂无额度数据" : "Connected, but no rate-limit data"; } }
        internal string AuthFailed { get { return Chinese ? "认证失败" : "authentication failed"; } }
        internal string CliMissing { get { return Chinese ? "未找到可用 Codex CLI" : "Codex CLI not found"; } }
        internal string InterfaceUnavailable { get { return Chinese ? "Codex 接口不可用" : "Codex interface unavailable"; } }
    }
}
