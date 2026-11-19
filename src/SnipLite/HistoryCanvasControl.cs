namespace SnipLite;

/// <summary>
/// Double-buffered, child-free canvas that draws the capture thumbnails inside a
/// native <see cref="ToolStripDropDown"/>. Living inside the menu family keeps the
/// tray menu's mouse capture intact (no focus loss, no instant close) while the
/// plain drop-down can be positioned at an explicit screen point, so it never
/// flips to the left of the tray menu.
/// </summary>
internal sealed class HistoryCanvasControl : Control
{
    private const int Pad = 3;
    private const int Gap = 4;
    private static readonly Color HoverEdge = Color.FromArgb(0, 103, 192);

    private readonly IReadOnlyList<CaptureHistory.Entry> _entries;
    private readonly int[] _heights;
    private readonly int _cardWidth;
    private readonly Action<CaptureHistory.Entry> _click;
    private int _offset;
    private int _hover = -1;

    public HistoryCanvasControl(IReadOnlyList<CaptureHistory.Entry> entries, int cardWidth, int maxHeight, Action<CaptureHistory.Entry> click)
    {
        _entries = entries;
        _cardWidth = cardWidth;
        _click = click;
        // Height follows each source ratio so every picture stays complete.
        _heights = entries
            .Select(entry => Math.Max(24, (int)Math.Round((double)entry.Thumbnail.Height * cardWidth / entry.Thumbnail.Width)))
            .ToArray();

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        DoubleBuffered = true;
        BackColor = SnipMenuRenderer.Back;
        TabStop = false;
        Width = entries.Count == 0 ? Math.Max(180, cardWidth) : cardWidth;
        Height = entries.Count == 0 ? 36 : Math.Min(ContentHeight, Math.Max(80, maxHeight));
    }

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

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

        using (var edge = new Pen(AppTheme.Current.Border, 1f))
            g.DrawRectangle(edge, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);

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
            var scale = Math.Min((_cardWidth - Pad * 2) / (float)thumb.Width, _heights[index] / (float)thumb.Height);
            var width = Math.Max(1, (int)Math.Round(thumb.Width * scale));
            var height = Math.Max(1, (int)Math.Round(thumb.Height * scale));
            var rect = new Rectangle(Pad + (_cardWidth - Pad * 2 - width) / 2, top + (_heights[index] - height) / 2, width, height);
            // Pixel source rectangle: the whole picture, never a cropped edge.
            g.DrawImage(thumb, rect, 0, 0, thumb.Width, thumb.Height, GraphicsUnit.Pixel);
            if (index != _hover) continue;
            using var pen = new Pen(HoverEdge, 2f);
            g.DrawRectangle(pen, rect.X - 1, rect.Y - 1, rect.Width + 1, rect.Height + 1);
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

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        var hit = IndexAt(e.Y);
        if (hit >= 0) _click(_entries[hit]);
    }
}
