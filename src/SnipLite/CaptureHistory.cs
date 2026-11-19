using System.Drawing.Imaging;

namespace SnipLite;

internal sealed class CaptureHistory
{
    /// <summary>Longest edge of the cached preview; the menu never loads the full PNG.</summary>
    private const int ThumbEdge = 640;

    internal sealed record Entry(byte[] Png, Rectangle Bounds, DateTime Time, Bitmap Thumbnail, string? FilePath = null)
    {
        public Bitmap Image() { using var stream = new MemoryStream(Png); using var image = new Bitmap(stream); return new Bitmap(image); }
    }
    private readonly List<Entry> _entries = new();
    public IReadOnlyList<Entry> Entries => _entries;
    public void Add(Bitmap image, Rectangle bounds, int limit, string? filePath = null)
    {
        if (limit <= 0) { Clear(); return; }
        using var stream = new MemoryStream(); image.Save(stream, ImageFormat.Png);
        // One independent record per capture: no minute-based overwrites.
        _entries.Insert(0, new(stream.ToArray(), bounds, DateTime.Now, Thumbnail(image), filePath));
        Trim(limit);
    }
    /// <summary>
    /// Drops records whose saved file the user deleted outside the app. Called before
    /// the history panel is shown, so no ghost thumbnails survive.
    /// </summary>
    public void PruneMissing()
    {
        for (var i = _entries.Count - 1; i >= 0; i--)
        {
            var entry = _entries[i];
            if (entry.FilePath is not { Length: > 0 } path || File.Exists(path)) continue;
            entry.Thumbnail.Dispose();
            _entries.RemoveAt(i);
        }
    }
    /// <summary>Removes a single record immediately, e.g. when its file vanished on click.</summary>
    public void Remove(Entry entry)
    {
        var index = _entries.IndexOf(entry);
        if (index < 0) return;
        entry.Thumbnail.Dispose();
        _entries.RemoveAt(index);
    }
    /// <summary>Attaches a path when the save happens after the record was created.</summary>
    public void ReplaceLatestPath(string? filePath)
    {
        if (_entries.Count == 0 || string.IsNullOrEmpty(filePath)) return;
        _entries[0] = _entries[0] with { FilePath = filePath };
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
