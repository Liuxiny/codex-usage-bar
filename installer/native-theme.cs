using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CodexUsageBar
{
    internal static class NativeTheme
    {
        internal static Color Mix(Color surface, Color ink, double amount)
        {
            return Color.FromArgb((int)(surface.R + (ink.R - surface.R) * amount), (int)(surface.G + (ink.G - surface.G) * amount), (int)(surface.B + (ink.B - surface.B) * amount));
        }
        internal static GraphicsPath Rounded(Rectangle bounds, int radius)
        {
            int d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure(); return path;
        }
        internal static Font UiFont(ThemePalette theme, float extra, FontStyle style)
        {
            string family = theme.FontFace == null ? theme.FontFamily : theme.FontFace.Family;
            family = (family ?? "Segoe UI").Split(',')[0].Trim().Trim('"', '\'');
            float size = Math.Max(11, Math.Min(16, theme.FontSizePixels)) + extra;
            try
            {
                var font = new Font(family, size, style, GraphicsUnit.Pixel);
                if (String.Equals(font.FontFamily.Name, family, StringComparison.OrdinalIgnoreCase)) return font;
                font.Dispose();
            }
            catch { }
            return new Font("Microsoft YaHei UI", size, style, GraphicsUnit.Pixel);
        }
        internal static void Apply(Control control, ThemePalette theme)
        {
            string role = control.Tag as string ?? "";
            Color card = Mix(theme.Surface, theme.Ink, theme.Dark ? 0.027 : 0.018);
            control.BackColor = control.Parent is ThemeCard ? card : control.Parent == null ? theme.Surface : control.Parent.BackColor;
            control.ForeColor = role == "muted" ? Mix(theme.Surface, theme.Ink, 0.60) : theme.Ink;
            var themedButton = control as ThemeButton;
            if (themedButton != null) themedButton.ApplyTheme(theme);
            var panel = control as ThemeCard;
            if (panel != null) { panel.Palette = theme; panel.BackColor = card; }
            var text = control as TextBox;
            if (text != null)
            {
                text.BorderStyle = BorderStyle.None;
                text.BackColor = Mix(theme.Surface, theme.Ink, theme.Dark ? 0.06 : 0.045);
                text.ForeColor = text.ReadOnly ? Mix(theme.Surface, theme.Ink, 0.78) : theme.Ink;
            }
            var input = control as InputSurface;
            if (input != null) { input.Palette = theme; input.BackColor = Mix(theme.Surface, theme.Ink, theme.Dark ? 0.06 : 0.045); }
            var number = control as NumericUpDown;
            if (number != null) { number.BorderStyle = BorderStyle.None; number.BackColor = Mix(theme.Surface, theme.Ink, theme.Dark ? 0.06 : 0.045); number.ForeColor = theme.Ink; }
            var combo = control as ComboBox;
            if (combo != null) { combo.FlatStyle = FlatStyle.Flat; combo.BackColor = Mix(theme.Surface, theme.Ink, theme.Dark ? 0.08 : 0.05); }
            var themedCombo = control as ThemeComboBox;
            if (themedCombo != null) themedCombo.Palette = theme;
            foreach (Control child in control.Controls) Apply(child, theme);
            if (control is TextBox || control is ComboBox || control is NumericUpDown)
            {
                try { SetWindowTheme(control.Handle, theme.Dark ? "DarkMode_Explorer" : "Explorer", null); } catch { }
            }
            control.Invalidate();
        }
        internal static void TitleBar(Form form, bool dark)
        {
            try { int value = dark ? 1 : 0; DwmSetWindowAttribute(form.Handle, 20, ref value, sizeof(int)); }
            catch { }
        }
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr window, string subAppName, string subIdList);
    }

    internal sealed class ThemeComboBox : ComboBox
    {
        internal ThemePalette Palette = ThemePalette.CreateDefault(true);
        internal ThemeComboBox() { DrawMode = DrawMode.OwnerDrawFixed; ItemHeight = 30; DropDownStyle = ComboBoxStyle.DropDownList; }
        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            Color background = NativeTheme.Mix(Palette.Surface, Palette.Ink, (e.State & DrawItemState.Selected) != 0 ? 0.12 : 0.06);
            using (var brush = new SolidBrush(background)) e.Graphics.FillRectangle(brush, e.Bounds);
            string text = e.Index >= 0 && e.Index < Items.Count ? GetItemText(Items[e.Index]) : Text;
            TextRenderer.DrawText(e.Graphics, text, Font, new Rectangle(e.Bounds.X + 10, e.Bounds.Y, e.Bounds.Width - 20, e.Bounds.Height), Palette.Ink, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    internal sealed class ThemeCard : Panel
    {
        internal ThemePalette Palette = ThemePalette.CreateDefault(true);
        internal ThemeCard() { DoubleBuffered = true; Padding = new Padding(18); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (Width < 2 || Height < 2) return;
            using (var path = NativeTheme.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 10))
            using (var pen = new Pen(NativeTheme.Mix(Palette.Surface, Palette.Ink, 0.12))) e.Graphics.DrawPath(pen, path);
        }
    }

    internal sealed class InputSurface : Panel
    {
        internal ThemePalette Palette = ThemePalette.CreateDefault(true);
        internal InputSurface(Control input, bool multiline)
        {
            Padding = multiline ? new Padding(12) : new Padding(10, 8, 10, 5);
            Margin = new Padding(0, 2, 0, 4); Dock = DockStyle.Fill;
            input.Dock = DockStyle.Fill; Controls.Add(input); DoubleBuffered = true;
            input.Enter += delegate { Invalidate(); }; input.Leave += delegate { Invalidate(); };
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (Width < 2 || Height < 2) return;
            using (var path = NativeTheme.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 6))
            using (var pen = new Pen(ContainsFocus ? NativeTheme.Mix(Palette.Surface, Palette.Accent, 0.7) : NativeTheme.Mix(Palette.Surface, Palette.Ink, 0.08))) e.Graphics.DrawPath(pen, path);
        }
    }

    internal sealed class ThemeButton : Button
    {
        private ThemePalette _theme = ThemePalette.CreateDefault(true);
        private bool _hover;
        internal bool Primary;
        internal ThemeButton()
        {
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
            Padding = new Padding(14, 5, 14, 5); Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }
        internal void ApplyTheme(ThemePalette theme) { _theme = theme; Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? _theme.Surface : Parent.BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill = Primary ? NativeTheme.Mix(_theme.Surface, _theme.Accent, _hover ? 0.30 : 0.20) : NativeTheme.Mix(_theme.Surface, _theme.Ink, _hover ? 0.12 : 0.06);
            Color ink = !Enabled ? NativeTheme.Mix(_theme.Surface, _theme.Ink, 0.32) : Primary ? _theme.Accent : _theme.Ink;
            using (var path = NativeTheme.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 7))
            using (var brush = new SolidBrush(fill)) e.Graphics.FillPath(brush, path);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, new Rectangle(4, 4, Width - 8, Height - 8), ink, fill);
        }
    }

    internal sealed class TrayThemeRenderer : ToolStripProfessionalRenderer
    {
        private readonly ThemePalette _theme;
        internal TrayThemeRenderer(ThemePalette theme) { _theme = theme; RoundedEdges = false; }
        internal static void Apply(ToolStrip strip, ThemePalette theme, Font font)
        {
            strip.Renderer = new TrayThemeRenderer(theme); strip.Font = font;
            strip.BackColor = theme.Surface; strip.ForeColor = theme.Ink;
            strip.Padding = new Padding(6); strip.ShowItemToolTips = false;
            int width = 244;
            foreach (ToolStripItem item in strip.Items)
                width = Math.Max(width, TextRenderer.MeasureText(item.Text, font).Width + 72);
            strip.MinimumSize = new Size(width, 0);
            var menu = strip as ToolStripDropDownMenu;
            if (menu != null) { menu.ShowImageMargin = false; menu.ShowCheckMargin = true; }
            foreach (ToolStripItem item in strip.Items)
            {
                item.Font = font; item.ForeColor = theme.Ink;
                item.Padding = item is ToolStripSeparator ? new Padding(0, 3, 0, 3) : new Padding(4, 7, 12, 7);
                item.AutoSize = false;
                item.Size = new Size(width - 12, item is ToolStripSeparator ? 12 : Math.Max(34, font.Height + 16));
                var submenu = item as ToolStripMenuItem;
                if (submenu != null && submenu.HasDropDownItems) Apply(submenu.DropDown, theme, font);
            }
            strip.Invalidate();
        }
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) { e.Graphics.Clear(_theme.Surface); }
        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = NativeTheme.Rounded(new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1), 9))
            using (var pen = new Pen(NativeTheme.Mix(_theme.Surface, _theme.Ink, 0.16))) e.Graphics.DrawPath(pen, path);
        }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = NativeTheme.Rounded(new Rectangle(1, 1, e.Item.Width - 2, e.Item.Height - 2), 6))
            using (var brush = new SolidBrush(NativeTheme.Mix(_theme.Surface, _theme.Ink, 0.08))) e.Graphics.FillPath(brush, path);
        }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? _theme.Ink : NativeTheme.Mix(_theme.Surface, _theme.Ink, 0.56);
            var bounds = new Rectangle(e.TextRectangle.Left, 0, Math.Max(1, e.Item.Width - e.TextRectangle.Left - 24), e.Item.Height);
            TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, bounds, e.TextColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using (var pen = new Pen(NativeTheme.Mix(_theme.Surface, _theme.Ink, 0.12))) e.Graphics.DrawLine(pen, 10, e.Item.Height / 2, e.Item.Width - 10, e.Item.Height / 2);
        }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { e.ArrowColor = NativeTheme.Mix(_theme.Surface, _theme.Ink, 0.6); base.OnRenderArrow(e); }
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Rectangle r = e.ImageRectangle; float x = r.Left + 2, y = r.Top + r.Height / 2;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(_theme.Accent, 1.8f)) e.Graphics.DrawLines(pen, new[] { new PointF(x, y), new PointF(x + 3, y + 3), new PointF(x + 9, y - 4) });
        }
    }
}
