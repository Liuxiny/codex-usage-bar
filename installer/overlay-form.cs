using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace CodexUsageBar
{
    internal sealed class OverlayForm : Form
    {
        internal const int ToolbarHeight = 35;
        private int CollapsedHeight { get { return ScalePixels(ToolbarHeight - 2); } }
        private int _dpi = 96;
        internal int ScaledToolbarHeight { get { return ScalePixels(ToolbarHeight); } }
        internal int ScalePixels(int pixels) { return (int)Math.Round(pixels * _dpi / 96.0); }
        private float RingStroke { get { return RingStrokeWidth * _dpi / 96f; } }

        internal void ApplyDpi(int dpi)
        {
            if (dpi < 96 || dpi > 768 || dpi == _dpi) return;
            _dpi = dpi;
            RebuildFonts();
            SetExpanded(_expanded);
            UpdateRegion();
        }

        internal void FollowWindowDpi(IntPtr window)
        {
            try { ApplyDpi((int)NativeMethods.GetDpiForWindow(window)); }
            catch (EntryPointNotFoundException) { }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            FollowWindowDpi(Handle);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x02E0) // WM_DPICHANGED: all custom geometry is scaled exactly once.
            {
                NativeMethods.RECT suggested = (NativeMethods.RECT)System.Runtime.InteropServices.Marshal.PtrToStructure(m.LParam, typeof(NativeMethods.RECT));
                ApplyDpi((int)(m.WParam.ToInt64() & 0xffff));
                SetProgrammaticLocation(suggested.Left, suggested.Top);
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }
        private const float MenuFontSizePixels = 16f;
        private const float MinimumUiFontSizePixels = 11f;
        private const float MaximumUiFontSizePixels = 16f;
        internal const int RingOuterDiameterPixels = 18;
        internal const float RingStrokeWidth = 4f;
        private UsageSnapshot _snapshot = new UsageSnapshot();
        private ThemePalette _theme = ThemePalette.CreateDefault(true);
        private Texts _texts = new Texts();
        private DisplayMode _mode = DisplayMode.Attached;
        private bool _expanded;
        private bool _programmaticMove;
        private bool _nativeDrag;
        private IntPtr _nativeOwner = IntPtr.Zero;
        private Font _smallFont;
        private Font _smallBoldFont;
        private Font _boldFont;
        private Font _menuFont;
        private PrivateFontCollection _normalPrivateFonts;
        private PrivateFontCollection _emphasisPrivateFonts;
        private static readonly object FontCacheGate = new object();
        private static List<UserFontFace> _userFontFaces;

        internal event Action OverlaySizeChanged;
        internal event Action<int, int> IndependentPositionChanged;

        internal OverlayForm()
        {
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            BackColor = Color.FromArgb(23, 23, 23);
            ClientSize = new Size(300, CollapsedHeight);
            Padding = Padding.Empty;
            AccessibleName = "Codex Usage Bar";
            AccessibleDescription = "Codex rate-limit usage";

            MouseDown += OnWindowMouseDown;
            MouseLeave += delegate { SetExpanded(false); };
            RebuildFonts();
            UpdateRegion();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= 0x00000080;
                parameters.ExStyle |= 0x08000000;
                parameters.ClassStyle |= 0x00020000;
                return parameters;
            }
        }

        internal void ApplyTheme(ThemePalette theme)
        {
            _theme = theme ?? ThemePalette.CreateDefault(true);
            BackColor = _theme.Surface;
            RebuildFonts();
            SetExpanded(_expanded);
            Invalidate();
        }

        internal void ApplyTexts(Texts texts)
        {
            _texts = texts ?? new Texts();
            SetExpanded(_expanded);
            Invalidate();
        }

        internal void ApplySnapshot(UsageSnapshot snapshot)
        {
            _snapshot = snapshot ?? new UsageSnapshot();
            SetExpanded(_expanded);
            LimitWindow tightest = _snapshot.Tightest;
            AccessibleDescription = tightest == null ? "Codex rate-limit usage" :
                "Codex " + _texts.Remaining + " " + Math.Round(tightest.Remaining).ToString(CultureInfo.InvariantCulture) + "%";
            Invalidate();
        }

        internal void SetMode(DisplayMode mode)
        {
            _mode = mode;
            if (mode == DisplayMode.Independent) ClearNativeOwner();
            TopMost = mode == DisplayMode.Independent;
        }

        internal void SetProgrammaticLocation(int x, int y)
        {
            _programmaticMove = true;
            try { Location = new Point(x, y); }
            finally { _programmaticMove = false; }
        }

        internal void BringAboveCodex(IntPtr codexWindow, bool promote)
        {
            if (!IsHandleCreated || codexWindow == IntPtr.Zero) return;
            IntPtr currentOwner = NativeMethods.GetWindow(Handle, NativeMethods.GW_OWNER);
            if (_nativeOwner != codexWindow || currentOwner != codexWindow)
            {
                NativeMethods.SetWindowOwner(Handle, codexWindow);
                _nativeOwner = NativeMethods.GetWindow(Handle, NativeMethods.GW_OWNER) == codexWindow
                    ? codexWindow : IntPtr.Zero;
            }
            if (promote)
                NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOP, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }

        internal void SetExpanded(bool expanded)
        {
            _expanded = expanded;
            Size next = new Size(DesiredWidth(), expanded ? DesiredExpandedHeight() : CollapsedHeight);
            if (ClientSize != next) ClientSize = next;
            Invalidate();
        }

        private int DesiredExpandedHeight()
        {
            if (_snapshot.IsThirdParty)
            {
                int line = ExpandedLineHeight();
                int height = _snapshot.ThirdPartyQuotas.Count > 0 ? CollapsedHeight + line * 2 + ScalePixels(20) : line * 2 + ScalePixels(20);
                if (_snapshot.ThirdPartyBalance != null || _snapshot.ThirdPartyEstimate != null) height += line + ScalePixels(18);
                return height + ScalePixels(8);
            }
            int lineHeight = ExpandedLineHeight();
            int detailTop = CollapsedHeight + ScalePixels(8);
            int resetTop = detailTop + lineHeight + ScalePixels(4);
            int footerTop = resetTop + lineHeight + ScalePixels(8);
            int footerTextTop = footerTop + ScalePixels(7);
            return footerTextTop + lineHeight + ScalePixels(10) + ScalePixels(7);
        }

        private int ExpandedLineHeight()
        {
            return Math.Max(ScalePixels(18), TextRenderer.MeasureText("Ag中", _smallBoldFont,
                new Size(Int32.MaxValue, Int32.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Height);
        }

        private int DesiredWidth()
        {
            if (_snapshot.IsThirdParty)
            {
                List<CcUsageRow> quotas = _snapshot.ThirdPartyQuotas;
                int width = 0;
                foreach (CcUsageRow row in quotas) width += QuotaColumnWidth(row);
                CcUsageRow balance = _snapshot.ThirdPartyBalance;
                if (balance != null) width += StyledWidth(BalanceText(balance), _menuFont, _boldFont) + ScalePixels(18);
                if (quotas.Count == 0) width = Math.Max(width, MeasureTextWidth(ThirdPartySummary(), _menuFont) + ScalePixels(24));
                CcUsageRow estimate = _snapshot.ThirdPartyEstimate;
                if (estimate != null) width = Math.Max(width, StyledWidth(EstimateText(estimate), _smallFont, _smallBoldFont) +
                    (balance == null ? 0 : StyledWidth(BalanceText(balance), _smallFont, _smallBoldFont) + ScalePixels(20)) + ScalePixels(20));
                return Math.Min(Math.Max(ScalePixels(300), width), Screen.FromControl(this).WorkingArea.Width - ScalePixels(20));
            }
            List<LimitWindow> windows = _snapshot.DisplayWindows;
            if (windows.Count == 0) return ScalePixels(300);
            int total = 0;
            foreach (int width in NaturalColumnWidths(windows)) total += width;
            return Math.Max(total, TokenFooterWidth() + ScalePixels(20));
        }

        private int[] NaturalColumnWidths(List<LimitWindow> windows)
        {
            var widths = new int[windows.Count];
            for (int i = 0; i < windows.Count; i++)
            {
                LimitWindow window = windows[i];
                string percent = Math.Round(window.Remaining, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture) + "%";
                string compactReset = Formatters.ResetTime(window.ResetsAt, _texts.Chinese, true);
                string fullReset = Formatters.ResetTime(window.ResetsAt, _texts.Chinese, false);
                string label = IsFiveHour(window) ? _texts.FiveHour : _texts.Weekly;
                int collapsed = ScalePixels(10) + RingOuterDiameter() + ScalePixels(6) + MeasureTextWidth(percent, _boldFont) + ScalePixels(7) + MeasureTextWidth(compactReset, _boldFont) + ScalePixels(10);
                int detail = ScalePixels(10) + MeasureTextWidth(label, _smallBoldFont) + ScalePixels(8) + MeasureTextWidth(percent, _smallBoldFont) + ScalePixels(10);
                int reset = ScalePixels(10) + MeasureTextWidth(fullReset, _smallBoldFont) + ScalePixels(10);
                widths[i] = Math.Max(ScalePixels(112), Math.Max(collapsed, Math.Max(detail, reset)));
            }
            return widths;
        }

        private int[] LayoutColumnWidths(List<LimitWindow> windows)
        {
            int[] widths = NaturalColumnWidths(windows);
            int total = 0;
            foreach (int width in widths) total += width;
            int remaining = Math.Max(0, ClientSize.Width - total);
            for (int i = 0; i < widths.Length; i++)
            {
                int share = remaining / (widths.Length - i);
                widths[i] += share;
                remaining -= share;
            }
            return widths;
        }

        private static int MeasureTextWidth(string value, Font font)
        {
            return TextRenderer.MeasureText(value ?? String.Empty, font, new Size(Int32.MaxValue, Int32.MaxValue),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + 2;
        }

        private int RingOuterDiameter()
        {
            return ScalePixels(RingOuterDiameterPixels);
        }

        private int TokenFooterWidth()
        {
            return MeasureTextWidth(_texts.Yesterday + " ", _smallBoldFont) +
                MeasureTextWidth(Formatters.CompactTokens(_snapshot.YesterdayTokens), _smallBoldFont) +
                MeasureTextWidth("  ·  " + _texts.Lifetime + " ", _smallBoldFont) +
                MeasureTextWidth(Formatters.CompactTokens(_snapshot.LifetimeTokens), _smallBoldFont) +
                MeasureTextWidth(" Token", _smallBoldFont);
        }

        private void ClearNativeOwner()
        {
            if (!IsHandleCreated) return;
            if (_nativeOwner == IntPtr.Zero && NativeMethods.GetWindow(Handle, NativeMethods.GW_OWNER) == IntPtr.Zero) return;
            NativeMethods.SetWindowOwner(Handle, IntPtr.Zero);
            _nativeOwner = IntPtr.Zero;
        }

        private void RebuildFonts()
        {
            DisposeFonts();
            string family = String.IsNullOrWhiteSpace(_theme.FontFamily) ? "Segoe UI" : _theme.FontFamily.Trim();
            string faceName = _theme.FontFace == null ? String.Empty : _theme.FontFace.FullName;
            string systemName = String.IsNullOrWhiteSpace(faceName) ? family : faceName.Trim();
            FontFamily normalFamily = null;
            FontFamily emphasisFamily = null;
            string normalName = null;
            string emphasisName = null;
            if (IsSystemFont(systemName))
            {
                normalName = systemName;
                emphasisName = systemName;
            }
            else if (TryLoadUserFontFamilies(family, _theme.FontFace, out normalFamily, out emphasisFamily))
            {
                normalName = normalFamily.Name;
                emphasisName = emphasisFamily.Name;
            }
            else if (IsSystemFont(family)) normalName = emphasisName = family;
            else normalName = emphasisName = "Segoe UI";

            float uiPixels = Math.Min(MaximumUiFontSizePixels, Math.Max(MinimumUiFontSizePixels, _theme.FontSizePixels));
            float uiDevicePixels = uiPixels * _dpi / 96f;
            float menuDevicePixels = MenuFontSizePixels * _dpi / 96f;
            CreateFonts(uiDevicePixels, menuDevicePixels, normalFamily, emphasisFamily, normalName, emphasisName);
            Log.Write("overlay font configured=" + family + " resolved=" + _smallFont.FontFamily.Name +
                " uiPx=" + uiPixels.ToString("0.##", CultureInfo.InvariantCulture) +
                " uiPt=" + _smallFont.SizeInPoints.ToString("0.##", CultureInfo.InvariantCulture) +
                " menuPx=" + MenuFontSizePixels.ToString("0.##", CultureInfo.InvariantCulture) +
                " menuPt=" + _boldFont.SizeInPoints.ToString("0.##", CultureInfo.InvariantCulture));
        }

        private void CreateFonts(float uiDevicePixels, float menuDevicePixels, FontFamily normalFamily, FontFamily emphasisFamily, string normalName, string emphasisName)
        {
            _menuFont = normalFamily == null
                ? new Font(normalName, menuDevicePixels, FontStyle.Regular, GraphicsUnit.Pixel)
                : new Font(normalFamily, menuDevicePixels, FontStyle.Regular, GraphicsUnit.Pixel);
            _smallFont = normalFamily == null
                ? new Font(normalName, uiDevicePixels, FontStyle.Regular, GraphicsUnit.Pixel)
                : new Font(normalFamily, uiDevicePixels, FontStyle.Regular, GraphicsUnit.Pixel);
            _smallBoldFont = emphasisFamily == null
                ? new Font(emphasisName, uiDevicePixels, FontStyle.Bold, GraphicsUnit.Pixel)
                : new Font(emphasisFamily, uiDevicePixels, FontStyle.Regular, GraphicsUnit.Pixel);
            _boldFont = emphasisFamily == null
                ? new Font(emphasisName, menuDevicePixels, FontStyle.Bold, GraphicsUnit.Pixel)
                : new Font(emphasisFamily, menuDevicePixels, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        private static bool IsSystemFont(string name)
        {
            if (String.IsNullOrWhiteSpace(name)) return false;
            try
            {
                using (var font = new Font(name, 9f, FontStyle.Regular, GraphicsUnit.Point))
                    return NormalizeFontName(font.FontFamily.Name) == NormalizeFontName(name);
            }
            catch { return false; }
        }

        private bool TryLoadUserFontFamilies(string family, ThemeFontFace selectedFace, out FontFamily normalFamily, out FontFamily emphasisFamily)
        {
            normalFamily = null;
            emphasisFamily = null;
            UserFontFace normal = FindUserFontFace(family, selectedFace);
            if (normal == null) return false;
            UserFontFace emphasis = FindHeavierUserFontFace(family, normal) ?? normal;
            try
            {
                _normalPrivateFonts = new PrivateFontCollection();
                _normalPrivateFonts.AddFontFile(normal.Path);
                _emphasisPrivateFonts = new PrivateFontCollection();
                _emphasisPrivateFonts.AddFontFile(emphasis.Path);
                if (_normalPrivateFonts.Families.Length == 0 || _emphasisPrivateFonts.Families.Length == 0) return false;
                normalFamily = _normalPrivateFonts.Families[0];
                emphasisFamily = _emphasisPrivateFonts.Families[0];
                return true;
            }
            catch
            {
                if (_normalPrivateFonts != null) { _normalPrivateFonts.Dispose(); _normalPrivateFonts = null; }
                if (_emphasisPrivateFonts != null) { _emphasisPrivateFonts.Dispose(); _emphasisPrivateFonts = null; }
                return false;
            }
        }

        private static UserFontFace FindUserFontFace(string family, ThemeFontFace selectedFace)
        {
            List<UserFontFace> matches = MatchingUserFonts(family);
            if (matches.Count == 0) return null;
            string postscript = NormalizeFontName(selectedFace == null ? null : selectedFace.PostscriptName);
            string fullName = NormalizeFontName(selectedFace == null ? null : selectedFace.FullName);
            if (postscript.Length > 0 || fullName.Length > 0)
            {
                foreach (UserFontFace candidate in matches)
                    if ((postscript.Length > 0 && candidate.FileName == postscript) ||
                        (fullName.Length > 0 && candidate.Names.Contains(fullName))) return candidate;
            }
            string regular = NormalizeFontName(family) + "regular";
            foreach (UserFontFace candidate in matches)
                if (candidate.FileName == regular || candidate.FileName.EndsWith("regular", StringComparison.Ordinal)) return candidate;
            UserFontFace closest = matches[0];
            foreach (UserFontFace candidate in matches)
                if (Math.Abs(candidate.Weight - 400) < Math.Abs(closest.Weight - 400)) closest = candidate;
            return closest;
        }

        private static UserFontFace FindHeavierUserFontFace(string family, UserFontFace normal)
        {
            UserFontFace heavier = null;
            foreach (UserFontFace candidate in MatchingUserFonts(family))
            {
                if (candidate.Weight <= normal.Weight) continue;
                if (heavier == null || candidate.Weight < heavier.Weight) heavier = candidate;
            }
            return heavier;
        }

        private static List<UserFontFace> MatchingUserFonts(string family)
        {
            string wanted = NormalizeFontName(family);
            var matches = new List<UserFontFace>();
            foreach (UserFontFace candidate in UserFontFaces())
                if (candidate.Names.Contains(wanted)) matches.Add(candidate);
            return matches;
        }

        private static List<UserFontFace> UserFontFaces()
        {
            lock (FontCacheGate)
            {
                if (_userFontFaces != null) return _userFontFaces;
                var result = new List<UserFontFace>();
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Fonts");
                if (Directory.Exists(directory))
                {
                    foreach (string path in Directory.GetFiles(directory))
                    {
                        string extension = Path.GetExtension(path);
                        if (!String.Equals(extension, ".ttf", StringComparison.OrdinalIgnoreCase) &&
                            !String.Equals(extension, ".otf", StringComparison.OrdinalIgnoreCase)) continue;
                        try
                        {
                            var glyph = new System.Windows.Media.GlyphTypeface(new Uri(path, UriKind.Absolute));
                            var names = new HashSet<string>(StringComparer.Ordinal);
                            foreach (string value in glyph.FamilyNames.Values) names.Add(NormalizeFontName(value));
                            foreach (string value in glyph.Win32FamilyNames.Values) names.Add(NormalizeFontName(value));
                            string fileName = NormalizeFontName(Path.GetFileNameWithoutExtension(path));
                            names.Remove(String.Empty);
                            result.Add(new UserFontFace(path, fileName, names, FontWeight(fileName, glyph.Weight.ToOpenTypeWeight())));
                        }
                        catch { }
                    }
                }
                _userFontFaces = result;
                return _userFontFaces;
            }
        }

        private static int FontWeight(string name, int fallback)
        {
            if (name.EndsWith("ultralight", StringComparison.Ordinal) || name.EndsWith("extralight", StringComparison.Ordinal)) return 200;
            if (name.EndsWith("thin", StringComparison.Ordinal)) return 300;
            if (name.EndsWith("light", StringComparison.Ordinal)) return 350;
            if (name.EndsWith("regular", StringComparison.Ordinal) || name.EndsWith("normal", StringComparison.Ordinal)) return 400;
            if (name.EndsWith("medium", StringComparison.Ordinal)) return 500;
            if (name.EndsWith("semibold", StringComparison.Ordinal) || name.EndsWith("demibold", StringComparison.Ordinal)) return 600;
            if (name.EndsWith("extrabold", StringComparison.Ordinal)) return 800;
            if (name.EndsWith("bold", StringComparison.Ordinal)) return 700;
            if (name.EndsWith("black", StringComparison.Ordinal) || name.EndsWith("heavy", StringComparison.Ordinal)) return 900;
            return fallback;
        }

        private static string NormalizeFontName(string value)
        {
            return Regex.Replace(value ?? String.Empty, @"[^\p{L}\p{Nd}]", String.Empty).ToLowerInvariant();
        }

        private void DisposeFontObjects()
        {
            if (_smallFont != null) { _smallFont.Dispose(); _smallFont = null; }
            if (_smallBoldFont != null) { _smallBoldFont.Dispose(); _smallBoldFont = null; }
            if (_boldFont != null) { _boldFont.Dispose(); _boldFont = null; }
            if (_menuFont != null) { _menuFont.Dispose(); _menuFont = null; }
        }

        private void DisposeFonts()
        {
            DisposeFontObjects();
            if (_normalPrivateFonts != null) { _normalPrivateFonts.Dispose(); _normalPrivateFonts = null; }
            if (_emphasisPrivateFonts != null) { _emphasisPrivateFonts.Dispose(); _emphasisPrivateFonts = null; }
        }

        internal float UiFontSizeInPoints { get { return _smallFont.SizeInPoints; } }
        internal float CollapsedFontSizeInPoints { get { return _boldFont.SizeInPoints; } }
        internal int ExpandedYOffset { get { return _expanded ? ScalePixels(1) : 0; } }

        private sealed class UserFontFace
        {
            internal readonly string Path;
            internal readonly string FileName;
            internal readonly HashSet<string> Names;
            internal readonly int Weight;

            internal UserFontFace(string path, string fileName, HashSet<string> names, int weight)
            {
                Path = path;
                FileName = fileName;
                Names = names;
                Weight = weight;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle bounds = new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            using (GraphicsPath path = RoundedRectangle(bounds, ScalePixels(8)))
            using (var surface = new SolidBrush(_theme.Surface))
            using (var border = new Pen(Blend(_theme.Surface, _theme.Ink, _theme.Dark ? 0.16 : 0.10), 1f))
            {
                graphics.FillPath(surface, path);
                graphics.DrawPath(border, path);
            }

            List<LimitWindow> windows = _snapshot.DisplayWindows;
            if (_snapshot.IsThirdParty) { DrawThirdParty(graphics); return; }
            if (windows.Count == 0) return;
            if (_expanded) DrawExpanded(graphics, windows);
            else DrawCollapsed(graphics, windows);
        }

        private string BalanceText(CcUsageRow row)
        {
            return "$ " + row.Remaining.Value.ToString("0.##", CultureInfo.InvariantCulture) + " " + row.Unit;
        }
        private string EstimateText(CcUsageRow row)
        {
            return (_texts.Chinese ? "约 " : "~ ") + Math.Round(row.Remaining.Value, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + (_texts.Chinese ? "% 周" : "% week");
        }
        private string QuotaLabel(CcUsageRow row)
        {
            if (row.WindowSeconds == 18000) return _texts.FiveHour;
            if (row.WindowSeconds == 604800) return _texts.Weekly;
            return String.IsNullOrWhiteSpace(row.Name) ? (_texts.Chinese ? "额度" : "Quota") : row.Name;
        }
        private int QuotaColumnWidth(CcUsageRow row)
        {
            string percent = row.Remaining.Value.ToString("0", CultureInfo.InvariantCulture) + "%";
            return Math.Max(ScalePixels(112), Math.Max(ScalePixels(10) + RingOuterDiameter() + ScalePixels(13) + MeasureTextWidth(percent, _boldFont) + MeasureTextWidth(Formatters.ResetTime(row.ResetsAt, _texts.Chinese, true), _menuFont) + ScalePixels(10),
                MeasureTextWidth(Formatters.ResetTime(row.ResetsAt, _texts.Chinese, false), _smallFont) + ScalePixels(20)));
        }
        private string ThirdPartySummary()
        {
            if (!String.IsNullOrEmpty(_snapshot.SourceError)) return _snapshot.SourceError;
            foreach (CcUsageRow row in _snapshot.UsageRows)
                if (!row.Valid && row.Kind != "estimate" && !String.IsNullOrWhiteSpace(row.InvalidMessage)) return row.InvalidMessage;
            CcUsageRow balance = _snapshot.ThirdPartyBalance;
            if (balance != null) return BalanceText(balance);
            foreach (CcUsageRow row in _snapshot.UsageRows)
                if (row.Kind != "estimate") return row.ValueText(_texts.Chinese);
            return _texts.Chinese ? "暂无用量数据" : "Usage unavailable";
        }
        private void DrawDivider(Graphics graphics, int top)
        {
            using (var pen = new Pen(Blend(_theme.Surface, _theme.Ink, _theme.Dark ? 0.15 : 0.11)))
                graphics.DrawLine(pen, ScalePixels(10), top, ClientSize.Width - ScalePixels(10), top);
        }
        private void DrawThirdParty(Graphics graphics)
        {
            List<CcUsageRow> quotas = _snapshot.ThirdPartyQuotas;
            CcUsageRow balance = _snapshot.ThirdPartyBalance, estimate = _snapshot.ThirdPartyEstimate;
            Color muted = Blend(_theme.Surface, _theme.Ink, 0.68);
            int line = ExpandedLineHeight();
            if (!_expanded)
            {
                if (quotas.Count == 0)
                {
                    DrawText(graphics, ThirdPartySummary(), _menuFont, _theme.Ink, new RectangleF(ScalePixels(10), 0, ClientSize.Width - ScalePixels(20), ClientSize.Height), StringAlignment.Center, StringAlignment.Center);
                    return;
                }
                int walletWidth = balance == null ? 0 : StyledWidth(BalanceText(balance), _menuFont, _boldFont) + ScalePixels(18);
                int natural = 0; foreach (CcUsageRow row in quotas) natural += QuotaColumnWidth(row);
                int available = ClientSize.Width - walletWidth;
                float left = 0;
                for (int i = 0; i < quotas.Count; i++)
                {
                    CcUsageRow row = quotas[i];
                    float width = (float)QuotaColumnWidth(row) / Math.Max(1, natural) * available;
                    int ring = RingOuterDiameter(); float path = ring - RingStroke;
                    DrawProgressRing(graphics, new RectangleF(left + ScalePixels(10) + RingStroke / 2, (ClientSize.Height - path) / 2, path, path), row.Remaining.Value);
                    string percent = row.Remaining.Value.ToString("0", CultureInfo.InvariantCulture) + "%";
                    int pw = StyledWidth(percent, _menuFont, _boldFont);
                    DrawStyled(graphics, percent, _menuFont, _boldFont, _theme.Accent, new RectangleF(left + ScalePixels(10) + ring + ScalePixels(6), 0, pw, ClientSize.Height));
                    DrawText(graphics, Formatters.ResetTime(row.ResetsAt, _texts.Chinese, true), _menuFont, muted, new RectangleF(left + ScalePixels(10) + ring + ScalePixels(13) + pw, 0, Math.Max(1, width - ScalePixels(33) - ring - pw), ClientSize.Height), StringAlignment.Near, StringAlignment.Center);
                    left += width;
                    if (i < quotas.Count - 1) using (var separator = new Pen(Blend(_theme.Surface, _theme.Ink, 0.12))) graphics.DrawLine(separator, left, ScalePixels(10), left, ClientSize.Height - ScalePixels(10));
                }
                if (balance != null) DrawBalance(graphics, BalanceText(balance), _menuFont, _boldFont, _theme.Ink, new RectangleF(left + ScalePixels(4), 0, walletWidth - ScalePixels(10), ClientSize.Height));
                return;
            }
            int top;
            if (quotas.Count == 0)
            {
                top = line * 2 + ScalePixels(20);
                DrawText(graphics, ThirdPartySummary(), _smallFont, _theme.Ink, new RectangleF(ScalePixels(10), 0, ClientSize.Width - ScalePixels(20), top), StringAlignment.Center, StringAlignment.Center);
            }
            else
            {
                top = CollapsedHeight + line * 2 + ScalePixels(20);
                float width = (float)ClientSize.Width / quotas.Count;
                for (int i = 0; i < quotas.Count; i++)
                {
                    CcUsageRow row = quotas[i]; float left = i * width;
                    DrawProgress(graphics, new RectangleF(left + ScalePixels(10), (CollapsedHeight - ScalePixels(4)) / 2f, width - ScalePixels(20), ScalePixels(4)), row.Remaining.Value);
                    string label = QuotaLabel(row); int labelWidth = MeasureTextWidth(label, _smallFont);
                    DrawText(graphics, label, _smallFont, _theme.Ink, new RectangleF(left + ScalePixels(10), CollapsedHeight + ScalePixels(8), labelWidth, line), StringAlignment.Near, StringAlignment.Center);
                    DrawAlignedPercent(graphics, row.Remaining.Value.ToString("0", CultureInfo.InvariantCulture) + "%", Formatters.ResetTime(row.ResetsAt, _texts.Chinese, false), left + ScalePixels(10), CollapsedHeight + ScalePixels(8), width - ScalePixels(20), labelWidth, line);
                    DrawText(graphics, Formatters.ResetTime(row.ResetsAt, _texts.Chinese, false), _smallFont, muted, new RectangleF(left + ScalePixels(10), CollapsedHeight + line + ScalePixels(12), width - ScalePixels(20), line), StringAlignment.Near, StringAlignment.Center);
                    if (i < quotas.Count - 1) using (var separator = new Pen(Blend(_theme.Surface, _theme.Ink, 0.12))) graphics.DrawLine(separator, left + width, ScalePixels(8), left + width, top - ScalePixels(8));
                }
            }
            if (balance != null || estimate != null)
            {
                DrawDivider(graphics, top);
                int inset = ScalePixels(10);
                int estimateWidth = estimate == null ? 0 : StyledWidth(EstimateText(estimate), _smallFont, _smallBoldFont);
                float estimateLeft = ClientSize.Width - inset - estimateWidth;
                if (balance != null)
                {
                    float balanceWidth = estimate == null ? ClientSize.Width - inset * 2 : Math.Max(0, estimateLeft - inset - ScalePixels(20));
                    DrawBalance(graphics, BalanceText(balance), _smallFont, _smallBoldFont, _theme.Ink,
                        new RectangleF(inset, top + ScalePixels(5), balanceWidth, line + ScalePixels(8)));
                }
                if (estimate != null)
                    DrawStyled(graphics, EstimateText(estimate), _smallFont, _smallBoldFont, _theme.Ink,
                        new RectangleF(estimateLeft, top + ScalePixels(5), estimateWidth, line + ScalePixels(8)), true);
            }
        }
        private void DrawBalance(Graphics graphics, string value, Font normal, Font numeric, Color color, RectangleF rectangle)
        {
            DrawStyled(graphics, value, normal, numeric, color, rectangle);
        }

        // Align the percentage center with the time portion, independently in each column.
        internal float ResetTimeCenter(string reset)
        {
            Match time = Regex.Match(reset ?? "", @"\d{1,2}:\d{2}");
            if (!time.Success) return TextAdvance(reset, _smallFont) / 2f;
            int start = TextAdvance(reset.Substring(0, time.Index), _smallFont);
            int end = TextAdvance(reset.Substring(0, time.Index + time.Length), _smallFont);
            return (start + end) / 2f;
        }

        private void DrawAlignedPercent(Graphics graphics, string percent, string reset, float left, float top, float width, int labelWidth, int height)
        {
            int pw = StyledWidth(percent, _smallFont, _smallBoldFont);
            float x = left + PercentOffset(reset, pw, width, labelWidth);
            DrawStyled(graphics, percent, _smallFont, _smallBoldFont, _theme.Accent, new RectangleF(x, top, Math.Max(0, left + width - x), height));
        }

        internal float PercentOffset(string reset, int percentWidth, float columnWidth, int labelWidth)
        {
            float fallback = labelWidth + ScalePixels(8);
            if (!Regex.IsMatch(reset ?? "", @"\d{1,2}:\d{2}")) return fallback;
            float centered = ResetTimeCenter(reset) - percentWidth / 2f;
            return centered >= fallback && centered + percentWidth <= columnWidth ? centered : fallback;
        }

        // Use glyph advances, without TextRenderer's layout overhang, between styled runs.
        private static int TextAdvance(string value, Font font)
        {
            if (String.IsNullOrEmpty(value)) return 0;
            IntPtr dc = NativeMethods.GetDC(IntPtr.Zero);
            IntPtr handle = font.ToHfont();
            IntPtr previous = NativeMethods.SelectObject(dc, handle);
            try
            {
                Size size;
                if (!NativeMethods.GetTextExtentPoint32(dc, value, value.Length, out size))
                    throw new InvalidOperationException("Unable to measure overlay text.");
                return size.Width;
            }
            finally
            {
                NativeMethods.SelectObject(dc, previous);
                NativeMethods.DeleteObject(handle);
                NativeMethods.ReleaseDC(IntPtr.Zero, dc);
            }
        }

        private static int StyledWidth(string value, Font normal, Font numeric)
        {
            int width = 0;
            foreach (string part in Regex.Split(value ?? "", @"([+-]?\d+(?:\.\d+)?(?:[%KBM])?)"))
                if (part.Length > 0) width += TextAdvance(part, Regex.IsMatch(part, @"^(?:[+-]?\d+(?:\.\d+)?(?:[%KBM])?)$") ? numeric : normal);
            return width;
        }
        private void DrawStyled(Graphics graphics, string value, Font normal, Font numeric, Color color, RectangleF rectangle, bool accentNumbers = false)
        {
            float left = rectangle.Left;
            foreach (string part in Regex.Split(value ?? "", @"([+-]?\d+(?:\.\d+)?(?:[%KBM])?)"))
            {
                if (part.Length == 0) continue;
                Font font = Regex.IsMatch(part, @"^(?:[+-]?\d+(?:\.\d+)?(?:[%KBM])?)$") ? numeric : normal;
                int width = TextAdvance(part, font);
                if (left >= rectangle.Right) break;
                Color ink = (accentNumbers || value.StartsWith("$ ", StringComparison.Ordinal)) && Regex.IsMatch(part, @"^[+-]?\d") ? _theme.Accent : color;
                DrawText(graphics, part, font, ink, new RectangleF(left, rectangle.Top, Math.Min(width, rectangle.Right - left), rectangle.Height), StringAlignment.Near, StringAlignment.Center);
                left += width;
            }
        }

        private void DrawCollapsed(Graphics graphics, List<LimitWindow> windows)
        {
            int[] widths = LayoutColumnWidths(windows);
            float left = 0;
            for (int i = 0; i < windows.Count; i++)
            {
                float columnWidth = widths[i];
                LimitWindow window = windows[i];
                int ringOuter = RingOuterDiameter();
                float ringPath = ringOuter - RingStroke;
                float ringLeft = left + ScalePixels(10) + RingStroke / 2f;
                float ringTop = (ClientSize.Height - ringPath) / 2f;
                DrawProgressRing(graphics, new RectangleF(ringLeft, ringTop, ringPath, ringPath), window.Remaining);
                string percent = Math.Round(window.Remaining, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture) + "%";
                int percentWidth = MeasureTextWidth(percent, _boldFont);
                DrawStyled(graphics, percent, _menuFont, _boldFont, _theme.Accent,
                    new RectangleF(left + ScalePixels(10) + ringOuter + ScalePixels(6), 0, percentWidth, ClientSize.Height));
                DrawText(graphics, Formatters.ResetTime(window.ResetsAt, _texts.Chinese, true), _menuFont,
                    Blend(_theme.Surface, _theme.Ink, 0.68), new RectangleF(left + ScalePixels(10) + ringOuter + ScalePixels(13) + percentWidth, 0,
                        columnWidth - ScalePixels(33) - ringOuter - percentWidth, ClientSize.Height), StringAlignment.Near, StringAlignment.Center);
                left += columnWidth;
            }
            if (windows.Count == 2)
            {
                using (var separator = new Pen(Blend(_theme.Surface, _theme.Ink, _theme.Dark ? 0.13 : 0.09), 1f))
                    graphics.DrawLine(separator, widths[0], ScalePixels(12), widths[0], ClientSize.Height - ScalePixels(12));
            }
        }

        private void DrawExpanded(Graphics graphics, List<LimitWindow> windows)
        {
            int[] widths = LayoutColumnWidths(windows);
            float left = 0;
            float barHeight = ScalePixels(4);
            float barTop = (CollapsedHeight - barHeight) / 2f;
            int lineHeight = ExpandedLineHeight();
            int detailTop = CollapsedHeight + ScalePixels(8);
            int resetTop = detailTop + lineHeight + ScalePixels(4);
            for (int i = 0; i < windows.Count; i++)
            {
                float columnWidth = widths[i];
                LimitWindow window = windows[i];
                DrawProgress(graphics, new RectangleF(left + ScalePixels(10), barTop, columnWidth - ScalePixels(20), barHeight), window.Remaining);
                string label = IsFiveHour(window) ? _texts.FiveHour : _texts.Weekly;
                int labelWidth = MeasureTextWidth(label, _smallBoldFont);
                DrawText(graphics, label, _smallFont, _theme.Ink,
                    new RectangleF(left + ScalePixels(10), detailTop, labelWidth, lineHeight), StringAlignment.Near);
                DrawAlignedPercent(graphics, Math.Round(window.Remaining, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture) + "%",
                    Formatters.ResetTime(window.ResetsAt, _texts.Chinese, false), left + ScalePixels(10), detailTop, columnWidth - ScalePixels(20), labelWidth, lineHeight);
                DrawText(graphics, Formatters.ResetTime(window.ResetsAt, _texts.Chinese, false), _smallFont,
                    Blend(_theme.Surface, _theme.Ink, 0.70), new RectangleF(left + ScalePixels(10), resetTop, columnWidth - ScalePixels(20), lineHeight), StringAlignment.Near);
                left += columnWidth;
            }
            int footerTop = resetTop + lineHeight + ScalePixels(8);
            if (windows.Count == 2)
            {
                using (var separator = new Pen(Blend(_theme.Surface, _theme.Ink, _theme.Dark ? 0.13 : 0.09), 1f))
                    graphics.DrawLine(separator, widths[0], ScalePixels(8), widths[0], footerTop - ScalePixels(8));
            }
            using (var footer = new Pen(Blend(_theme.Surface, _theme.Ink, _theme.Dark ? 0.15 : 0.11), 1f))
                graphics.DrawLine(footer, ScalePixels(10), footerTop, ClientSize.Width - ScalePixels(10), footerTop);
            DrawTokenFooter(graphics, footerTop + ScalePixels(7), lineHeight + ScalePixels(10));
        }

        private void DrawTokenFooter(Graphics graphics, float top, float height)
        {
            Color dateColor = Blend(_theme.Surface, _theme.Ink, 0.70);
            float left = ScalePixels(10);
            left = DrawTokenSegment(graphics, _texts.Yesterday + " ", dateColor, left, top, height);
            left = DrawTokenSegment(graphics, Formatters.CompactTokens(_snapshot.YesterdayTokens), _theme.Accent, left, top, height);
            left = DrawTokenSegment(graphics, "  ·  " + _texts.Lifetime + " ", dateColor, left, top, height);
            left = DrawTokenSegment(graphics, Formatters.CompactTokens(_snapshot.LifetimeTokens), _theme.Accent, left, top, height);
            DrawTokenSegment(graphics, " Token", dateColor, left, top, height);
        }

        private float DrawTokenSegment(Graphics graphics, string value, Color color, float left, float top, float height)
        {
            int width = MeasureTextWidth(value, _smallBoldFont);
            DrawStyled(graphics, value, _smallFont, _smallBoldFont, color,
                new RectangleF(left, top, Math.Min(width, Math.Max(0, ClientSize.Width - ScalePixels(10) - left)), height));
            return left + width;
        }

        private bool IsFiveHour(LimitWindow window)
        {
            if (window.WindowDurationMins.HasValue)
                return Math.Abs(window.WindowDurationMins.Value - 300) <= 6;
            return (window.Key ?? String.Empty).IndexOf("primary", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void DrawProgressRing(Graphics graphics, RectangleF rectangle, double remaining)
        {
            float sweep = Math.Max(8f, (float)(360 * Math.Max(0, Math.Min(100, remaining)) / 100.0));
            using (var track = new Pen(Blend(_theme.Surface, _theme.Ink, _theme.Dark ? 0.20 : 0.13), RingStroke))
            using (var fill = new Pen(_theme.Accent, RingStroke))
            {
                track.StartCap = track.EndCap = LineCap.Round;
                fill.StartCap = fill.EndCap = LineCap.Round;
                graphics.DrawEllipse(track, rectangle);
                graphics.DrawArc(fill, rectangle, -90, sweep);
            }
        }

        private void DrawProgress(Graphics graphics, RectangleF rectangle, double remaining)
        {
            double clamped = Math.Max(0, Math.Min(100, remaining));
            using (var track = new SolidBrush(Blend(_theme.Surface, _theme.Ink, _theme.Dark ? 0.15 : 0.10)))
            using (var fill = new SolidBrush(_theme.Accent))
            {
                graphics.FillRectangle(track, rectangle);
                float width = Math.Max(2f, (float)(rectangle.Width * clamped / 100.0));
                graphics.FillRectangle(fill, new RectangleF(rectangle.X, rectangle.Y, Math.Min(rectangle.Width, width), rectangle.Height));
            }
        }

        private static void DrawText(Graphics graphics, string value, Font font, Color color, RectangleF rectangle, StringAlignment alignment)
        {
            DrawText(graphics, value, font, color, rectangle, alignment, StringAlignment.Near);
        }

        private static void DrawText(Graphics graphics, string value, Font font, Color color, RectangleF rectangle,
            StringAlignment alignment, StringAlignment lineAlignment)
        {
            TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine |
                TextFormatFlags.EndEllipsis | TextFormatFlags.PreserveGraphicsClipping |
                TextFormatFlags.PreserveGraphicsTranslateTransform;
            if (alignment == StringAlignment.Center) flags |= TextFormatFlags.HorizontalCenter;
            else if (alignment == StringAlignment.Far) flags |= TextFormatFlags.Right;
            if (lineAlignment == StringAlignment.Center) flags |= TextFormatFlags.VerticalCenter;
            else if (lineAlignment == StringAlignment.Far) flags |= TextFormatFlags.Bottom;
            TextRenderer.DrawText(graphics, value ?? String.Empty, font, Rectangle.Round(rectangle), color, flags);
        }

        private static Color Blend(Color background, Color foreground, double amount)
        {
            amount = Math.Max(0, Math.Min(1, amount));
            return Color.FromArgb(
                (int)Math.Round(background.R + (foreground.R - background.R) * amount),
                (int)Math.Round(background.G + (foreground.G - background.G) * amount),
                (int)Math.Round(background.B + (foreground.B - background.B) * amount));
        }

        private static GraphicsPath RoundedRectangle(Rectangle rectangle, int radius)
        {
            int diameter = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private void UpdateRegion()
        {
            if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
            using (GraphicsPath path = RoundedRectangle(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), ScalePixels(8)))
            {
                Region old = Region;
                Region = new Region(path);
                if (old != null) old.Dispose();
            }
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            UpdateRegion();
            Action callback = OverlaySizeChanged;
            if (callback != null) callback();
        }

        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            if (_programmaticMove || _nativeDrag || _mode != DisplayMode.Independent || !Visible) return;
            Action<int, int> callback = IndependentPositionChanged;
            if (callback != null) callback(Left, Top);
        }

        private void OnWindowMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (e.Y < CollapsedHeight)
            {
                SetExpanded(!_expanded);
                return;
            }
            if (_mode != DisplayMode.Independent) return;
            _nativeDrag = true;
            try
            {
                NativeMethods.ReleaseCapture();
                NativeMethods.SendMessage(Handle, NativeMethods.WM_NCLBUTTONDOWN, new IntPtr(NativeMethods.HTCAPTION), IntPtr.Zero);
            }
            finally { _nativeDrag = false; }
            Action<int, int> callback = IndependentPositionChanged;
            if (callback != null) callback(Left, Top);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeFonts();
            }
            base.Dispose(disposing);
        }
    }
}
