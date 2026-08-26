using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace CodexUsageBar
{
    internal sealed class OverlayForm : Form
    {
        internal const int ToolbarHeight = 35;
        private const int CollapsedHeight = ToolbarHeight - 2;
        private const int ExpandedHeight = CollapsedHeight + 81;
        private const float RingStrokeWidth = 2f;
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

        internal event Action OverlaySizeChanged;
        internal event Action<int, int> IndependentPositionChanged;

        internal OverlayForm()
        {
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
            Size next = new Size(DesiredWidth(), expanded ? ExpandedHeight : CollapsedHeight);
            if (ClientSize != next) ClientSize = next;
            Invalidate();
        }

        private int DesiredWidth()
        {
            List<LimitWindow> windows = _snapshot.DisplayWindows;
            if (windows.Count == 0) return 300;
            int total = 0;
            foreach (int width in NaturalColumnWidths(windows)) total += width;
            return Math.Max(total, MeasureTextWidth(TokenText(), _smallFont) + 20);
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
                int collapsed = 10 + RingOuterDiameter() + 6 + MeasureTextWidth(percent, _boldFont) + 7 + MeasureTextWidth(compactReset, _smallBoldFont) + 10;
                int detail = 10 + MeasureTextWidth(label, _smallFont) + 8 + MeasureTextWidth(percent, _smallBoldFont) + 10;
                int reset = 10 + MeasureTextWidth(fullReset, _smallFont) + 10;
                widths[i] = Math.Max(112, Math.Max(collapsed, Math.Max(detail, reset)));
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
            return Math.Max(8, MeasureTextWidth("00", _boldFont) - 6);
        }

        private string TokenText()
        {
            return _texts.Yesterday + " " + Formatters.CompactTokens(_snapshot.YesterdayTokens) + "  ·  " +
                _texts.Lifetime + " " + Formatters.CompactTokens(_snapshot.LifetimeTokens) + " Token";
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
            if (_smallFont != null) _smallFont.Dispose();
            if (_smallBoldFont != null) _smallBoldFont.Dispose();
            if (_boldFont != null) _boldFont.Dispose();
            string family = String.IsNullOrWhiteSpace(_theme.FontFamily) ? "Segoe UI" : _theme.FontFamily;
            try
            {
                _smallFont = new Font(family, 8.25f, FontStyle.Regular, GraphicsUnit.Point);
                _smallBoldFont = new Font(family, 8.25f, FontStyle.Bold, GraphicsUnit.Point);
                _boldFont = new Font(family, 9.25f, FontStyle.Bold, GraphicsUnit.Point);
            }
            catch
            {
                _smallFont = new Font("Segoe UI", 8.25f, FontStyle.Regular, GraphicsUnit.Point);
                _smallBoldFont = new Font("Segoe UI", 8.25f, FontStyle.Bold, GraphicsUnit.Point);
                _boldFont = new Font("Segoe UI", 9.25f, FontStyle.Bold, GraphicsUnit.Point);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            Rectangle bounds = new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            using (GraphicsPath path = RoundedRectangle(bounds, 8))
            using (var surface = new SolidBrush(_theme.Surface))
            using (var border = new Pen(Blend(_theme.Surface, _theme.Ink, _theme.Dark ? 0.16 : 0.10), 1f))
            {
                graphics.FillPath(surface, path);
                graphics.DrawPath(border, path);
            }

            List<LimitWindow> windows = _snapshot.DisplayWindows;
            if (windows.Count == 0) return;
            if (_expanded) DrawExpanded(graphics, windows);
            else DrawCollapsed(graphics, windows);
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
                float ringPath = ringOuter - RingStrokeWidth;
                float ringLeft = left + 10 + RingStrokeWidth / 2f;
                float ringTop = (ClientSize.Height - ringPath) / 2f;
                DrawProgressRing(graphics, new RectangleF(ringLeft, ringTop, ringPath, ringPath), window.Remaining);
                string percent = Math.Round(window.Remaining, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture) + "%";
                int percentWidth = MeasureTextWidth(percent, _boldFont);
                DrawText(graphics, percent, _boldFont, _theme.Accent,
                    new RectangleF(left + 10 + ringOuter + 6, 0, percentWidth, ClientSize.Height), StringAlignment.Near, StringAlignment.Center);
                DrawText(graphics, Formatters.ResetTime(window.ResetsAt, _texts.Chinese, true), _smallBoldFont,
                    Blend(_theme.Surface, _theme.Ink, 0.68), new RectangleF(left + 10 + ringOuter + 13 + percentWidth, 1,
                        columnWidth - 33 - ringOuter - percentWidth, ClientSize.Height), StringAlignment.Near, StringAlignment.Center);
                left += columnWidth;
            }
            if (windows.Count == 2)
            {
                using (var separator = new Pen(Blend(_theme.Surface, _theme.Ink, _theme.Dark ? 0.13 : 0.09), 1f))
                    graphics.DrawLine(separator, widths[0], 12, widths[0], ClientSize.Height - 12);
            }
        }

        private void DrawExpanded(Graphics graphics, List<LimitWindow> windows)
        {
            int[] widths = LayoutColumnWidths(windows);
            float left = 0;
            const float barHeight = 4;
            float barTop = (CollapsedHeight - barHeight) / 2f;
            int detailTop = CollapsedHeight + 6;
            int resetTop = CollapsedHeight + 25;
            for (int i = 0; i < windows.Count; i++)
            {
                float columnWidth = widths[i];
                LimitWindow window = windows[i];
                DrawProgress(graphics, new RectangleF(left + 10, barTop, columnWidth - 20, barHeight), window.Remaining);
                string label = IsFiveHour(window) ? _texts.FiveHour : _texts.Weekly;
                int labelWidth = MeasureTextWidth(label, _smallFont);
                DrawText(graphics, label, _smallFont, _theme.Ink,
                    new RectangleF(left + 10, detailTop, labelWidth, 18), StringAlignment.Near);
                DrawText(graphics, Math.Round(window.Remaining, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture) + "%",
                    _smallBoldFont, _theme.Accent, new RectangleF(left + 10 + labelWidth + 8, detailTop,
                        columnWidth - 28 - labelWidth, 18), StringAlignment.Near);
                DrawText(graphics, Formatters.ResetTime(window.ResetsAt, _texts.Chinese, false), _smallFont,
                    Blend(_theme.Surface, _theme.Ink, 0.70), new RectangleF(left + 10, resetTop, columnWidth - 20, 18), StringAlignment.Near);
                left += columnWidth;
            }
            if (windows.Count == 2)
            {
                using (var separator = new Pen(Blend(_theme.Surface, _theme.Ink, _theme.Dark ? 0.13 : 0.09), 1f))
                    graphics.DrawLine(separator, widths[0], 8, widths[0], CollapsedHeight + 43);
            }
            int footerTop = CollapsedHeight + 49;
            using (var footer = new Pen(Blend(_theme.Surface, _theme.Ink, _theme.Dark ? 0.15 : 0.11), 1f))
                graphics.DrawLine(footer, 10, footerTop, ClientSize.Width - 10, footerTop);
            DrawText(graphics, TokenText(), _smallFont, _theme.Ink,
                new RectangleF(10, footerTop + 10, ClientSize.Width - 20, 18), StringAlignment.Near);
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
            using (var track = new Pen(Blend(_theme.Surface, _theme.Ink, _theme.Dark ? 0.20 : 0.13), RingStrokeWidth))
            using (var fill = new Pen(_theme.Accent, RingStrokeWidth))
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
            using (var brush = new SolidBrush(color))
            using (var format = new StringFormat())
            {
                format.Alignment = alignment;
                format.LineAlignment = lineAlignment;
                format.Trimming = StringTrimming.EllipsisCharacter;
                format.FormatFlags = StringFormatFlags.NoWrap;
                graphics.DrawString(value ?? String.Empty, font, brush, rectangle, format);
            }
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
            using (GraphicsPath path = RoundedRectangle(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), 8))
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
                if (_smallFont != null) _smallFont.Dispose();
                if (_smallBoldFont != null) _smallBoldFont.Dispose();
                if (_boldFont != null) _boldFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
