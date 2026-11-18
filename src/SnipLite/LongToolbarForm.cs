namespace SnipLite;

/// <summary>
/// Minimal white actions card that floats above the long-capture selection:
/// 编辑 / ↓ 保存 / × 取消 / ✓ 完成. All four buttons are laid out with measured widths,
/// so nothing is ever clipped.
/// </summary>
internal sealed class LongToolbarForm : ToolWindow
{
    private readonly LongCaptureForm _owner;
    private readonly float _ui;
    private readonly Font _font;
    private readonly string[] _labels = { "编辑", "↓", "×", "✓ 完成" };
    private readonly Rectangle[] _cells;
    private int _hot = -1;

    public LongToolbarForm(LongCaptureForm owner, float ui)
    {
        _owner = owner;
        _ui = ui;
        _font = new Font("Microsoft YaHei UI", 9f * Math.Max(1f, ui), FontStyle.Regular, GraphicsUnit.Point);

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        DoubleBuffered = true;
        BackColor = Color.White;

        var padding = (int)Math.Round(10 * ui);
        var gap = (int)Math.Round(6 * ui);
        var height = (int)Math.Round(38 * ui);
        var widths = new int[_labels.Length];
        for (var i = 0; i < _labels.Length; i++)
        {
            var text = TextRenderer.MeasureText(_labels[i], _font);
            widths[i] = Math.Max((int)Math.Round(54 * ui), text.Width + (int)Math.Round(22 * ui));
        }
        var width = padding * 2 + widths.Sum() + gap * (widths.Length - 1);
        width = Math.Max(width, (int)Math.Round(264 * ui));

        // Spread any extra room evenly so the four buttons stay balanced.
        var extra = (width - padding * 2 - gap * (widths.Length - 1) - widths.Sum()) / widths.Length;
        _cells = new Rectangle[widths.Length];
        var x = padding;
        for (var i = 0; i < widths.Length; i++)
        {
            _cells[i] = new Rectangle(x, padding / 2, widths[i] + extra, height - padding);
            x += widths[i] + extra + gap;
        }

        Size = new Size(width, height);
        Place();
    }

    private void Place()
    {
        var screen = Screen.FromRectangle(_owner.CaptureRegion).WorkingArea;
        var sel = _owner.CaptureRegion;
        var gap = (int)Math.Round(8 * _ui);
        var left = sel.Left + (sel.Width - Width) / 2;
        var top = sel.Top - Height - gap;
        if (top < screen.Top) top = sel.Bottom + gap;
        left = Math.Clamp(left, screen.Left, Math.Max(screen.Left, screen.Right - Width));
        top = Math.Clamp(top, screen.Top, Math.Max(screen.Top, screen.Bottom - Height));
        Location = new Point(left, top);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var card = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = FluentDrawing.Rounded(card, (int)Math.Round(10 * _ui)))
        {
            using var fill = new SolidBrush(Color.FromArgb(254, 255, 255, 255));
            g.FillPath(fill, path);
            using var border = new Pen(Color.FromArgb(222, 228, 234));
            g.DrawPath(border, path);
        }

        for (var i = 0; i < _cells.Length; i++)
        {
            if (i == _hot)
            {
                using var hot = FluentDrawing.Rounded(_cells[i], (int)Math.Round(8 * _ui));
                using var brush = new SolidBrush(Color.FromArgb(238, 242, 246));
                g.FillPath(brush, hot);
            }
            var color = i == 3 ? Color.FromArgb(30, 130, 90) : Color.FromArgb(34, 34, 34);
            TextRenderer.DrawText(g, _labels[i], _font, _cells[i], color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    private int Hit(Point p)
    {
        for (var i = 0; i < _cells.Length; i++)
            if (_cells[i].Contains(p)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hot = Hit(e.Location);
        if (hot == _hot) return;
        _hot = hot;
        Cursor = hot >= 0 ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hot == -1) return;
        _hot = -1;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        switch (Hit(e.Location))
        {
            case 0: _owner.EditNow(); break;
            case 1: _owner.SaveAsNow(); break;
            case 2: _owner.CancelNow(); break;
            case 3: _owner.FinishNow(); break;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _font.Dispose();
        base.Dispose(disposing);
    }
}
