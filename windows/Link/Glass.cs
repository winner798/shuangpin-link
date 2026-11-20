using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ShuangpinLink;

internal static class Glass
{
    internal static bool Contrast { get; } = SystemInformation.HighContrast;
    internal static bool Dark { get; } = !Contrast && UsesDarkTheme();
    static readonly bool PressMotion = !Contrast && SystemParametersInfoW(0x1042, 0, out int animation, 0) && animation != 0;

    internal static Color Surface => Token(0xF4F1FB, 0x11121E, SystemColors.Control);
    internal static Color Ink => Token(0x201B32, 0xF6F1FF, SystemColors.WindowText);
    internal static Color Muted => Token(0x645C77, 0xB8B2CB, SystemColors.WindowText);
    internal static Color AccentText => Token(0x533A90, 0xCCBAFF, SystemColors.WindowText);
    internal static Color Tint => Token(0xE7DEFF, 0x35304F, SystemColors.Control);
    internal static Color Field => Token(0xFCFAFF, 0x1C1B2C, SystemColors.Window);
    internal static Color Border => Token(0xDFD4F2, 0x504466, SystemColors.WindowText);

    static bool UsesDarkTheme()
    {
        try
        {
            return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1) is 0;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        { return false; }
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SystemParametersInfoW(uint action, uint parameter, out int value, uint flags);

    internal static void ApplyCaption(IntPtr handle)
    {
        if (Contrast) return;
        int dark = Dark ? 1 : 0;
        DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
    }

    internal static Color Token(int light, int dark, Color fallback) => Contrast ? fallback : Rgb(Dark ? dark : light);
    static Color Rgb(int rgb) => Color.FromArgb(unchecked((int)0xFF000000) | rgb);
    internal static float Dp(Control control, float value) => value * control.DeviceDpi / 96f;

    internal static void PaintBackdrop(Graphics graphics, Rectangle bounds, bool header)
    {
        if (bounds.Width < 1 || bounds.Height < 1) return;
        Color start = header ? Token(0xFDFBFF, 0x252239, Surface) : Token(0xF9F5FF, 0x191829, Surface);
        using var wash = new LinearGradientBrush(bounds, start, Surface, 51f);
        graphics.FillRectangle(wash, bounds);
    }

    internal static void PaintCard(Control control, Graphics graphics) => PaintShape(graphics, Inside(control), Dp(control, 22),
        Token(0xFEFBFF, 0x262238, Field), Token(0xF7F2FC, 0x1B1B2D, Field), Border);

    internal static void PaintField(Control control, Graphics graphics, bool focused) => PaintShape(graphics, Inside(control), Dp(control, 12),
        Field, Field, focused ? AccentText : Border);

    internal static void PaintButton(ButtonBase button, Graphics graphics, bool prominent, bool focused)
    {
        bool hover = button.Enabled && button.ClientRectangle.Contains(button.PointToClient(Cursor.Position));
        bool pressed = hover && button.Capture && (Control.MouseButtons & MouseButtons.Left) != 0;
        Color top = prominent ? Rgb(0xD2C1FF) : Token(0xFCF8FF, 0x302943, Field);
        Color bottom = prominent ? Rgb(0xB8A4FF) : Token(0xF0E7FB, 0x242237, Tint);
        if (hover) bottom = prominent ? Rgb(0xC4ACFF) : Tint;
        if (pressed) bottom = prominent ? Rgb(0xAF96F7) : Token(0xDFD0F7, 0x403354, Tint);
        var bounds = Inside(button);
        if (pressed && PressMotion) bounds.Inflate(-bounds.Width * .023f, -bounds.Height * .023f);
        PaintShape(graphics, bounds, Dp(button, 14), top, bottom,
            focused ? AccentText : prominent ? Rgb(0xB49BEF) : Border);

        Color text = !button.Enabled ? Muted : prominent ? Rgb(0x2F214A) : Ink;
        var textBounds = Rectangle.Round(bounds);
        textBounds.Inflate(-(int)Dp(button, 5), 0);
        TextRenderer.DrawText(graphics, button.Text, button.Font, textBounds, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (focused)
        {
            var focus = Rectangle.Round(bounds);
            focus.Inflate(-(int)Dp(button, 5), -(int)Dp(button, 5));
            ControlPaint.DrawFocusRectangle(graphics, focus, text, bottom);
        }
    }

    internal static void PaintCheckBox(CheckBox button, Graphics graphics, bool focused)
    {
        float size = Dp(button, 15);
        var box = new RectangleF(1.5f, (button.Height - size) / 2, size, size);
        Color fill = button.Checked && button.Enabled ? Rgb(0xB8A4FF) : Field;
        PaintShape(graphics, box, Dp(button, 4), fill, fill, button.Checked ? AccentText : Border);
        if (button.Checked)
        {
            using var check = new Pen(button.Enabled ? Rgb(0x2F214A) : Muted, Dp(button, 1.6f))
                { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            var smoothing = graphics.SmoothingMode;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.DrawLines(check, [new PointF(box.X + size * .23f, box.Y + size * .51f),
                new PointF(box.X + size * .43f, box.Y + size * .70f), new PointF(box.X + size * .77f, box.Y + size * .32f)]);
            graphics.SmoothingMode = smoothing;
        }
        int labelLeft = (int)(box.Right + Dp(button, 8));
        var label = Rectangle.FromLTRB(labelLeft, 1, button.Width, button.Height - 1);
        TextRenderer.DrawText(graphics, button.Text, button.Font, label, button.Enabled ? Ink : Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (focused)
        {
            int measured = TextRenderer.MeasureText(button.Text, button.Font).Width;
            label.Width = Math.Min(label.Width, measured);
            ControlPaint.DrawFocusRectangle(graphics, label, Ink, Field);
        }
    }

    static RectangleF Inside(Control control) => new(.5f, .5f, control.Width - 1f, control.Height - 1f);

    static void PaintShape(Graphics graphics, RectangleF bounds, float radius, Color start, Color end, Color stroke)
    {
        if (bounds.Width < 2 || bounds.Height < 2) return;
        var smoothing = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = Rounded(bounds, radius);
        using var fill = new LinearGradientBrush(bounds, start, end, 76f);
        graphics.FillPath(fill, shape);
        using var outline = new Pen(stroke);
        graphics.DrawPath(outline, shape);

        // The thin inner rim catches the light without sampling anything behind the application.
        bounds.Inflate(-1, -1);
        if (bounds.Width >= 2 && bounds.Height >= 2)
        {
            using var rimShape = Rounded(bounds, radius - 1);
            using var rim = new LinearGradientBrush(bounds, Color.FromArgb(Dark ? 22 : 170, Color.White), Color.Transparent, 78f);
            using var highlight = new Pen(rim);
            graphics.DrawPath(highlight, rimShape);
        }
        graphics.SmoothingMode = smoothing;
    }

    static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        float diameter = Math.Clamp(radius * 2, 1, Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class GlassPage(bool header = false) : Panel
{
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (Glass.Contrast) base.OnPaintBackground(e);
        else Glass.PaintBackdrop(e.Graphics, ClientRectangle, header);
    }
}

internal sealed class GlassCard : TableLayoutPanel
{
    internal GlassCard()
    {
        BackColor = Glass.Contrast ? SystemColors.Window : Color.Transparent;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
        if (!Glass.Contrast) Glass.PaintCard(this, e.Graphics);
    }
}

internal sealed class GlassField : Panel
{
    internal GlassField(TextBox field, string name)
    {
        Height = 39;
        Padding = new Padding(14, 8, 14, 7);
        BackColor = Glass.Contrast ? SystemColors.Window : Color.Transparent;
        BorderStyle = Glass.Contrast ? BorderStyle.FixedSingle : BorderStyle.None;
        field.AccessibleName = name;
        field.BackColor = Glass.Field;
        field.ForeColor = Glass.Ink;
        field.BorderStyle = BorderStyle.None;
        field.Dock = DockStyle.Top;
        Controls.Add(field);
        field.Enter += (_, _) => Invalidate();
        field.Leave += (_, _) => Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
        if (!Glass.Contrast) Glass.PaintField(this, e.Graphics, ContainsFocus);
    }
}

internal sealed class GlassButton : Button
{
    internal bool Primary { get; set; }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Glass.Contrast) base.OnPaint(e);
        else Glass.PaintButton(this, e.Graphics, Primary, Focused && ShowFocusCues);
    }
}

internal sealed class GlassTab : RadioButton
{
    protected override void OnPaint(PaintEventArgs e)
    {
        if (Glass.Contrast) base.OnPaint(e);
        else Glass.PaintButton(this, e.Graphics, Checked, Focused && ShowFocusCues);
    }
}

internal sealed class GlassCheckBox : CheckBox
{
    protected override void OnPaint(PaintEventArgs e)
    {
        if (Glass.Contrast) base.OnPaint(e);
        else Glass.PaintCheckBox(this, e.Graphics, Focused && ShowFocusCues);
    }
}
