using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SnipLite;

/// <summary>
/// Large red seven-segment LED digit in the middle of the screen before a delayed
/// capture. Painted per pixel through UpdateLayeredWindow so the glow keeps clean
/// edges on any desktop background. Never in the taskbar, never takes focus,
/// click through, and Esc anywhere cancels it.
/// </summary>
internal sealed class DelayCountdownForm : ToolWindow
{
    private const int LogicalSize = 400;

    // Segment geometry in 96 dpi units: digit height, bar thickness, corner gap.
    // 210 x 113.5 keeps every bar slim (about 1:4.2) like a real LED display.
    private const float DigitHeight = 210f;
    private const float BarThickness = 20f;
    private const float SegmentGap = 4f;
    private const float DigitAspect = 1.85f;

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly int _total;
    private int _remaining;
    private float _scale = 1f;
    private IntPtr _hook;

    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    private readonly HookProc _callback;

    public event Action? Finished;

    public DelayCountdownForm(int seconds)
    {
        _total = Math.Max(1, seconds);
        _remaining = _total;
        _callback = Filter;

        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.Opaque, true);

        _timer.Tick += (_, _) => Tick();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00080000; // WS_EX_LAYERED: per-pixel alpha
            cp.ExStyle |= 0x00000020; // WS_EX_TRANSPARENT: clicks fall through
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE: never steal focus
            return cp;
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _scale = DeviceDpi / 96f;
        var size = (int)Math.Round(LogicalSize * _scale);
        Size = new Size(size, size);
        CenterOnScreen();
    }

    private void CenterOnScreen()
    {
        var area = Screen.FromPoint(Cursor.Position).Bounds;
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // A global hook lets Esc cancel even though the digit is not activated.
        _hook = SetWindowsHookEx(13, _callback, GetModuleHandle(null), 0);
        CenterOnScreen();
        Render();
        _timer.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        base.OnFormClosed(e);
    }

    private IntPtr Filter(int code, IntPtr message, IntPtr data)
    {
        const int WmKeyDown = 0x0100;
        const int VkEscape = 0x1B;
        if (code >= 0 && message == WmKeyDown && Marshal.ReadInt32(data) == VkEscape)
        {
            BeginInvoke(new Action(Close));
            return new IntPtr(1);
        }
        return CallNextHookEx(_hook, code, message, data);
    }

    private void Tick()
    {
        _remaining--;
        if (_remaining > 0)
        {
            Render();
            return;
        }

        _timer.Stop();
        Hide();
        // One compositor frame so the digit is gone before the screen is frozen.
        var settle = new System.Windows.Forms.Timer { Interval = 90 };
        settle.Tick += (_, _) =>
        {
            settle.Stop();
            settle.Dispose();
            Finished?.Invoke();
            Close();
        };
        settle.Start();
    }

    // Painting is fully manual (layered bitmap); keep the base paint from flashing.
    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }

    /// <summary>Renders the lit seven-segment digit with its red LED glow.</summary>
    private void Render()
    {
        if (!IsHandleCreated) return;

        using var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var digits = _remaining.ToString();
            var h = DigitHeight * _scale;
            var t = BarThickness * _scale / 2f;
            var gap = SegmentGap * _scale;
            var w = h / DigitAspect;
            var spacing = w * 0.18f;
            var total = digits.Length * w + (digits.Length - 1) * spacing;
            var x = (Width - total) / 2f;
            var y = (Height - h) / 2f;

            var lit = new GraphicsPath();
            var bodies = new List<GraphicsPath>();
            foreach (var ch in digits)
            {
                var figure = DigitPath(ch, x, y, w, h, t, gap);
                foreach (var segment in figure) bodies.Add(segment);
                x += w + spacing;
            }

            using (lit)
            {
                foreach (var segment in bodies) lit.AddPath(segment, false);

                // 1. Silky halo: paint the lit bars small, then scale back up twice.
                //    Bilinear down/up sampling acts as a true gaussian-ish blur, so
                //    there are no stacked hard-edged rings around the digit.
                PaintGlow(g, lit, glowColor: Color.FromArgb(145, 255, 20, 30));
            }

            // 2. Crisp LED body: sharp anti-aliased hexagons, gem red gradient,
            //    no inner blur so every 45 degree corner stays razor sharp.
            foreach (var segment in bodies)
            {
                using (segment)
                {
                    var b = segment.GetBounds();
                    if (b.Width <= 0 || b.Height <= 0) continue;

                    using var body = new LinearGradientBrush(b,
                        Color.FromArgb(255, 0xE6, 0x00, 0x12), Color.FromArgb(255, 0xFF, 0x1E, 0x2D), 45f);
                    g.FillPath(body, segment);
                }
            }
        }

        PushLayered(bitmap);
    }

    /// <summary>
    /// Smooth LED glow: fill the lit bars into a quarter-size ARGB bitmap, then
    /// scale it back up through two bilinear passes. The resampling spreads the
    /// light like a real gaussian blur instead of stacked hard-edged pen rings.
    /// </summary>
    private void PaintGlow(Graphics g, GraphicsPath lit, Color glowColor)
    {
        var qw = Math.Max(1, Width / 4);
        var qh = Math.Max(1, Height / 4);
        using var small = new Bitmap(qw, qh, PixelFormat.Format32bppPArgb);
        using (var sg = Graphics.FromImage(small))
        {
            sg.SmoothingMode = SmoothingMode.AntiAlias;
            sg.Clear(Color.Transparent);
            sg.ScaleTransform(qw / (float)Width, qh / (float)Height);
            using var brush = new SolidBrush(glowColor);
            sg.FillPath(brush, lit);
        }

        var hw = Math.Max(1, Width / 2);
        var hh = Math.Max(1, Height / 2);
        using var mid = new Bitmap(hw, hh, PixelFormat.Format32bppPArgb);
        using (var mg = Graphics.FromImage(mid))
        {
            mg.InterpolationMode = InterpolationMode.HighQualityBilinear;
            mg.PixelOffsetMode = PixelOffsetMode.HighQuality;
            mg.DrawImage(small, new Rectangle(0, 0, hw, hh), 0, 0, qw, qh, GraphicsUnit.Pixel);
        }

        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(mid, new Rectangle(0, 0, Width, Height), 0, 0, hw, hh, GraphicsUnit.Pixel);

        // A wider, fainter ambient pass lets the red fog dissolve naturally.
        var pad = (int)Math.Round(12 * _scale);
        var wide = new Rectangle(-pad, -pad, Width + pad * 2, Height + pad * 2);
        using var fade = new ImageAttributes();
        fade.SetColorMatrix(new ColorMatrix { Matrix33 = 0.35f });
        g.DrawImage(mid, wide, 0, 0, hw, hh, GraphicsUnit.Pixel, fade);
    }

    private static readonly Dictionary<char, string> SegmentMap = new()
    {
        ['0'] = "abcdef",
        ['1'] = "bc",
        ['2'] = "abged",
        ['3'] = "abgcd",
        ['4'] = "fgbc",
        ['5'] = "afgcd",
        ['6'] = "afgecd",
        ['7'] = "abc",
        ['8'] = "abcdefg",
        ['9'] = "abcdfg"
    };

    /// <summary>Lit bar polygons for one digit, in digit-local coordinates offset by (x, y).</summary>
    private static List<GraphicsPath> DigitPath(char digit, float x, float y, float w, float h, float t, float gap)
    {
        var map = SegmentMap.TryGetValue(digit, out var segments) ? segments : "";
        var paths = new List<GraphicsPath>(7);

        var halfBar = w / 2f - gap;
        var vertHalf = h / 4f - 1.5f * t - gap;
        var left = x + t;
        var right = x + w - t;
        var center = x + w / 2f;

        void Add(char segment, GraphicsPath path)
        {
            if (map.Contains(segment)) paths.Add(path);
            else path.Dispose();
        }

        Add('a', BarH(center, y + t, halfBar, t));
        Add('g', BarH(center, y + h / 2f, halfBar, t));
        Add('d', BarH(center, y + h - t, halfBar, t));
        Add('f', BarV(left, y + h / 4f + t / 2f, vertHalf, t));
        Add('b', BarV(right, y + h / 4f + t / 2f, vertHalf, t));
        Add('e', BarV(left, y + 3f * h / 4f - t / 2f, vertHalf, t));
        Add('c', BarV(right, y + 3f * h / 4f - t / 2f, vertHalf, t));

        return paths;
    }

    /// <summary>Horizontal bar with 45 degree pointed ends.</summary>
    private static GraphicsPath BarH(float cx, float cy, float half, float t)
    {
        var path = new GraphicsPath();
        path.AddPolygon(new[]
        {
            new PointF(cx - half, cy),
            new PointF(cx - half + t, cy - t),
            new PointF(cx + half - t, cy - t),
            new PointF(cx + half, cy),
            new PointF(cx + half - t, cy + t),
            new PointF(cx - half + t, cy + t)
        });
        path.CloseFigure();
        return path;
    }

    /// <summary>Vertical bar with 45 degree pointed ends.</summary>
    private static GraphicsPath BarV(float cx, float cy, float half, float t)
    {
        var path = new GraphicsPath();
        path.AddPolygon(new[]
        {
            new PointF(cx, cy - half),
            new PointF(cx + t, cy - half + t),
            new PointF(cx + t, cy + half - t),
            new PointF(cx, cy + half),
            new PointF(cx - t, cy + half - t),
            new PointF(cx - t, cy - half + t)
        });
        path.CloseFigure();
        return path;
    }

    private void PushLayered(Bitmap bitmap)
    {
        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
        var old = SelectObject(memDc, hBitmap);
        try
        {
            var dst = new NativePoint { X = Left, Y = Top };
            var src = new NativePoint();
            var size = new NativeSize { Cx = Width, Cy = Height };
            var blend = new BlendFunction { Op = 0, Flags = 0, Alpha = 255, Format = 1 };
            UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2);
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize { public int Cx, Cy; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction { public byte Op, Flags, Alpha, Format; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst,
        ref NativePoint dst, ref NativeSize size, IntPtr hdcSrc,
        ref NativePoint src, int key, ref BlendFunction blend, int flags);

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}

/// <summary>
/// White camera-shutter flash played right after a silent capture has been saved,
/// so the burst of light never lands in the picture itself.
/// </summary>
internal sealed class CameraFlashForm : Form
{
    private readonly System.Windows.Forms.Timer _fade = new() { Interval = 15 };
    private double _alpha = 0.85;

    public CameraFlashForm(Rectangle bounds)
    {
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
        BackColor = Color.White;
        Opacity = _alpha;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        SetStyle(ControlStyles.OptimizedDoubleBuffer, true);

        _fade.Tick += (_, _) =>
        {
            _alpha -= 0.06;
            if (_alpha <= 0)
            {
                _fade.Stop();
                _fade.Dispose();
                Close();
                return;
            }
            Opacity = _alpha;
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
            cp.ExStyle |= 0x00000020; // WS_EX_TRANSPARENT
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            return cp;
        }
    }

    public void Play()
    {
        Show();
        _fade.Start();
    }
}
