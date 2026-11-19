namespace SnipLite;

/// <summary>
/// Double-buffered, child-free canvas that draws the capture thumbnails inside a
/// native <see cref="ToolStripDropDown"/>. Living inside the menu family keeps the
/// tray menu's mouse capture intact (no focus loss, no instant close) while the
/// plain drop-down can be positioned at an explicit screen point, so it never
/// flips to the left of the tray menu. The canvas itself is the only frame: a
/// 2 px symmetric border, no title bar, no scroll arrows.
/// </summary>
internal sealed class HistoryCanvasControl : Control
{
    /// <summary>2 px thin frame on all four sides.</summary>
    private const int Pad = 2;
    /// <summary>3 px gap between two pictures.</summary>
    private const int Gap = 3;
    private static readonly Color HoverEdge = Color.FromArgb(0, 103, 192);

    private readonly IReadOnlyList<CaptureHistory.Entry> _entries;
    private readonly int[] _heights;
    private readonly int _drawWidth;
    private readonly Action<CaptureHistory.Entry> _click;
    private long _shownAt;
    private int _offset;
    private int _hover = -1;
    private int _pressed = -1;
    private long _pressedAt;

    public HistoryCanvasControl(IReadOnlyList<CaptureHistory.Entry> entries, int drawWidth, int maxHeight, Action<CaptureHistory.Entry> click)
    {
        _entries = entries;
        _drawWidth = drawWidth;
        _click = click;
        _shownAt = Environment.TickCount64;
        // Height follows each source ratio so every picture is complete.
        _heights = entries
            .Select(entry => Math.Max(20, (int)Math.Round((double)entry.Thumbnail.Height * drawWidth / entry.Thumbnail.Width)))
            .ToArray();

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        DoubleBuffered = true;
        BackColor = SnipMenuRenderer.Back;
        TabStop = false;
        // Whole popup: pictures plus 2 px of frame on each side.
        Width = entries.Count == 0 ? Math.Max(180, drawWidth) + 4 : drawWidth + 4;
        Height = entries.Count == 0 ? 40 : Math.Min(ContentHeight, Math.Max(40, maxHeight));
    }

    /// <summary>Pictures plus their 3 px gaps, framed by 2 px top and bottom.</summary>
    private int ContentHeight => Pad * 2 + _heights.Sum() + Math.Max(0, _entries.Count - 1) * Gap;

    private int TopOf(int index)
    {
        var top = Pad - _offset;
        for (var i = 0; i < index; i++) top += _heights[i] + Gap;
        return top;
    }

    private int IndexAt(int y)
    {
        var cursor = y + _offset - Pad;
        if (cursor < 0) return -1;
        var offset = 0;
        for (var i = 0; i < _heights.Length; i++)
        {
            if (cursor < offset + _heights[i]) return i;
            offset += _heights[i] + Gap;
        }
        return -1;
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

        for (var index = 0; index < _entries.Count; index++)
        {
            var top = TopOf(index);
            if (top + _heights[index] < 0) continue;
            if (top > ClientSize.Height) break;
            var thumb = _entries[index].Thumbnail;
            // Force 96 DPI: a high-DPI source would otherwise be scaled by GDI+ and
            // overflow the destination rectangle, cutting the right edge away.
            if (Math.Abs(thumb.HorizontalResolution - 96f) > 0.1f) thumb.SetResolution(96f, 96f);
            var card = new Rectangle(Pad, top, _drawWidth, _heights[index]);
            var scale = Math.Min(card.Width / (float)thumb.Width, card.Height / (float)thumb.Height);
            var width = Math.Max(1, (int)Math.Round(thumb.Width * scale));
            var height = Math.Max(1, (int)Math.Round(thumb.Height * scale));
            var rect = new Rectangle(card.X + (card.Width - width) / 2, card.Y + (card.Height - height) / 2, width, height);
            // Pixel source rectangle: the whole picture, never a cropped edge.
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
        var max = Math.Max(0, ContentHeight - ClientSize.Height);
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
