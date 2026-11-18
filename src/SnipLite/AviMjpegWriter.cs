using System.Drawing.Imaging;

namespace SnipLite;

/// <summary>
/// Minimal Motion-JPEG AVI writer. Frames are JPEG encoded and appended as
/// <c>00dc</c> chunks, then an index is written on completion. Used directly for the
/// AVI output format and as the fallback when Media Foundation is unavailable.
/// </summary>
internal sealed class AviMjpegWriter : IFrameWriter
{
    private readonly FileStream _stream;
    private readonly BinaryWriter _writer;
    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly ImageCodecInfo? _jpeg;
    private readonly int _quality;
    private readonly long _riffSizePos;
    private readonly long _frameCountPos;
    private readonly long _streamLengthPos;
    private readonly long _moviSizePos;
    private readonly long _moviFourCcPos;
    private readonly List<(int Offset, int Size)> _index = new();
    private int _frames;
    private int _maxFrame;
    private bool _closed;

    public AviMjpegWriter(string path, int width, int height, int fps, int quality = 85)
    {
        _width = width;
        _height = height;
        _fps = Math.Max(1, fps);
        _quality = Math.Clamp(quality, 40, 98);
        _jpeg = ImageCodecInfo.GetImageEncoders().FirstOrDefault(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
        _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        _writer = new BinaryWriter(_stream);

        _writer.Write("RIFF"u8.ToArray());
        _riffSizePos = _stream.Position;
        _writer.Write(0);
        _writer.Write("AVI "u8.ToArray());

        _writer.Write("LIST"u8.ToArray());
        _writer.Write(4 + 8 + 56 + 8 + 56 + 8 + 40);
        _writer.Write("hdrl"u8.ToArray());

        _writer.Write("avih"u8.ToArray());
        _writer.Write(56);
        _writer.Write(1_000_000 / _fps); // microseconds per frame
        _writer.Write(0);                // max bytes per second (patched never)
        _writer.Write(0);                // padding
        _writer.Write(0x10);             // AVIF_HASINDEX
        _frameCountPos = _stream.Position;
        _writer.Write(0);                // total frames
        _writer.Write(0);                // initial frames
        _writer.Write(1);                // streams
        _writer.Write(_width * _height * 3);
        _writer.Write(_width);
        _writer.Write(_height);
        for (var i = 0; i < 4; i++) _writer.Write(0);

        _writer.Write("LIST"u8.ToArray());
        _writer.Write(8 + 56 + 8 + 40);
        _writer.Write("strl"u8.ToArray());

        _writer.Write("strh"u8.ToArray());
        _writer.Write(56);
        _writer.Write("vids"u8.ToArray());
        _writer.Write("MJPG"u8.ToArray());
        _writer.Write(0);   // flags
        _writer.Write((short)0);
        _writer.Write((short)0);
        _writer.Write(0);   // initial frames
        _writer.Write(1);   // scale
        _writer.Write(_fps);
        _writer.Write(0);   // start
        _streamLengthPos = _stream.Position;
        _writer.Write(0);   // length (patched)
        _writer.Write(_width * _height * 3);
        _writer.Write(-1);  // quality
        _writer.Write(0);   // sample size
        _writer.Write((short)0);
        _writer.Write((short)0);
        _writer.Write((short)_width);
        _writer.Write((short)_height);

        _writer.Write("strf"u8.ToArray());
        _writer.Write(40);
        _writer.Write(40);            // biSize
        _writer.Write(_width);
        _writer.Write(_height);
        _writer.Write((short)1);      // planes
        _writer.Write((short)24);     // bit count
        _writer.Write("MJPG"u8.ToArray());
        _writer.Write(_width * _height * 3);
        _writer.Write(0);
        _writer.Write(0);
        _writer.Write(0);
        _writer.Write(0);

        _writer.Write("LIST"u8.ToArray());
        _moviSizePos = _stream.Position;
        _writer.Write(0);
        _moviFourCcPos = _stream.Position;
        _writer.Write("movi"u8.ToArray());
    }

    public void WriteFrame(Bitmap frame, double seconds)
    {
        if (_closed) return;
        using var buffer = new MemoryStream();
        var encoder = _jpeg;
        if (encoder is null)
        {
            frame.Save(buffer, ImageFormat.Jpeg);
        }
        else
        {
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)_quality);
            frame.Save(buffer, encoder, parameters);
        }
        var data = buffer.ToArray();
        var chunkStart = _stream.Position;
        _writer.Write("00dc"u8.ToArray());
        _writer.Write(data.Length);
        _writer.Write(data);
        if ((data.Length & 1) != 0) _writer.Write((byte)0);

        _index.Add(((int)(chunkStart - _moviFourCcPos), data.Length));
        _frames++;
        _maxFrame = Math.Max(_maxFrame, data.Length);
    }

    public void Complete()
    {
        if (_closed) return;
        _closed = true;

        var moviEnd = _stream.Position;
        var moviSize = (int)(moviEnd - _moviSizePos - 8);
        _writer.Write("idx1"u8.ToArray());
        _writer.Write(_index.Count * 16);
        foreach (var (offset, size) in _index)
        {
            _writer.Write("00dc"u8.ToArray());
            _writer.Write(0x10); // AVIIF_KEYFRAME
            _writer.Write(offset);
            _writer.Write(size);
        }
        var fileEnd = _stream.Position;
        _writer.Flush();

        _stream.Position = _riffSizePos;
        _writer.Write((int)(fileEnd - _riffSizePos - 4));
        _stream.Position = _moviSizePos;
        _writer.Write(moviSize);
        _stream.Position = _frameCountPos;
        _writer.Write(_frames);
        _stream.Position = _streamLengthPos;
        _writer.Write(_frames);
        _writer.Flush();
        _writer.Dispose();
        _stream.Dispose();
    }

    public void Dispose() => Complete();
}
