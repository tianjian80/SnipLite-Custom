using System.Drawing.Drawing2D;

namespace SnipLite;

/// <summary>
/// Translucent rounded countdown card shown in the top-right corner before a
/// delayed capture. It is a tool window, never appears in the taskbar, and does
/// not take focus so the desktop state stays untouched.
/// </summary>
internal sealed class DelayCountdownForm : ToolWindow
{
    private static Color Card => AppTheme.Current.Card;
    private static Color Ink => AppTheme.Current.Text;
    private static Color Accent => AppTheme.Current.Accent;

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly int _total;
    private int _remaining;
    private float _scale = 1f;

    public event Action? Finished;

    public DelayCountdownForm(int seconds)
    {
        _total = Math.Max(1, seconds);
        _remaining = _total;

        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Card;
        Opacity = 0.92;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

        _timer.Tick += (_, _) => Tick();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE: never steal focus from the desktop
            return cp;
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _scale = DeviceDpi / 96f;
        var size = (int)Math.Round(132 * _scale);
        Size = new Size(size, size);
        var area = Screen.PrimaryScreen?.WorkingArea ?? Screen.GetWorkingArea(Cursor.Position);
        Location = new Point(area.Right - Width - (int)Math.Round(24 * _scale), area.Top + (int)Math.Round(24 * _scale));
        using var path = Rounded(new Rectangle(0, 0, Width, Height), (int)Math.Round(26 * _scale));
        Region = new Region(path);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _timer.Start();
        Invalidate();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        base.OnFormClosed(e);
    }

    private void Tick()
    {
        _remaining--;
        if (_remaining > 0)
        {
            Invalidate();
            return;
        }

        _timer.Stop();
        Hide();
        // Give the compositor one frame to remove the card before freezing the screen.
        var settle = new System.Windows.Forms.Timer { Interval = 120 };
        settle.Tick += (_, _) =>
        {
            settle.Stop();
            settle.Dispose();
            Finished?.Invoke();
            Close();
        };
        settle.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Card);

        var ring = (int)Math.Round(14 * _scale);
        var pad = (int)Math.Round(10 * _scale);
        using (var pen = new Pen(Color.FromArgb(52, Accent), ring))
            g.DrawEllipse(pen, pad + ring / 2f, pad + ring / 2f, Width - pad * 2 - ring, Height - pad * 2 - ring);

        var progress = 1f - (float)(_remaining - 1) / _total;
        using (var arc = new Pen(Accent, ring) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(arc, pad + ring / 2f, pad + ring / 2f, Width - pad * 2 - ring, Height - pad * 2 - ring,
                -90, Math.Max(1, progress * 360));

        using var number = new Font("Microsoft YaHei UI", 44 * _scale, FontStyle.Bold, GraphicsUnit.Pixel);
        using var caption = new Font("Microsoft YaHei UI", 11 * _scale, GraphicsUnit.Pixel);
        var numberBounds = new RectangleF(0, Height / 2f - 44 * _scale, Width, 60 * _scale);
        TextRenderer.DrawText(g, _remaining.ToString(), number, Rectangle.Round(numberBounds), Ink,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        var captionBounds = new RectangleF(0, Height / 2f + 30 * _scale, Width, 22 * _scale);
        TextRenderer.DrawText(g, UiText.T("秒后截屏"), caption, Rectangle.Round(captionBounds),
            Color.FromArgb(170, Ink), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }

    private static GraphicsPath Rounded(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
