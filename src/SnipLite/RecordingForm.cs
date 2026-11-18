using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace SnipLite;

/// <summary>
/// Recording chrome: a thin red frame around the captured region plus a floating
/// control card with the elapsed time, pause / stop / cancel. The card sits outside
/// the region so it never ends up inside the recorded video.
/// </summary>
internal sealed class RecordingForm : Form
{
    private const int RingThickness = 2;

    private readonly Rectangle _region;
    private readonly AppSettings _settings;
    private readonly bool _gif;
    private readonly bool _showControls;
    private readonly float _ui;
    private readonly RecordingSession _session;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 200 };
    private readonly Font _timeFont;
    private FrameRing? _ring;
    private Rectangle _pauseRect;
    private Rectangle _stopRect;
    private Rectangle _cancelRect;
    private int _timeLeft;
    private int _timeWidth;
    private readonly ToolTip _tip = new()
    {
        InitialDelay = 300,
        ReshowDelay = 80,
        AutoPopDelay = 4000,
        // The card is a never-activated tool window, so the tip must opt out of the
        // active-window requirement explicitly.
        ShowAlways = true
    };
    private string _tipKey = "";
    private bool _closing;
    private bool _stopped;
    private IntPtr _hook;
    private HookProc? _hookProc;

    public RecordingForm(Rectangle region, AppSettings settings, bool gif, bool showControls = true)
    {
        _region = region;
        _settings = settings;
        _gif = gif;
        _showControls = showControls;
        _ui = Math.Max(1f, DeviceDpi / 96f);
        _timeFont = new Font("Consolas", 9f, FontStyle.Bold);

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = AppTheme.Current.Card;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        // Compact card: dot + HH:mm:ss + the three buttons, with no hollow middle.
        var padX = (int)Math.Round(9 * _ui);
        var dot = (int)Math.Round(7 * _ui);
        var gap = (int)Math.Round(5 * _ui);
        var button = (int)Math.Round(20 * _ui);
        _timeWidth = TextRenderer.MeasureText("00:00:00", _timeFont).Width;
        _timeLeft = padX + dot + gap;
        Width = _timeLeft + _timeWidth + gap + button * 3 + (int)Math.Round(4 * _ui) * 2 + (int)Math.Round(8 * _ui);
        Height = Math.Max((int)Math.Round(30 * _ui), button + (int)Math.Round(12 * _ui));
        if (_showControls)
        {
            Opacity = Math.Clamp((gif ? settings.GifTimerOpacity : settings.RecTimerOpacity) / 100f, 0.2f, 1f);
            PlaceBar();
        }
        else
        {
            // Preset / full-screen recordings stay completely unobstructed: the window
            // only keeps a handle for the keyboard hook and the stop toggle.
            Opacity = 0;
            Width = 1;
            Height = 1;
        }

        _session = new RecordingSession(region, settings, gif);
        _session.Finished += OnFinished;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080 | 0x08000000; // tool window, never activated
            cp.ClassStyle |= 0x00020000;           // CS_DROPSHADOW
            return cp;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_showControls)
        {
            _ring = new FrameRing(_region, RingThickness);
            _ring.Show();
            _timer.Tick += (_, _) =>
            {
                if (_session.LimitReached && !_closing) return;
                Invalidate();
            };
            _timer.Start();
        }
        _session.Start();
        InstallHook();
    }

    /// <summary>
    /// Control card sits just outside the bottom-right corner of the recorded region,
    /// right edges aligned; when the taskbar leaves no room below it flips above.
    /// </summary>
    private void PlaceBar()
    {
        var work = Screen.FromRectangle(_region).WorkingArea;
        var gap = (int)Math.Round(6 * _ui);
        var left = _region.Right - Width;
        var top = _region.Bottom + gap;
        if (top + Height > work.Bottom) top = _region.Top - Height - gap;
        left = Math.Clamp(left, work.Left + gap, Math.Max(work.Left + gap, work.Right - Width - gap));
        top = Math.Clamp(top, work.Top + gap, Math.Max(work.Top + gap, work.Bottom - Height - gap));
        Location = new Point(left, top);
    }

    private void LayoutItems()
    {
        var size = (int)Math.Round(20 * _ui);
        var gap = (int)Math.Round(4 * _ui);
        var right = ClientSize.Width - (int)Math.Round(8 * _ui);
        var top = (ClientSize.Height - size) / 2;
        _cancelRect = new Rectangle(right - size, top, size, size);
        _stopRect = new Rectangle(_cancelRect.Left - gap - size, top, size, size);
        _pauseRect = new Rectangle(_stopRect.Left - gap - size, top, size, size);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = FluentDrawing.Rounded(new Rectangle(0, 0, Width - 1, Height - 1),
                   (int)Math.Round(8 * _ui)))
        {
            using var fill = new SolidBrush(AppTheme.Current.Card);
            g.FillPath(fill, path);
        }

        LayoutItems();
        var accent = Color.FromArgb(255, 59, 48);
        var text = AppTheme.Current.Text;
        var dot = (int)Math.Round(7 * _ui);
        using (var brush = new SolidBrush(accent))
            g.FillEllipse(brush, (int)Math.Round(9 * _ui), (ClientSize.Height - dot) / 2, dot, dot);

        var total = (int)_session.Seconds;
        var label = $"{total / 3600:00}:{total / 60 % 60:00}:{total % 60:00}";
        TextRenderer.DrawText(g, label, _timeFont,
            new Rectangle(_timeLeft, 0, _timeWidth, ClientSize.Height), text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);

        using var pen = new Pen(text, Math.Max(1f, 1.4f * _ui)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        var inset = (int)Math.Round(6 * _ui);

        // pause / play
        var pause = Rectangle.Inflate(_pauseRect, -inset, -inset);
        if (_session.Paused)
        {
            using var brush = new SolidBrush(text);
            g.FillPolygon(brush, new[]
            {
                new Point(pause.Left, pause.Top), new Point(pause.Right, pause.Top + pause.Height / 2),
                new Point(pause.Left, pause.Bottom)
            });
        }
        else
        {
            var barWidth = Math.Max(1, pause.Width / 4);
            using var brush = new SolidBrush(text);
            g.FillRectangle(brush, pause.Left + pause.Width / 6, pause.Top, barWidth, pause.Height);
            g.FillRectangle(brush, pause.Right - pause.Width / 6 - barWidth, pause.Top, barWidth, pause.Height);
        }

        // stop
        var stop = Rectangle.Inflate(_stopRect, -inset, -inset);
        using (var brush = new SolidBrush(text))
            g.FillRectangle(brush, new Rectangle(stop.X + stop.Width / 6, stop.Y + stop.Height / 6,
                stop.Width * 2 / 3, stop.Height * 2 / 3));

        // cancel
        var cancel = Rectangle.Inflate(_cancelRect, -inset, -inset);
        g.DrawLine(pen, cancel.Left, cancel.Top, cancel.Right, cancel.Bottom);
        g.DrawLine(pen, cancel.Right, cancel.Top, cancel.Left, cancel.Bottom);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = _pauseRect.Contains(e.Location) || _stopRect.Contains(e.Location) || _cancelRect.Contains(e.Location)
            ? Cursors.Hand
            : Cursors.Default;
        ShowTip(e.Location);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _tip.Hide(this);
        _tipKey = "";
    }

    /// <summary>Hover hints for the three hotspots of the single painted card.</summary>
    private void ShowTip(Point point)
    {
        var key = _pauseRect.Contains(point) ? UiText.T("暂停 / 继续")
            : _stopRect.Contains(point) ? UiText.T("停止并保存")
            : _cancelRect.Contains(point) ? UiText.T("取消录制")
            : "";
        if (key == _tipKey) return;
        _tipKey = key;
        _tip.Hide(this);
        if (key.Length == 0) return;
        _tip.Show(key, this, point.X + (int)Math.Round(14 * _ui), point.Y + (int)Math.Round(22 * _ui), 4000);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || _closing) return;
        if (_pauseRect.Contains(e.Location))
        {
            _session.TogglePause();
            Invalidate();
            return;
        }
        if (_stopRect.Contains(e.Location))
        {
            Finish(save: true);
            return;
        }
        if (_cancelRect.Contains(e.Location))
        {
            Finish(save: false);
            return;
        }
        // dragging the card out of the way is allowed
        ReleaseCapture();
        SendMessage(Handle, 0x00A1 /*WM_NCLBUTTONDOWN*/, (IntPtr)2 /*HTCAPTION*/, IntPtr.Zero);
    }

    private void TogglePause()
    {
        if (_closing) return;
        _session.TogglePause();
        Invalidate();
    }

    /// <summary>
    /// Closing the recording UI is instant: the ring and the card vanish right away while
    /// the encoder finishes (and saves) on its background thread, so clicking stop never
    /// leaves the chrome hanging for a second or two.
    /// </summary>
    private void Finish(bool save)
    {
        if (_closing) return;
        _closing = true;
        _stopped = true;
        _timer.Stop();
        _ring?.Close();
        _ring = null;
        Opacity = 0;
        Hide();
        _session.Stop(save);
    }

    private void OnFinished(RecordingSession session)
    {
        if (IsDisposed) return;
        _stopped = true;
        _timer.Stop();
        _ring?.Close();
        _ring = null;
        Hide();
        Close();

        if (session.Error is not null)
        {
            Toast.Show(UiText.T("录制失败：") + session.Error);
            return;
        }
        if (session.FilePath is null)
        {
            Toast.Show(UiText.T("已取消录制"));
            return;
        }
        var message = (session.LimitReached ? UiText.T("已达到设定时长，已保存：") : UiText.T("已保存："))
                      + Path.GetFileName(session.FilePath);
        if (session.Note is not null) message += "  " + session.Note;
        Toast.Show(message);
        if (_settings.SoundOnSave) _ = Services.PlaySoundAsync(_settings.CaptureSound);
    }

    // ------------------------------------------------------------- keyboard

    private void InstallHook()
    {
        _hookProc = Hook;
        _hook = SetWindowsHookEx(13 /*WH_KEYBOARD_LL*/, _hookProc, GetModuleHandle(null), 0);
    }

    private IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (wParam == (IntPtr)0x0100 || wParam == (IntPtr)0x0104))
        {
            var info = Marshal.PtrToStructure<KbdHookStruct>(lParam);
            if (info.VirtualKey == 0x1B) // Esc: cancel
            {
                try { BeginInvoke(new Action(() => Finish(save: false))); } catch (InvalidOperationException) { }
                return (IntPtr)1;
            }
            if (info.VirtualKey == 0x0D) // Enter: stop and save
            {
                try { BeginInvoke(new Action(() => Finish(save: true))); } catch (InvalidOperationException) { }
                return (IntPtr)1;
            }
            if (info.VirtualKey == 0x20) // Space: pause / resume, or stop when chrome is hidden
            {
                try
                {
                    if (_showControls) BeginInvoke(new Action(TogglePause));
                    else BeginInvoke(new Action(() => Finish(save: true)));
                }
                catch (InvalidOperationException) { }
                return (IntPtr)1;
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _ring?.Close();
        _ring = null;
        _session.Finished -= OnFinished;
        if (!_stopped) _session.Stop(false);
        _tip.Dispose();
        _timeFont.Dispose();
        base.OnFormClosed(e);
    }

    private sealed class FrameRing : Form
    {
        public FrameRing(Rectangle region, int thickness)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.FromArgb(255, 59, 48);
            Bounds = Rectangle.Inflate(region, thickness + 1, thickness + 1);
            var outer = new Rectangle(0, 0, Width, Height);
            var inner = new Rectangle(thickness + 1, thickness + 1,
                Math.Max(1, Width - 2 * (thickness + 1)), Math.Max(1, Height - 2 * (thickness + 1)));
            var ring = new Region(outer);
            ring.Exclude(inner);
            Region = ring;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00000020 | 0x08000000 | 0x00000080; // transparent, no activate, toolwindow
                return cp;
            }
        }
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdHookStruct
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr Extra;
    }

    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
}
