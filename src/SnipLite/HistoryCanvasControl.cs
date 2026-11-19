namespace SnipLite;

/// <summary>
/// Double-buffered, child-free canvas that draws the capture thumbnails inside a
/// native <see cref="ToolStripDropDown"/>. Living inside the menu family keeps the
/// tray menu's mouse capture intact (no focus loss, no instant close) while the
/// plain drop-down can be positioned at an explicit screen point, so it never
/// flips to the left of the tray menu. Every thumbnail keeps its own aspect ratio
/// and is centred inside a uniform card, so no picture is ever blown up or cropped.
/// </summary>
internal sealed class HistoryCanvasControl : Control
{
    private const int Pad = 6;
    private const int Gap = 6;
    private static readonly Color HoverEdge = Color.FromArgb(0, 103, 192);

    private readonly IReadOnlyList<CaptureHistory.Entry> _entries;
    private readonly int _cardWidth;
    private readonly int _cardHeight;
    private readonly Action<CaptureHistory.Entry> _click;
    private long _shownAt;
    private int _offset;
    private int _hover = -1;
    private int _pressed = -1;
    private long _pressedAt;

    public HistoryCanvasControl(IReadOnlyList<CaptureHistory.Entry> entries, int cardWidth, int maxHeight, Action<CaptureHistory.Entry> click)
    {
        _entries = entries;
        _cardWidth = cardWidth;
        _cardHeight = Math.Max(30, (int)Math.Round(cardWidth * 9 / 16.0));
        _click = click;
        _shownAt = Environment.TickCount64;

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        DoubleBuffered = true;
        BackColor = SnipMenuRenderer.Back;
        TabStop = false;
        Width = cardWidth + Pad * 2;
        // At most five cards are visible; the rest scroll with the wheel.
        var visible = Math.Min(ContentHeight, 5 * _cardHeight + 36);
        Height = entries.Count == 0 ? 40 : Math.Min(visible, Math.Max(40, maxHeight));
    }

    private int ContentHeight => Pad * 2 + _entries.Count * _cardHeight + Math.Max(0, _entries.Count - 1) * Gap;

    /// <summary>
    /// Effective card height: the 16:9 height of the column, capped so the visible
    /// cards fit the hosted client area exactly.
    /// </summary>
    private int CardHeight
    {
        get
        {
            var count = Math.Max(1, Math.Min(_entries.Count, 5));
            var fit = (Height - Pad * 2 - (count - 1) * Gap) / count;
            return Math.Min(_cardHeight, Math.Max(24, fit));
        }
    }

    private int Stride => CardHeight + Gap;

    private int IndexAt(int y)
    {
        y += _offset - Pad;
        if (y < 0) return -1;
        var index = y / Stride;
        if (index >= _entries.Count || y % Stride >= CardHeight) return -1;
        return index;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Painted in OnPaint together with the pictures: no background flash.
    }

    /// <summary>Restarts the anti-misclick window each time the popup really appears.</summary>
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        _pressed = -1;
        _hover = -1;
        if (Visible) _shownAt = Environment.TickCount64;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

        if (_entries.Count == 0)
        {
            TextRenderer.DrawText(g, UiText.T("暂无历史截图"), Font, ClientRectangle,
                AppTheme.Current.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            return;
        }

        var cardHeight = CardHeight;
        for (var index = 0; index < _entries.Count; index++)
        {
            var top = Pad + index * Stride - _offset;
            if (top + cardHeight < 0) continue;
            if (top > Height) break;
            var card = new Rectangle(Pad, top, _cardWidth, cardHeight);
            var thumb = _entries[index].Thumbnail;
            // Force 96 DPI: a high-DPI source would otherwise be scaled by GDI+ and
            // overflow the destination rectangle, cutting the right edge away.
            if (Math.Abs(thumb.HorizontalResolution - 96f) > 0.1f) thumb.SetResolution(96f, 96f);
            // Aspect-fit inside the card: the whole picture, never cropped, never
            // stretched, and the same scale for every entry.
            var scale = Math.Min(card.Width / (float)thumb.Width, card.Height / (float)thumb.Height);
            var width = Math.Max(1, (int)Math.Round(thumb.Width * scale));
            var height = Math.Max(1, (int)Math.Round(thumb.Height * scale));
            var rect = new Rectangle(card.X + (card.Width - width) / 2, card.Y + (card.Height - height) / 2, width, height);
            g.DrawImage(thumb, rect, 0, 0, thumb.Width, thumb.Height, GraphicsUnit.Pixel);
            if (index != _hover) continue;
            using var pen = new Pen(HoverEdge, 2f);
            g.DrawRectangle(pen, rect.X + 1, rect.Y + 1, rect.Width - 3, rect.Height - 3);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = IndexAt(e.Y);
        Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
        if (hit == _hover) return;
        _hover = hit;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        Cursor = Cursors.Default;
        if (_hover < 0) return;
        _hover = -1;
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        var max = Math.Max(0, ContentHeight - Height);
        var next = Math.Clamp(_offset - e.Delta / 2, 0, max);
        if (next == _offset) return;
        _offset = next;
        Invalidate();
    }

    // A picture is restored only by a real left press + release on the same one: a
    // stray click synthesised while the popup appears can never pin anything.
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _pressed = e.Button == MouseButtons.Left ? IndexAt(e.Y) : -1;
        _pressedAt = Environment.TickCount64;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var pressed = _pressed;
        _pressed = -1;
        if (e.Button != MouseButtons.Left || pressed < 0) return;
        if (_pressedAt - _shownAt < 150) return;
        if (Environment.TickCount64 - _shownAt < 150) return;
        if (IndexAt(e.Y) != pressed) return;
        _click(_entries[pressed]);
    }
}
