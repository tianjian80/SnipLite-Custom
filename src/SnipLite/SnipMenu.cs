using System.Drawing.Drawing2D;

namespace SnipLite;

/// <summary>
/// Theme-aware rounded menus shared by the tray, setting pickers and capture tools.
/// </summary>
internal class SnipMenuRenderer : ToolStripProfessionalRenderer
{
    internal static Color Back => AppTheme.Current.Card;
    internal static Color Hover => AppTheme.Current.Hover;

    public SnipMenuRenderer() : base(new SnipMenuColors())
    {
        RoundedEdges = true;
    }

    /// <summary>
    /// Render callbacks use item-local coordinates, even for rows below a separator.
    /// Fill the complete item so the highlight and its centered text share a row.
    /// </summary>
    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var background = new SolidBrush(Back)) g.FillRectangle(background, new Rectangle(Point.Empty, e.Item.Size));
        if (e.Item.Selected && e.Item.Enabled)
        {
            using var path = FluentDrawing.Rounded(new RectangleF(2, 1, e.Item.Width - 4, e.Item.Height - 2), 5 * (e.ToolStrip?.DeviceDpi ?? 96) / 96f);
            using var brush = new SolidBrush(Hover); g.FillPath(brush, path);
        }
        if (e.Item is ToolStripMenuItem { Checked: false, Tag: string icon } && icon.StartsWith("fluent:", StringComparison.Ordinal))
        {
            var size = 16 * (e.ToolStrip?.DeviceDpi ?? 96) / 96f;
            FluentIcons.Draw(g, icon[7..], new RectangleF(8, (e.Item.Height - size) / 2, size, size), AppTheme.Current.Text);
        }
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? AppTheme.Current.Text : AppTheme.Current.Disabled;
        e.TextRectangle = new Rectangle(e.TextRectangle.X, 0, e.TextRectangle.Width, e.Item.Height);
        e.TextFormat = (e.TextFormat & ~TextFormatFlags.Bottom) | TextFormatFlags.VerticalCenter;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        // A plain dark tick instead of the system's blue checkbox: these menus
        // are light grey strips, and the accent belongs to the annotation bar.
        var box = e.ImageRectangle;
        DrawTick(e.Graphics,
            box.Left + box.Width / 2,
            box.Top + box.Height / 2,
            Math.Max(4, box.Width / 3));
    }

    protected static void DrawTick(Graphics g, int x, int y, int arm)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var pen = new Pen(AppTheme.Current.Accent, 1.6f);
        g.DrawLines(pen, new[]
        {
            new Point(x - arm, y),
            new Point(x - arm / 3, y + arm * 2 / 3),
            new Point(x + arm, y - arm)
        });
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new Pen(AppTheme.Current.Border, 1f);
        var y = e.Item.Height / 2;
        e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(AppTheme.Current.Border, 1f);
        var bounds = new Rectangle(Point.Empty, e.ToolStrip.Size);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = FluentDrawing.Rounded(new RectangleF(.5f, .5f, bounds.Width - 1, bounds.Height - 1), 7);
        e.Graphics.DrawPath(pen, path);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = AppTheme.Current.Text;
        base.OnRenderArrow(e);
    }

    private sealed class SnipMenuColors : ProfessionalColorTable
    {
        private static Color Border => AppTheme.Current.Border;

        public override Color ToolStripDropDownBackground => Back;
        public override Color ImageMarginGradientBegin => Back;
        public override Color ImageMarginGradientMiddle => Back;
        public override Color ImageMarginGradientEnd => Back;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientMiddle => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Back;
        public override Color ButtonSelectedHighlight => Hover;
        public override Color ButtonSelectedHighlightBorder => Border;
    }
}

/// <summary>
/// Dedicated renderer for the settings pickers' drop-down lists: option text is
/// centered across the whole row and the tick hugs the centered text instead of
/// sitting in an image margin. The tray menu keeps using <see cref="SnipMenuRenderer"/>.
/// </summary>
internal sealed class ComboPopupRenderer : SnipMenuRenderer
{
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        var color = e.Item.Enabled ? AppTheme.Current.Text : AppTheme.Current.Disabled;
        var row = new Rectangle(0, 0, e.Item.Width, e.Item.Height);
        TextRenderer.DrawText(e.Graphics, e.Text, e.Item.Font, row, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        if (e.Item is ToolStripMenuItem { Checked: true } item && !string.IsNullOrEmpty(item.Text))
        {
            var dpi = (e.ToolStrip?.DeviceDpi ?? 96) / 96f;
            var textWidth = TextRenderer.MeasureText(item.Text, item.Font).Width;
            var textLeft = Math.Max(0, (e.Item.Width - textWidth) / 2);
            DrawTick(e.Graphics, textLeft - (int)(10 * dpi), e.Item.Height / 2, Math.Max(4, (int)(4 * dpi)));
        }
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        // The tick sits next to the centered label, drawn in OnRenderItemText.
    }
}

/// <summary>
/// Tray menu window styled as a tool window so Windows never adds a taskbar
/// button for it while it is foregrounded to receive outside-click closing.
/// </summary>
internal sealed class TrayContextMenuStrip : ContextMenuStrip
{
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW
            cp.ExStyle &= ~0x00040000;  // WS_EX_APPWINDOW
            return cp;
        }
    }
}

internal static class SnipMenu
{
    public static ContextMenuStrip Create() => Build(combo: false);

    /// <summary>Tray menu: same look, but its window never gets a taskbar button.</summary>
    public static ContextMenuStrip CreateTray() => Build(combo: false, toolWindow: true);

    /// <summary>Drop-down list for a settings picker: centered options, no icon column.</summary>
    public static ContextMenuStrip CreateComboPopup() => Build(combo: true);

    private static ContextMenuStrip Build(bool combo, bool toolWindow = false)
    {
        ContextMenuStrip menu = toolWindow ? new TrayContextMenuStrip() : new ContextMenuStrip();
        menu.Font = new Font("Microsoft YaHei UI", 9f);
        menu.BackColor = SnipMenuRenderer.Back;
        menu.ForeColor = AppTheme.Current.Text;
        menu.ShowImageMargin = false;
        menu.ShowCheckMargin = !combo;
        menu.DropShadowEnabled = true;
        menu.Renderer = combo ? new ComboPopupRenderer() : new SnipMenuRenderer();
        menu.Padding = new Padding(4);
        void ApplyTheme() { if (!menu.IsDisposed) { menu.BackColor = SnipMenuRenderer.Back; menu.ForeColor = AppTheme.Current.Text; menu.Invalidate(true); } }
        AppTheme.Changed += ApplyTheme;
        menu.Disposed += (_, _) => AppTheme.Changed -= ApplyTheme;
        menu.Opening += (_, _) => { ApplyTheme(); UiText.Apply(menu); };
        return menu;
    }

    public static ToolStripMenuItem Item(string text, Action action, string shortcut = "", bool check = false)
    {
        var item = new ToolStripMenuItem(text)
        {
            CheckOnClick = false,
            Checked = check,
            TextAlign = ContentAlignment.MiddleLeft,
            Tag = "fluent:" + FluentIcons.ForLabel(text),
            Padding = new Padding(0, 5, 0, 5)
        };

        if (shortcut.Length > 0)
            item.ShortcutKeyDisplayString = shortcut;

        item.Click += (_, _) => action();
        Localization.LocalizedControls.Apply(item);
        return item;
    }
}
