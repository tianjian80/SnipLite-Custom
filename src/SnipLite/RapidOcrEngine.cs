using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using RapidOcrNet;
using SkiaSharp;

namespace SnipLite;

/// <summary>Lightweight multilingual PP-OCRv6 inference, shared by capture and pin OCR.</summary>
internal static class RapidOcrEngine
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static RapidOcr? _engine;

    public static async Task<OcrDocument> RecognizeAsync(Bitmap source)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                if (_engine is null)
                {
                    var engine = new RapidOcr();
                    var models = RapidOcrModelSet.PPOCRv6Tiny;
                    models = models with
                    {
                        DetModelPath = ModelPath(models.DetModelPath),
                        ClsModelPath = ModelPath(models.ClsModelPath),
                        RecModelPath = ModelPath(models.RecModelPath),
                        KeysPath = ModelPath(models.KeysPath)
                    };
                    try
                    {
                        engine.InitModels(models, Math.Min(Environment.ProcessorCount, 4));
                        _engine = engine;
                    }
                    catch
                    {
                        engine.Dispose();
                        throw;
                    }
                }

                // Bound exceptionally large selections. Normal screenshots retain their pixels.
                var scale = Math.Min(1d, Math.Min(4096d / Math.Max(source.Width, source.Height),
                    Math.Sqrt(16_000_000d / ((long)source.Width * source.Height))));
                using var bitmap = new Bitmap(Math.Max(1, (int)Math.Round(source.Width * scale)),
                    Math.Max(1, (int)Math.Round(source.Height * scale)), PixelFormat.Format32bppArgb);
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.White);
                    graphics.DrawImage(source, new Rectangle(Point.Empty, bitmap.Size));
                }

                var stride = checked(bitmap.Width * 4);
                var pixels = new byte[checked(stride * bitmap.Height)];
                var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    for (var y = 0; y < bitmap.Height; y++)
                        Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * stride, stride);
                }
                finally { bitmap.UnlockBits(data); }

                var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                try
                {
                    using var skBitmap = new SKBitmap();
                    var info = new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
                    if (!skBitmap.InstallPixels(info, pin.AddrOfPinnedObject(), stride))
                        throw new InvalidOperationException("无法准备 RapidOCR 输入图像。");

                    var options = RapidOcrOptions.PPOCRv6 with
                    {
                        ReturnWordBox = true,
                        ReturnSingleCharBox = true
                    };
                    var result = _engine.Detect(skBitmap, options, CancellationToken.None);
                    return ToDocument(result, source.Size, bitmap.Size);
                }
                finally { pin.Free(); }
            }).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    internal static OcrDocument ToDocument(OcrResult result, Size original, Size input)
    {
        var characters = new List<RecognizedCharacter>();
        var lineBounds = new List<RectangleF>();
        var sx = (float)original.Width / input.Width;
        var sy = (float)original.Height / input.Height;
        foreach (var line in result.TextBlocks)
        {
            if (string.IsNullOrEmpty(line.Text) || !ValidBox(line.BoxPoints)) continue;
            lineBounds.Add(Bounds(line.BoxPoints));
            var prefix = characters.Count == 0 ? "" : Environment.NewLine;
            var boxes = line.WordResults;
            if (boxes is null || boxes.Length == 0 || boxes.Any(box => !ValidBox(box.BoxPoints))
                || string.Concat(boxes.Select(box => box.Text)) != line.Text)
            {
                // Keep every character if the model cannot supply aligned word boxes.
                var fallback = new OcrDocument(new[]
                {
                    new RecognizedWord(line.Text, Bounds(line.BoxPoints), 0)
                });
                characters.AddRange(fallback.Characters.Select((character, index) =>
                    index == 0 ? character with { Prefix = prefix } : character));
                continue;
            }

            foreach (var box in boxes)
            {
                characters.Add(new(box.Text, Bounds(box.BoxPoints), prefix));
                prefix = "";
            }
        }
        return OcrDocument.FromCharacters(characters, lineBounds);

        static bool ValidBox(SKPointI[]? points) => points is { Length: 4 };

        RectangleF Bounds(SKPointI[] points)
        {
            var left = Math.Clamp(points.Min(point => point.X) * sx, 0, original.Width);
            var top = Math.Clamp(points.Min(point => point.Y) * sy, 0, original.Height);
            var right = Math.Clamp(points.Max(point => point.X) * sx, left, original.Width);
            var bottom = Math.Clamp(points.Max(point => point.Y) * sy, top, original.Height);
            return RectangleF.FromLTRB(left, top, right, bottom);
        }
    }

    private static string ModelPath(string path) => Path.GetFullPath(path, AppContext.BaseDirectory);
}
