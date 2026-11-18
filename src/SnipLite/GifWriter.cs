using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SnipLite;

/// <summary>
/// GIF writer built on the in-box WPF <see cref="GifBitmapEncoder"/>: every frame is
/// quantised and palette-mapped by the framework encoder (so colours and row order are
/// always correct), then a NETSCAPE2.0 block is spliced in afterwards to request looping.
/// </summary>
internal sealed class GifWriter : IFrameWriter
{
    private readonly GifBitmapEncoder _encoder = new();
    private readonly int _delay;
    private readonly bool _loop;
    private string? _path;
    private bool _closed;

    public GifWriter(string path, Size size, int fps, bool loop)
    {
        _path = path;
        _loop = loop;
        // GIF only stores hundredths of a second; clamp so players never see 0.
        _delay = Math.Max(2, (int)Math.Round(100.0 / Math.Max(1, fps)));
    }

    public void WriteFrame(Bitmap frame, double seconds)
    {
        if (_closed) return;
        var source = ToSource(frame);
        var metadata = new BitmapMetadata("gif");
        metadata.SetQuery("/grctlext/Delay", (ushort)_delay);
        _encoder.Frames.Add(BitmapFrame.Create(source, null, metadata, null));
    }

    private static BitmapSource ToSource(Bitmap bitmap)
    {
        var stride = bitmap.Width * 4;
        var bytes = new byte[stride * bitmap.Height];
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            if (data.Stride == stride) Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            else
                for (var y = 0; y < bitmap.Height; y++)
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), bytes, y * stride, stride);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        var source = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96,
            PixelFormats.Bgra32, null, bytes, stride);
        source.Freeze();
        return source;
    }

    public void Complete()
    {
        if (_closed) return;
        _closed = true;
        if (_path is null) return;

        using var buffer = new MemoryStream();
        _encoder.Save(buffer);
        var gif = buffer.ToArray();
        var offset = GlobalTableEnd(gif);
        if (offset > 0 && _loop)
        {
            var loopBlock = new byte[]
            {
                0x21, 0xFF, 0x0B,
                (byte)'N', (byte)'E', (byte)'T', (byte)'S', (byte)'C', (byte)'A', (byte)'P', (byte)'E',
                (byte)'2', (byte)'.', (byte)'0',
                0x03, 0x01, 0x00, 0x00, 0x00
            };
            using var file = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read);
            file.Write(gif, 0, offset);
            file.Write(loopBlock, 0, loopBlock.Length);
            file.Write(gif, offset, gif.Length - offset);
        }
        else
        {
            File.WriteAllBytes(_path, gif);
        }
        _path = null;
    }

    /// <summary>Byte offset just past the header, logical screen descriptor and global colour table.</summary>
    private static int GlobalTableEnd(byte[] gif)
    {
        if (gif.Length < 13) return -1;
        if (gif[0] != 'G' || gif[1] != 'I' || gif[2] != 'F') return -1;
        var packed = gif[10];
        if ((packed & 0x80) == 0) return 13; // no global colour table
        var entries = 1 << ((packed & 0x07) + 1);
        var end = 13 + entries * 3;
        return end <= gif.Length ? end : -1;
    }

    public void Dispose() => Complete();
}
