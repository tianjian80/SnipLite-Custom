using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Sdcb.SimdPaddleOCR;
using Sdcb.SimdPaddleOCR.Models.ChineseV6Small;

namespace SnipLite;

internal enum OcrEngineKind
{
    SimdPaddle = 0,
    WindowsEnhanced = 1,
    RapidLight = 2
}

internal sealed record OcrEngineChoice(OcrEngineKind Kind, string Label);

internal static class OcrEngines
{
    public static OcrEngineChoice[] Choices { get; } =
    {
        new(OcrEngineKind.RapidLight, "轻量极速识别"),
        new(OcrEngineKind.SimdPaddle, "飞桨高精识别"),
        new(OcrEngineKind.WindowsEnhanced, "本地系统增强")
    };

    public static string Label(OcrEngineKind kind) =>
        Choices.FirstOrDefault(choice => choice.Kind == kind)?.Label ?? "飞桨高精识别";
}

/// <summary>A single, lazily loaded offline model, shared by capture and pin OCR.</summary>
internal static class SimdOcr
{
    // Serializing requests bounds concurrent inference memory when several pins ask for OCR.
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static PaddleOcrAll? _engine;

    public static async Task<OcrDocument> RecognizeAsync(Bitmap source)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Keep both model initialization and inference off the UI thread, even on first use.
            return await Task.Run(async () =>
            {
                _engine ??= await PaddleOcrAll.LoadAsync(ChineseV6SmallModels.Default, new PaddleOcrOptions
                {
                    LineWorkerCount = 2,
                    DetIntraOpThreads = Math.Min(Environment.ProcessorCount, 4),
                    Detector = new() { Backend = OcrBackend.Cpu, MaxPooledSessions = 1 },
                    Classifier = new() { Backend = OcrBackend.Cpu, MaxPooledSessions = 2 },
                    Recognizer = new() { Backend = OcrBackend.Cpu, MaxPooledSessions = 2 }
                }).ConfigureAwait(false);

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

                var result = _engine.Run(pixels, bitmap.Width, bitmap.Height, returnCtcAlignment: true,
                    sourceStride: stride, format: ImagePixelFormat.Bgra32);
                return ToDocument(result, source.Size, bitmap.Size);
            }).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    internal static OcrDocument ToDocument(PaddleOcrResult result, Size original, Size input)
    {
        var characters = new List<RecognizedCharacter>();
        var lineBounds = new List<RectangleF>();
        var sx = (float)original.Width / input.Width;
        var sy = (float)original.Height / input.Height;
        foreach (var line in result.Lines)
        {
            if (string.IsNullOrEmpty(line.Text)) continue;
            var detected = line.Box;
            var lineRect = Bounds(detected.X1, detected.Y1, detected.X2, detected.Y2, detected.X3, detected.Y3, detected.X4, detected.Y4);
            lineBounds.Add(lineRect);
            var prefix = characters.Count == 0 ? "" : Environment.NewLine;
            var boxes = line.EstimateCharacterBoxes();
            if (boxes.Length == 0 || string.Concat(boxes.Select(b => b.Text)) != line.Text)
            {
                // Retain all text when a future model cannot return token alignment.
                var fallback = new OcrDocument(new[] { new RecognizedWord(line.Text, lineRect, 0) });
                characters.AddRange(fallback.Characters.Select((c, index) => index == 0 ? c with { Prefix = prefix } : c));
                continue;
            }
            // Sdcb's CTC character estimates shrink inward at the line ends, leaving the
            // first/last glyph under-covered. Stretch the per-character bounds so their
            // outer rectangle exactly matches the detected line box — the same geometry
            // the other two engines already produce — then scale back to the crop.
            var mapped = boxes.Select(b => Bounds(b.X1, b.Y1, b.X2, b.Y2, b.X3, b.Y3, b.X4, b.Y4)).ToArray();
            NormalizeToLine(mapped, lineRect);
            for (var i = 0; i < boxes.Length; i++)
            {
                characters.Add(new(boxes[i].Text, mapped[i], prefix));
                prefix = "";
            }
        }
        return OcrDocument.FromCharacters(characters, lineBounds);

        static void NormalizeToLine(RectangleF[] chars, RectangleF line)
        {
            var left = chars.Min(c => c.Left);
            var top = chars.Min(c => c.Top);
            var right = chars.Max(c => c.Right);
            var bottom = chars.Max(c => c.Bottom);
            var fx = right > left ? line.Width / (right - left) : 1f;
            var fy = bottom > top ? line.Height / (bottom - top) : 1f;
            for (var i = 0; i < chars.Length; i++)
            {
                var c = chars[i];
                chars[i] = new RectangleF(
                    line.Left + (c.Left - left) * fx,
                    line.Top + (c.Top - top) * fy,
                    c.Width * fx,
                    c.Height * fy);
            }
        }

        RectangleF Bounds(float x1, float y1, float x2, float y2, float x3, float y3, float x4, float y4)
        {
            var left = Math.Clamp(Math.Min(Math.Min(x1, x2), Math.Min(x3, x4)) * sx, 0, original.Width);
            var top = Math.Clamp(Math.Min(Math.Min(y1, y2), Math.Min(y3, y4)) * sy, 0, original.Height);
            var right = Math.Clamp(Math.Max(Math.Max(x1, x2), Math.Max(x3, x4)) * sx, left, original.Width);
            var bottom = Math.Clamp(Math.Max(Math.Max(y1, y2), Math.Max(y3, y4)) * sy, top, original.Height);
            return RectangleF.FromLTRB(left, top, right, bottom);
        }
    }
}
