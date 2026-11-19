using System.Drawing.Drawing2D;

namespace SnipLite;

internal static class FluentDrawing
{
    public static GraphicsPath Rounded(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Max(1, Math.Min(radius * 2, Math.Min(rect.Width, rect.Height)));
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>Keeps native text editing and number validation inside a shared Fluent field.</summary>
internal sealed class FluentInputFrame : UserControl
{
    private readonly Control _editor;
    private readonly Panel _clip = new();
    private readonly NumericUpDown? _number;
    private bool _hover;
    private int _hoverStep;

    /// <summary>Grey unit suffix painted inside the field, e.g. "Mbps".</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal string? Suffix { get; set; }

    /// <summary>
    /// Drops the frame entirely: the field then lives inside an outer capsule and only
    /// paints its content (plus a focus underline), so two cells share one outline.
    /// </summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool Bare { get; set; }

    /// <summary>Space kept for the numeric spinner; shrink it for narrow fields.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal int SpinnerReserve { get; set; } = 54;

    /// <summary>Narrow variant: hides the native spinner and draws compact +/- buttons instead.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool Compact { get; set; }

    /// <summary>Custom −/+ stepper for plain text fields; replaces the native numeric spinner.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Action<int>? Step { get; set; }

    public FluentInputFrame(Control editor)
    {
        var editorWidth = editor.Width;
        _editor = editor;
        _number = editor as NumericUpDown;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        ResizeRedraw = true;
        TabStop = false;
        AccessibleName = editor.AccessibleName;
        Size = new Size(editorWidth, (int)Math.Round(32 * editor.DeviceDpi / 96f));
        if (editor is TextBoxBase text) text.BorderStyle = BorderStyle.None;
        if (_number is not null) _number.BorderStyle = BorderStyle.None;
        Controls.Add(_clip);
        _clip.Controls.Add(editor);
        // The clip panel swallows clicks, so hand focus to the editor explicitly.
        _clip.Cursor = Cursors.IBeam;
        _clip.MouseDown += (_, _) => _editor.Focus();
        editor.Margin = Padding.Empty;
        editor.Enter += EditorStateChanged;
        editor.Leave += EditorStateChanged;
        editor.MouseEnter += EditorMouseEnter;
        editor.MouseLeave += EditorMouseLeave;
        editor.FontChanged += EditorFontChanged;
        if (_number is not null) _number.ValueChanged += EditorStateChanged;
        PerformLayout();
    }

    /// <summary>Puts the caret in the editable area, e.g. right after the field appears.</summary>
    internal void FocusEditor()
    {
        if (!_editor.CanFocus) return;
        _editor.Focus();
    }

    private void EditorStateChanged(object? sender, EventArgs e) => Invalidate();
    private void EditorMouseEnter(object? sender, EventArgs e) { _hover = true; Invalidate(); }
    private void EditorMouseLeave(object? sender, EventArgs e) { _hover = false; Invalidate(); }
    private void EditorFontChanged(object? sender, EventArgs e) => PerformLayout();
    private int Px(float value) => (int)Math.Round(value * DeviceDpi / 96f);
    private Rectangle StepBounds(int step)
    {
        var size = Px(Compact ? 21 : 25);
        var gap = Px(Compact ? 3 : 1);
        var right = Px(3);
        var left = Width - right - (step < 0 ? size * 2 + gap : size);
        return new Rectangle(left, Px(3), size, Height - Px(6));
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (_editor is null || _editor.Parent != _clip || _clip.Parent != this) return;
        var hasStepper = _number is not null || Step is not null;
        var reserve = hasStepper ? Px(SpinnerReserve)
            : Suffix is null ? 0 : TextRenderer.MeasureText(Suffix, Font).Width + Px(4);
        var editorHeight = _editor.PreferredSize.Height;
        _clip.Bounds = new Rectangle(Px(8), Math.Max(Px(3), (Height - editorHeight) / 2), Math.Max(1, Width - Px(14) - reserve), editorHeight);
        // Clip the native spinner; the editor still handles typing, arrows, wheel and validation.
        var spinner = Compact ? 0
            : _number?.Controls.Cast<Control>().FirstOrDefault(c => c is not TextBoxBase)?.Width ?? 0;
        _editor.Bounds = new Rectangle(0, 0, _clip.Width + spinner, editorHeight);
    }

    private void PaintSuffix(Graphics g)
    {
        if (Suffix is null) return;
        var width = TextRenderer.MeasureText(Suffix, Font).Width;
        TextRenderer.DrawText(g, Suffix, Font,
            new Rectangle(Width - width - Px(8), 0, width, Height), AppTheme.Current.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPrefix);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _hover = true;
        var step = _number is null && Step is null ? 0 : StepBounds(-1).Contains(e.Location) ? -1 : StepBounds(1).Contains(e.Location) ? 1 : 0;
        if (_hoverStep != step) { _hoverStep = step; Invalidate(); }
        Cursor = step == 0 ? Cursors.IBeam : Cursors.Hand;
    }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; _hoverStep = 0; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _editor.Focus();
        if (_number is null)
        {
            if (Step is not null)
            {
                if (StepBounds(-1).Contains(e.Location)) Step(-1);
                else if (StepBounds(1).Contains(e.Location)) Step(1);
            }
            return;
        }
        if (StepBounds(-1).Contains(e.Location)) _number.DownButton();
        else if (StepBounds(1).Contains(e.Location)) _number.UpButton();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var p = AppTheme.Current;
        var glass = GlassPaint.UnderWallpaper(this);
        if (glass && !Bare) GlassPaint.PaintBackdrop(this, e.Graphics, ClientRectangle);
        else e.Graphics.Clear(Parent?.BackColor ?? p.Card);
        if (Bare)
        {
            // Frame is drawn by the surrounding capsule.
            if (ContainsFocus)
            {
                using var underline = new Pen(p.Accent, Math.Max(1, Px(2)));
                e.Graphics.DrawLine(underline, Px(8), Height - Px(1), Width - Px(8), Height - Px(1));
            }
            if (Suffix is not null) PaintSuffix(e.Graphics);
            return;
        }
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        // Inset a full pixel so the 1 px rounded edge stays inside the client area.
        using var path = FluentDrawing.Rounded(new RectangleF(1f, 1f, Width - 2, Height - 2), Px(5));
        using var fill = new SolidBrush(glass ? ThemeManager.GlassTint : p.Field); e.Graphics.FillPath(fill, path);
        using var edge = new Pen(ContainsFocus ? p.Accent : glass ? (_hover ? p.Accent : ThemeManager.GlassEdge) : _hover ? p.Muted : p.Border); e.Graphics.DrawPath(edge, path);
        if (ContainsFocus)
        {
            using var accent = new Pen(p.Accent, Math.Max(1, Px(2)));
            e.Graphics.DrawLine(accent, Px(5), Height - Px(1), Width - Px(5), Height - Px(1));
        }
        if (Suffix is not null) PaintSuffix(e.Graphics);
        if (_number is null && Step is null) return;
        foreach (var step in new[] { -1, 1 })
        {
            var bounds = StepBounds(step);
            var enabled = Enabled && (_number is null
                || (step < 0 ? _number.Value > _number.Minimum : _number.Value < _number.Maximum));
            if (_hoverStep == step && enabled)
            {
                using var hoverPath = FluentDrawing.Rounded(bounds, Px(3));
                using var hover = new SolidBrush(p.Hover); e.Graphics.FillPath(hover, hoverPath);
            }
            using var pen = new Pen(enabled ? p.Text : p.Disabled, Math.Max(1, DeviceDpi / 96f));
            var x = bounds.Left + bounds.Width / 2; var y = bounds.Top + bounds.Height / 2;
            var arm = Compact ? Math.Max(Px(6), bounds.Width / 2 - Px(2)) : Px(4);
            e.Graphics.DrawLine(pen, x - arm, y, x + arm, y);
            if (step > 0) e.Graphics.DrawLine(pen, x, y - arm, x, y + arm);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _editor.Enter -= EditorStateChanged; _editor.Leave -= EditorStateChanged;
            _editor.MouseEnter -= EditorMouseEnter; _editor.MouseLeave -= EditorMouseLeave;
            _editor.FontChanged -= EditorFontChanged;
            if (_number is not null) _number.ValueChanged -= EditorStateChanged;
        }
        base.Dispose(disposing);
    }
}

internal sealed class FluentButton : Button
{
    private bool _hover;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool DropDown { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool Selected { get; set; }

    /// <summary>
    /// Paints only the caption and the chevron: the surrounding capsule supplies the
    /// outline, so a picker and a field can share one rounded frame.
    /// </summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool Segment { get; set; }
    public FluentButton()
    {
        FlatStyle = FlatStyle.Flat; UseVisualStyleBackColor = false;
        ResizeRedraw = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }
    protected override bool ShowFocusCues => false;
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var p = AppTheme.Current; var s = DeviceDpi / 96f; var g = e.Graphics;
        var glass = !Segment && !(Tag as string is "color-swatch" or "accent-button") && GlassPaint.UnderWallpaper(this);
        if (glass) GlassPaint.PaintBackdrop(this, g, ClientRectangle);
        else g.Clear(Parent?.BackColor ?? p.Window);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (Segment)
        {
            var caption = new Rectangle((int)(9 * s), 0, Math.Max(1, Width - (int)(29 * s)), Height);
            TextRenderer.DrawText(g, Text, Font, caption, Enabled ? ForeColor : p.Disabled,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (DropDown) FluentIcons.Draw(g, "chevron", new RectangleF(Width - 21 * s, (Height - 16 * s) / 2, 16 * s, 16 * s), p.Muted);
            return;
        }
        // Accent buttons (colour pickers, icon picker, dialog OK) light up gold on hover;
        // painting them here is mandatory because OnPaint ignores FlatAppearance.
        var accent = Tag as string is "color-swatch" or "accent-button";
        var onGold = accent && _hover && Enabled;
        Color? glassFill = glass ? (_hover ? Color.FromArgb(150, 255, 255, 255) : ThemeManager.GlassTint) : null;
        var surface = Selected ? p.AccentSoft
            : _hover ? (accent ? Color.FromArgb(245, 184, 0) : glassFill!.Value)
            : glassFill ?? BackColor;
        using var path = FluentDrawing.Rounded(new RectangleF(1f, 1f, Width - 2, Height - 2), 5 * s);
        using var fill = new SolidBrush(surface); g.FillPath(fill, path);
        using var edge = new Pen(Selected || (glass && _hover) ? p.Accent : glass ? ThemeManager.GlassEdge : p.Border); g.DrawPath(edge, path);
        var text = Rectangle.Inflate(ClientRectangle, -(int)(9 * s), 0);
        if (DropDown) text.Width -= (int)(20 * s);
        var alignment = TextAlign == ContentAlignment.MiddleLeft ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter;
        var ink = onGold ? Color.FromArgb(26, 26, 26) : ForeColor;
        TextRenderer.DrawText(g, Text, Font, text, Enabled ? Selected ? p.Accent : ink : p.Disabled,
            alignment | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (DropDown) FluentIcons.Draw(g, "chevron", new RectangleF(Width - 23 * s, (Height - 16 * s) / 2, 16 * s, 16 * s), p.Muted);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -4, -4), p.Text, surface);
    }
}

/// <summary>Link label that never paints the dotted focus rectangle around its text.</summary>
internal sealed class FluentLinkLabel : LinkLabel
{
    protected override bool ShowFocusCues => false;
}

/// <summary>
/// Outer rounded capsule that supplies the single outline for a pair of borderless
/// cells, so a picker plus a numeric field read as one box that is exactly as wide as
/// the plain pickers in the rows above and below.
/// </summary>
internal sealed class CapsuleFrame : Panel
{
    public CapsuleFrame()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = AppTheme.Current.Field;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    private int Px(float value) => (int)Math.Round(value * DeviceDpi / 96f);

    /// <summary>Lets an interactive capsule tint itself while the pointer is over it.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool HoverHighlight { get; set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool Hovered
    {
        get => _hovered;
        set
        {
            if (_hovered == value) return;
            _hovered = value;
            if (HoverHighlight) Invalidate();
        }
    }
    private bool _hovered;

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = AppTheme.Current;
        var g = e.Graphics;
        var glass = GlassPaint.UnderWallpaper(this);
        if (glass) GlassPaint.PaintBackdrop(this, g, ClientRectangle);
        else g.Clear(Parent?.BackColor ?? p.Card);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = FluentDrawing.Rounded(new RectangleF(1f, 1f, Width - 2, Height - 2), Px(5));
        using var fill = new SolidBrush(glass
            ? (HoverHighlight && Hovered ? Color.FromArgb(150, 255, 255, 255) : ThemeManager.GlassTint)
            : (HoverHighlight && Hovered ? p.Hover : p.Field));
        g.FillPath(fill, path);
        using var edge = new Pen(glass ? ThemeManager.GlassEdge : p.Border);
        g.DrawPath(edge, path);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (Width <= 0 || Height <= 0) return;
        // Clip the borderless cells to the rounded outline.
        using var path = FluentDrawing.Rounded(new RectangleF(0, 0, Width, Height), Px(5));
        var old = Region;
        Region = new Region(path);
        old?.Dispose();
    }
}

internal sealed class FluentToggle : CheckBox
{
    public FluentToggle()
    {
        ResizeRedraw = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        AccessibleRole = AccessibleRole.CheckButton;
    }
    protected override bool ShowFocusCues => false;
    protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var p = AppTheme.Current; var s = DeviceDpi / 96f; var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? p.Card); g.SmoothingMode = SmoothingMode.AntiAlias;
        var track = new RectangleF(Width - 39 * s, (Height - 20 * s) / 2, 36 * s, 20 * s);
        using var path = FluentDrawing.Rounded(track, 10 * s);
        using var fill = new SolidBrush(Checked ? p.Accent : p.Field); g.FillPath(fill, path);
        using var edge = new Pen(Checked ? p.Accent : p.Muted); g.DrawPath(edge, path);
        using var knob = new SolidBrush(Checked ? p.OnAccent : p.Muted);
        g.FillEllipse(knob, track.X + (Checked ? 20 : 4) * s, track.Y + 4 * s, 12 * s, 12 * s);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Math.Max(0, Width - (int)(51 * s)), Height),
            Enabled ? p.Text : p.Disabled, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -2, -2), p.Text, p.Card);
    }
}

/// <summary>Hint line that floats on the page: white with a soft shadow over a wallpaper.</summary>
internal sealed class FluentHintLabel : Label
{
    public FluentHintLabel()
    {
        AutoSize = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var wallpaper = ThemeManager.HasWallpaper;
        if (wallpaper && Parent is IWallpaperBackdrop backdrop)
        {
            backdrop.PaintWallpaperFor(g, ClientRectangle, Location);
        }
        else
        {
            using var brush = new SolidBrush(Parent?.BackColor ?? AppTheme.Current.Window);
            g.FillRectangle(brush, ClientRectangle);
        }
        const TextFormatFlags flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
        if (wallpaper)
        {
            TextRenderer.DrawText(g, Text, Font,
                new Rectangle(ClientRectangle.X + 1, ClientRectangle.Y + 1, Width, Height),
                Color.FromArgb(150, 0, 0, 0), flags);
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, Color.White, flags);
        }
        else
        {
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, AppTheme.Current.Muted, flags);
        }
    }
}

/// <summary>Lets a translucent card paint the wallpaper slice that sits beneath it.</summary>
internal interface IWallpaperBackdrop
{
    void PaintWallpaperFor(Graphics g, Rectangle clip, Point childInPage);
}

/// <summary>Shared painting helpers for the frosted-glass look inside cards.</summary>
internal static class GlassPaint
{
    public static bool UnderWallpaper(Control control)
    {
        if (!ThemeManager.HasWallpaper) return false;
        for (var parent = control.Parent; parent is not null; parent = parent.Parent)
            if (parent is IWallpaperBackdrop) return true;
        return false;
    }

    /// <summary>Draws the wallpaper slice behind a control so an alpha fill reads as glass.</summary>
    public static void PaintBackdrop(Control control, Graphics g, Rectangle clip)
    {
        for (var parent = control.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is not IWallpaperBackdrop backdrop) continue;
            Point at;
            try { at = parent.PointToClient(control.PointToScreen(Point.Empty)); }
            catch { at = control.Location; }
            backdrop.PaintWallpaperFor(g, clip, at);
            return;
        }
    }
}

internal sealed class FluentSettingRow : Panel
{
    public FluentSettingRow()
    {
        DoubleBuffered = true; ResizeRedraw = true; BackColor = AppTheme.Current.Card;
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
    }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var s = DeviceDpi / 96f; var p = AppTheme.Current;
        var glass = ThemeManager.HasWallpaper && Parent is IWallpaperBackdrop;
        // Inset a full pixel for both fill and stroke so the 1px border is never
        // clipped by the card's own edge, even when siblings sit right above it.
        if (glass) ((IWallpaperBackdrop)Parent!).PaintWallpaperFor(e.Graphics, ClientRectangle, Location);
        else e.Graphics.Clear(Parent?.BackColor ?? p.Window);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = FluentDrawing.Rounded(new RectangleF(1f, 1f, Width - 2, Height - 2), 7 * s);
        using var fill = new SolidBrush(glass ? Color.FromArgb(185, 255, 255, 255) : p.Card);
        e.Graphics.FillPath(fill, path);
        using var border = new Pen(glass ? Color.FromArgb(210, 255, 255, 255) : p.Border);
        e.Graphics.DrawPath(border, path);
    }
}
