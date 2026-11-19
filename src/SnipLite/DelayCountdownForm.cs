using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SnipLite;

/// <summary>
/// Large glass-style digit in the middle of the screen before a delayed capture.
/// Painted per pixel through UpdateLayeredWindow so the glyph keeps clean edges
/// on any desktop background. Never in the taskbar, never takes focus, click
/// through, and Esc anywhere cancels it.
/// </summary>
internal sealed class DelayCountdownForm : ToolWindow
{
    private const int LogicalSize = 400;
    private const float EmSize = 170f;

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

    /// <summary>
    /// Draws the neon / jelly-glass digit into an ARGB bitmap and pushes it to the
    /// layered window. Four stacked passes: outer neon glow, translucent glass
    /// body, inner highlights, then a sharp neon tube rim.
    /// </summary>
    private void Render()
    {
        if (!IsHandleCreated) return;

        var size = new Size(Width, Height);
        using var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            using var path = GlyphPath(_remaining.ToString());
            if (path is null) return;

            var bounds = path.GetBounds();
            var glowArea = RectangleF.Inflate(bounds, 60 * _scale, 60 * _scale);

            // 1. Outer neon halo: five widening round pens, warm orange -> magenta.
            var halo = new (float Width, int Alpha)[] { (40f, 20), (30f, 30), (22f, 45), (14f, 65), (7f, 90) };
            foreach (var (width, alpha) in halo)
            {
                using var brush = NeonBrush(glowArea, alpha);
                using var pen = new Pen(brush, width * _scale) { LineJoin = LineJoin.Round };
                g.DrawPath(pen, path);
            }

            // 2. Translucent jelly body: pink top -> violet middle -> blue-violet base.
            using (var body = new LinearGradientBrush(bounds,
                       Color.FromArgb(150, 255, 190, 245), Color.FromArgb(145, 160, 185, 255), 90f))
            {
                body.InterpolationColors = new ColorBlend
                {
                    Positions = new[] { 0f, 0.5f, 1f },
                    Colors = new[]
                    {
                        Color.FromArgb(150, 255, 190, 245),
                        Color.FromArgb(110, 210, 140, 255),
                        Color.FromArgb(145, 160, 185, 255)
                    }
                };
                g.FillPath(body, path);
            }

            // 3. Inner refraction: clip to the glyph, then stroke offset copies so
            //    only the upper-left highlight and lower-right dark line show.
            using (var clip = (GraphicsPath)path.Clone())
            {
                var state = g.Save();
                g.SetClip(clip, CombineMode.Replace);

                using (var highlight = (GraphicsPath)path.Clone())
                using (var shift = new Matrix())
                {
                    shift.Translate(-3.2f * _scale, -3.2f * _scale);
                    highlight.Transform(shift);
                    using var pen = new Pen(Color.FromArgb(195, 255, 255, 255), Math.Max(4f, 8f * _scale))
                    {
                        LineJoin = LineJoin.Round
                    };
                    g.DrawPath(pen, highlight);
                }

                using (var shade = (GraphicsPath)path.Clone())
                using (var shift = new Matrix())
                {
                    shift.Translate(3.2f * _scale, 3.2f * _scale);
                    shade.Transform(shift);
                    using var pen = new Pen(Color.FromArgb(85, 95, 20, 140), Math.Max(1.2f, 2.2f * _scale))
                    {
                        LineJoin = LineJoin.Round
                    };
                    g.DrawPath(pen, shade);
                }

                g.Restore(state);
            }

            // 4. Sharp neon tube rim: thin bright white-pink edge.
            using (var rim = new Pen(Color.FromArgb(245, 255, 235, 255), Math.Max(2f, 2f * _scale)))
            {
                rim.LineJoin = LineJoin.Round;
                g.DrawPath(rim, path);
            }
        }

        PushLayered(bitmap);
    }

    /// <summary>Orange -> magenta -> violet diagonal gradient used by every halo pass.</summary>
    private static LinearGradientBrush NeonBrush(RectangleF area, int alpha)
    {
        var brush = new LinearGradientBrush(area,
            Color.FromArgb(alpha, 0xFF, 0x5E, 0x3A), Color.FromArgb(alpha, 0x8A, 0x2B, 0xE2), 45f);
        brush.InterpolationColors = new ColorBlend
        {
            Positions = new[] { 0f, 0.55f, 1f },
            Colors = new[]
            {
                Color.FromArgb(alpha, 0xFF, 0x5E, 0x3A),
                Color.FromArgb(alpha, 0xE0, 0x26, 0xFF),
                Color.FromArgb(alpha, 0x8A, 0x2B, 0xE2)
            }
        };
        return brush;
    }

    private GraphicsPath GlyphPath(string digit)
    {
        using var family = RoundedFamily();
        var path = new GraphicsPath();

        var em = EmSize * _scale;
        path.AddString(digit, family, (int)FontStyle.Bold, em, PointF.Empty, StringFormat.GenericTypographic);

        if (path.PointCount == 0) { path.Dispose(); return null!; }

        // Centre the measured glyph (and leave room for its shadow) in the window.
        var b = path.GetBounds();
        using var m = new Matrix();
        m.Translate((Width - b.Width) / 2f - b.X, (Height - b.Height) / 2f - b.Y - 6 * _scale);
        path.Transform(m);
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

    /// <summary>Rounded modern face when installed, otherwise the system UI face.</summary>
    private static FontFamily RoundedFamily()
    {
        foreach (var name in new[] { "Arial Rounded MT Bold", "Nunito", "Segoe UI" })
        {
            try { return new FontFamily(name); }
            catch (ArgumentException) { /* not installed */ }
        }
        return new FontFamily(System.Drawing.Text.GenericFontFamilies.SansSerif);
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
