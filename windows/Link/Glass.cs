using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ShuangpinLink;

internal static class Glass
{
    internal static bool Contrast { get; } = SystemInformation.HighContrast;
    internal static bool Dark { get; } = !Contrast && UsesDarkTheme();
    internal const ControlStyles Buffered = ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
        ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw;
    static readonly bool PressMotion = !Contrast && SystemParametersInfoW(0x1042, 0, out int animation, 0) && animation != 0;

    internal static Color Surface => Token(0xF4F3F1, 0x191A1C, SystemColors.Control);
    internal static Color Ink => Token(0x242527, 0xF4F3EF, SystemColors.WindowText);
    internal static Color Muted => Token(0x5C5B59, 0xBDBBB7, SystemColors.WindowText);
    internal static Color AccentText => Token(0x4B4741, 0xE1DED8, SystemColors.WindowText);
    internal static Color Tint => Token(0xE6E4DF, 0x474747, SystemColors.Control);
    internal static Color Field => Token(0xFCFBF9, 0x292A2C, SystemColors.Window);
    internal static Color Border => Token(0xC7C6C2, 0x535354, SystemColors.WindowText);

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
        Color start = header ? Token(0xFEFDFC, 0x363637, Surface) : Token(0xF9F8F6, 0x27282A, Surface);
        using var wash = new LinearGradientBrush(bounds, start, Surface, 51f);
        graphics.FillRectangle(wash, bounds);
        using var glowShape = new GraphicsPath();
        glowShape.AddEllipse(-bounds.Width / 2, -bounds.Height / 2, bounds.Width * 2, bounds.Height * 2);
        using var glow = new PathGradientBrush(glowShape)
        {
            CenterPoint = new PointF(bounds.Width * .16f, bounds.Height * .1f),
            CenterColor = Color.FromArgb(Dark ? 28 : 80, Rgb(0xD8D7D2)),
            SurroundColors = [Color.Transparent],
        };
        graphics.FillRectangle(glow, bounds);
    }

    internal static void PaintCard(Control control, Graphics graphics) => PaintShape(graphics, Inside(control), Dp(control, 22),
        Color.FromArgb(185, Token(0xFFFEFD, 0x363638, Field)), Color.FromArgb(160, Token(0xEAE9E6, 0x242527, Field)), Border);

    internal static void PaintField(Control control, Graphics graphics, bool focused) => PaintShape(graphics, Inside(control), Dp(control, 12),
        Field, Field, focused ? AccentText : Border);

    internal static void PaintButton(ButtonBase button, Graphics graphics, bool prominent, bool focused)
    {
        bool hover = button.Enabled && button.ClientRectangle.Contains(button.PointToClient(Cursor.Position));
        bool pressed = hover && button.Capture && (Control.MouseButtons & MouseButtons.Left) != 0;
        Color top = prominent ? Rgb(0xF5F4F0) : Token(0xFFFFFF, 0x404042, Field);
        Color bottom = prominent ? Rgb(0xD8D7D2) : Token(0xEDECE9, 0x303133, Tint);
        if (hover) bottom = prominent ? Rgb(0xE7E6E1) : Tint;
        if (pressed) bottom = prominent ? Rgb(0xC6C5BE) : Token(0xD9D8D4, 0x535355, Tint);
        var bounds = Inside(button);
        if (pressed && PressMotion) bounds.Inflate(-bounds.Width * .023f, -bounds.Height * .023f);
        PaintShape(graphics, bounds, Dp(button, 14), top, bottom,
            focused ? AccentText : prominent ? Rgb(0xA7A69F) : Border);

        Color text = !button.Enabled ? Muted : prominent ? Rgb(0x242527) : Ink;
        var textBounds = Rectangle.Round(bounds);
        textBounds.Inflate(-(int)Dp(button, 5), 0);
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        if (button is GlassTab tab)
        {
            int gap = (int)Dp(button, 7);
            int size = (int)Dp(button, 15);
            int labelWidth = TextRenderer.MeasureText(button.Text, button.Font, Size.Empty, flags | TextFormatFlags.NoPadding).Width;
            int x = Math.Max(textBounds.Left, textBounds.Left + (textBounds.Width - labelWidth - size - gap) / 2);
            PaintGlyph(graphics, new Rectangle(x, (button.Height - size) / 2, size, size), text, tab.Glyph);
            textBounds.X = x + size + gap;
            textBounds.Width = (int)bounds.Right - textBounds.X - gap;
            flags |= TextFormatFlags.Left | TextFormatFlags.NoPadding;
        }
        else flags |= TextFormatFlags.HorizontalCenter;
        TextRenderer.DrawText(graphics, button.Text, button.Font, textBounds, text, flags);
        if (focused)
        {
            var focus = Rectangle.Round(bounds);
            focus.Inflate(-(int)Dp(button, 5), -(int)Dp(button, 5));
            ControlPaint.DrawFocusRectangle(graphics, focus, text, bottom);
        }
    }

    static void PaintGlyph(Graphics graphics, Rectangle box, Color color, GlassGlyph glyph)
    {
        var state = graphics.Save();
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TranslateTransform(box.Left, box.Top);
        graphics.ScaleTransform(box.Width / 16f, box.Height / 16f);
        using var pen = new Pen(color, 1.35f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        switch (glyph)
        {
            case GlassGlyph.Input:
                graphics.DrawRectangle(pen, 1, 3, 14, 10);
                for (int x = 4; x <= 12; x += 4) graphics.DrawLine(pen, x, 6, x + .15f, 6);
                graphics.DrawLine(pen, 5, 10, 11, 10);
                break;
            case GlassGlyph.Devices:
                graphics.DrawArc(pen, 1, 2, 9, 9, 62, 295);
                graphics.DrawArc(pen, 6, 5, 9, 9, 242, 295);
                break;
            case GlassGlyph.Update:
                graphics.DrawLine(pen, 8, 1.5f, 8, 10);
                graphics.DrawLines(pen, [new PointF(4, 6.5f), new PointF(8, 10.5f), new PointF(12, 6.5f)]);
                graphics.DrawLines(pen, [new PointF(1.5f, 10), new PointF(1.5f, 14), new PointF(14.5f, 14), new PointF(14.5f, 10)]);
                break;
        }
        graphics.Restore(state);
    }

    internal static void PaintCheckBox(CheckBox button, Graphics graphics, bool focused)
    {
        float size = Dp(button, 15);
        var box = new RectangleF(1.5f, (button.Height - size) / 2, size, size);
        Color fill = button.Checked && button.Enabled ? Rgb(0xD8D7D2) : Field;
        PaintShape(graphics, box, Dp(button, 4), fill, fill, button.Checked ? AccentText : Border);
        if (button.Checked)
        {
            using var check = new Pen(button.Enabled ? Rgb(0x242527) : Muted, Dp(button, 1.6f))
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
        using var raisedEdge = new LinearGradientBrush(bounds, Color.FromArgb(Dark ? 68 : 247, Color.White), stroke, 78f);
        using var outline = new Pen(raisedEdge);
        graphics.DrawPath(outline, shape);
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

internal enum GlassGlyph { Input, Devices, Update }

internal sealed class GlassPage : Panel
{
    readonly bool header;
    Bitmap? backdrop;

    internal GlassPage(bool header = false)
    {
        this.header = header;
        SetStyle(Glass.Buffered, true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (Glass.Contrast || ClientSize.Width == 0 || ClientSize.Height == 0)
        {
            base.OnPaintBackground(e);
            return;
        }
        if (backdrop?.Size != ClientSize)
        {
            backdrop?.Dispose();
            backdrop = new Bitmap(ClientSize.Width, ClientSize.Height);
            using var graphics = Graphics.FromImage(backdrop);
            Glass.PaintBackdrop(graphics, ClientRectangle, header);
        }
        e.Graphics.DrawImageUnscaled(backdrop, 0, 0);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) backdrop?.Dispose();
        base.Dispose(disposing);
    }
}

internal sealed class GlassCard : TableLayoutPanel
{
    internal GlassCard()
    {
        SetStyle(Glass.Buffered, true);
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
        SetStyle(Glass.Buffered, true);
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
    internal GlassButton() => SetStyle(Glass.Buffered, true);

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Glass.Contrast) base.OnPaint(e);
        else Glass.PaintButton(this, e.Graphics, Primary, Focused && ShowFocusCues);
    }
}

internal sealed class GlassTab : RadioButton
{
    internal GlassGlyph Glyph { get; init; }
    internal GlassTab() => SetStyle(Glass.Buffered, true);

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Glass.Contrast) base.OnPaint(e);
        else Glass.PaintButton(this, e.Graphics, Checked, Focused && ShowFocusCues);
    }
}

internal sealed class GlassCheckBox : CheckBox
{
    internal GlassCheckBox() => SetStyle(Glass.Buffered, true);

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Glass.Contrast) base.OnPaint(e);
        else Glass.PaintCheckBox(this, e.Graphics, Focused && ShowFocusCues);
    }
}
