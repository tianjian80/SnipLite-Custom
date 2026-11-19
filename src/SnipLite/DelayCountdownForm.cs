using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace SnipLite;

/// <summary>
/// Tiny translucent countdown badge shown in the top-right corner before a
/// delayed capture. Tool window, never in the taskbar, never takes focus, and
/// Esc anywhere cancels it.
/// </summary>
internal sealed class DelayCountdownForm : ToolWindow
{
    private static readonly Color CardFill = Color.FromArgb(0xF4, 0xF9, 0xFF);
    private static readonly Color CardEdge = Color.FromArgb(0x00, 0x67, 0xC0);
    private static readonly Color CardInk = Color.FromArgb(0x00, 0x67, 0xC0);

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
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = CardFill;
        Opacity = 0.88;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

        _timer.Tick += (_, _) => Tick();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE: never steal focus from the desktop
            return cp;
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _scale = DeviceDpi / 96f;
        var size = (int)Math.Round(42 * _scale);
        Size = new Size(size, size);
        var area = Screen.PrimaryScreen?.WorkingArea ?? Screen.GetWorkingArea(Cursor.Position);
        Location = new Point(area.Right - Width - (int)Math.Round(16 * _scale),
            area.Top + (int)Math.Round(16 * _scale));
        using var path = Rounded(new Rectangle(0, 0, Width, Height), (int)Math.Round(12 * _scale));
        Region = new Region(path);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // A global hook lets Esc cancel even though the badge is not activated.
        _hook = SetWindowsHookEx(13, _callback, GetModuleHandle(null), 0);
        _timer.Start();
        Invalidate();
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
            Invalidate();
            return;
        }

        _timer.Stop();
        Hide();
        // One compositor frame so the badge is gone before the screen is frozen.
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

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(CardFill);

        using (var edge = new Pen(CardEdge, Math.Max(1f, _scale)))
        {
            var half = edge.Width / 2f;
            g.DrawPath(edge, Rounded(new RectangleF(half, half, Width - edge.Width, Height - edge.Width),
                (int)Math.Round(11.5f * _scale)));
        }

        using var font = new Font("Segoe UI", 14f, FontStyle.Bold, GraphicsUnit.Point);
        TextRenderer.DrawText(g, _remaining.ToString(), font, ClientRectangle, CardInk,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }

    private static GraphicsPath Rounded(Rectangle bounds, int radius) =>
        Rounded(new RectangleF(bounds.X, bounds.Y, bounds.Width, bounds.Height), radius);

    private static GraphicsPath Rounded(RectangleF bounds, int radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2f, Math.Min(bounds.Width, bounds.Height));
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
