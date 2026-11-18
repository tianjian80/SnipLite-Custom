using System.Drawing.Imaging;

namespace SnipLite;

internal sealed class CaptureHistory
{
    /// <summary>Longest edge of the cached thumbnail; the menu never loads the full PNG.</summary>
    private const int ThumbEdge = 96;

    internal sealed record Entry(byte[] Png, Rectangle Bounds, DateTime Time, Bitmap Thumbnail)
    {
        public Bitmap Image() { using var stream = new MemoryStream(Png); using var image = new Bitmap(stream); return new Bitmap(image); }
    }
    private readonly List<Entry> _entries = new();
    public IReadOnlyList<Entry> Entries => _entries;
    public void Add(Bitmap image, Rectangle bounds, int limit)
    {
        if (limit <= 0) { Clear(); return; }
        using var stream = new MemoryStream(); image.Save(stream, ImageFormat.Png);
        // One independent record per capture: no minute-based overwrites.
        _entries.Insert(0, new(stream.ToArray(), bounds, DateTime.Now, Thumbnail(image)));
        Trim(limit);
    }
    /// <summary>Small ARGB thumbnail built once per capture; hover only touches these bytes.</summary>
    private static Bitmap Thumbnail(Bitmap image)
    {
        var scale = Math.Min(ThumbEdge / (float)image.Width, ThumbEdge / (float)image.Height);
        var width = Math.Max(1, (int)Math.Round(image.Width * scale));
        var height = Math.Max(1, (int)Math.Round(image.Height * scale));
        var thumb = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(thumb))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.DrawImage(image, new Rectangle(0, 0, width, height));
        }
        return thumb;
    }
    public void Trim(int limit)
    {
        if (limit < 0) limit = 0;
        if (_entries.Count <= limit) return;
        foreach (var entry in _entries.Skip(limit)) entry.Thumbnail.Dispose();
        _entries.RemoveRange(limit, _entries.Count - limit);
    }
    public void Clear()
    {
        foreach (var entry in _entries) entry.Thumbnail.Dispose();
        _entries.Clear();
    }
}
