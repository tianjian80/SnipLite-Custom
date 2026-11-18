using System.Drawing.Drawing2D;

namespace SnipLite;

/// <summary>
/// Borderless floating tool bar that rides just below the long-image editor window.
/// It is a separate top-level window, so the editor client area stays pure image, and
/// it follows the editor's move / resize while scaling its own buttons with the width.
/// </summary>
internal sealed class EditorToolbarForm : Form
{
    private readonly LongEditForm _editor;
    private readonly AnnotationBar _bar;
    private readonly float _ui;
    private readonly ToolTip _tip = new()
    {
        InitialDelay = 250,
        ReshowDelay = 80,
        AutoPopDelay = 5000,
        // The card is an owned, never-activated window: without this the tooltip would
        // be suppressed whenever the editor is not the active window.
        ShowAlways = true
    };
    private string? _tipId;
    private bool _laying;

    private const int Gap = 6;
    private const float MinScale = 0.7f;
    private const float MaxScale = 2f;

    public EditorToolbarForm(LongEditForm editor, AnnotationBar bar, float ui)
    {
        _editor = editor;
        _bar = bar;
        _ui = ui;
        _bar.Flat = true; // one single white card, no nested bordered frame

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = AppTheme.Current.Card;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        DoubleBuffered = true;

        Width = Math.Max((int)(300 * _ui), editor.Width);
        Height = (int)(64 * _ui);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW, the only "edge" the card gets
            return cp;
        }
    }

    /// <summary>The card is a pure overlay: it never resizes itself, the editor drives it.</summary>
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0084) // WM_NCHITTEST
        {
            m.Result = new IntPtr(1); // HTCLIENT everywhere: no frame, no resize cursors
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = FluentDrawing.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), (int)Math.Round(12 * _ui)))
        {
            using var fill = new SolidBrush(AppTheme.Current.Card);
            g.FillPath(fill, path);
        }
        _bar.CanUndo = _editor.CanUndo;
        _bar.CanRedo = _editor.CanRedo;
        _bar.Render(g);
    }

    /// <summary>
    /// Takes the natural single-row width of every visible button as the reference and
    /// keeps the card at that width even at the smallest allowed scale, so the editor can
    /// never shrink to a point where the bar has to wrap or squeeze its icons.
    /// </summary>
    public void Prepare()
    {
        var pad = (int)Math.Round(10 * _ui);
        _bar.Scale = _ui;
        _bar.WrapWidth = 4000;
        _bar.Layout(new Rectangle(0, 0, 4000, 0), new Size(4000, 4000));
        var single = _bar.Bounds.Height;

        _bar.Scale = _ui * 0.8f;
        _bar.Layout(new Rectangle(0, 0, 4000, 0), new Size(4000, 4000));
        var minWidth = _bar.Bounds.Width + pad * 2;
        MinimumSize = new Size(minWidth, single);
        LayoutBar();
        EnsureRegion();
    }

    /// <summary>
    /// Lays the icon row out for the card width, scaling the buttons so the row fills the
    /// card evenly: widening the window grows icons and gaps, narrowing compacts them,
    /// and the row stays centred with no hollow ends.
    /// </summary>
    private void Measure(int width)
    {
        var pad = (int)Math.Round(10 * _ui);
        var content = Math.Max(1, width - pad * 2);

        _bar.Scale = _ui;
        _bar.WrapWidth = 4000;
        _bar.Layout(new Rectangle(0, 0, 4000, 0), new Size(4000, 4000));
        var natural = Math.Max(1, _bar.Bounds.Width);

        _bar.Scale = _ui * Math.Clamp(content / (float)natural, MinScale, MaxScale);
        _bar.WrapWidth = 4000; // one single row
        var lift = (int)Math.Round(10 * _bar.Scale);
        _bar.Layout(new Rectangle(pad, pad - lift, content, 0), new Size(4000, 4000));
    }

    /// <summary>Shapes the window like the rounded card: no stray rectangular backplate.</summary>
    private void EnsureRegion()
    {
        if (Width < 8 || Height < 8) return;
        using var path = FluentDrawing.Rounded(new Rectangle(0, 0, Width, Height), (int)Math.Round(12 * _ui));
        var old = Region;
        Region = new Region(path);
        old?.Dispose();
    }

    /// <summary>Sizes the card to the editor width so both windows align edge to edge.</summary>
    private void LayoutBar()
    {
        if (_laying) return;
        _laying = true;
        try
        {
            var width = Math.Max(1, _editor.Width);
            Measure(width);
            var pad = (int)Math.Round(10 * _ui);
            var h = Math.Max((int)Math.Round(52 * _ui), _bar.Bounds.Bottom + pad);
            if (Width == width && Height == h) return;
            Width = width;
            Height = h;
            EnsureRegion();
        }
        finally
        {
            _laying = false;
        }
    }

    /// <summary>
    /// Pins the card directly under the editor: same left edge, same width, 6 px below.
    /// The editor is the only thing that moves or resizes, the toolbar just follows.
    /// </summary>
    public void SyncToOwner()
    {
        if (!IsHandleCreated) return;
        LayoutBar();
        var left = _editor.Left;
        var top = _editor.Top + _editor.Height + (int)Math.Round(Gap * _ui);
        if (Location != new Point(left, top)) Location = new Point(left, top);
    }

    public void RefreshBar()
    {
        LayoutBar();
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_laying) return;
        Invalidate();
    }

    // ------------------------------------------------------------ bar input

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        var id = _bar.HitTest(e.Location);
        if (id is null) return;
        _editor.Command(id, e.Location);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_editor.SliderId is { } slider)
        {
            _editor.BarSlider(slider, e.Location);
            return;
        }
        Cursor = _bar.HitTest(e.Location) is null ? Cursors.Default : Cursors.Hand;
        ShowTip(e.Location);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _editor.BarSliderEnd();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _tip.Hide(this);
        _tipId = null;
        _editor.BarSliderEnd();
    }

    /// <summary>Hover hint with the Chinese tool name and its shortcut.</summary>
    private void ShowTip(Point point)
    {
        var id = _bar.HitTest(point);
        if (id == _tipId) return;
        _tipId = id;
        var text = id is null ? "" : AnnotationBar.Hint(id, false);
        _tip.Hide(this);
        if (string.IsNullOrEmpty(text))
        {
            _tip.SetToolTip(this, "");
            return;
        }
        _tip.SetToolTip(this, text);
        _tip.Show(text, this, point.X + (int)Math.Round(16 * _ui), point.Y + (int)Math.Round(24 * _ui), 5000);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _tip.Dispose();
        base.OnFormClosed(e);
    }
}
