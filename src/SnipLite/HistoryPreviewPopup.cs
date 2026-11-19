using System.Runtime.InteropServices;

namespace SnipLite;

/// <summary>
/// Borderless floating list of capture thumbnails, shown to the right of the tray
/// menu. It replaces the ToolStripDropDownMenu submenu, whose white frame and
/// automatic left flip near the screen edge could not be tamed. While a context
/// menu is open Windows routes every mouse message to that menu, so the popup
/// tracks the pointer through a low-level hook instead of ordinary control events.
/// </summary>
internal sealed class HistoryPreviewPopup : Form
{
    private const int Pad = 3;
    private const int Gap = 4;
    private static readonly Color HoverEdge = Color.FromArgb(0, 103, 192);

    private readonly IReadOnlyList<CaptureHistory.Entry> _entries;
    private readonly int[] _heights;
    private readonly int _cardWidth;
    private readonly Action<CaptureHistory.Entry> _click;
    private int _offset;
    private int _hover = -1;

    public HistoryPreviewPopup(IReadOnlyList<CaptureHistory.Entry> entries, int cardWidth, int maxHeight, Action<CaptureHistory.Entry> click)
    {
        _entries = entries;
        _cardWidth = cardWidth;
        _click = click;
        // Keep every picture complete: height follows the source ratio, width is finalW.
        _heights = entries
            .Select(entry => Math.Max(24, (int)Math.Round((double)entry.Thumbnail.Height * cardWidth / entry.Thumbnail.Width)))
            .ToArray();

        AutoScaleMode = AutoScaleMode.None;
        _hookProc = OnMouseHook;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = SnipMenuRenderer.Back;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        Width = cardWidth + Pad * 2 + 2;   // 1 px theme border on each side
        Height = Math.Min(ContentHeight, Math.Max(80, maxHeight));
    }

    /// <summary>No taskbar button, no Alt+Tab entry and no activation at all.</summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST: showing the preview
            // must never deactivate the open ContextMenuStrip (which would close it).
            cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00000008;
            return cp;
        }
    }

    /// <summary>Shown next to an open menu, so it must never steal focus.</summary>
    protected override bool ShowWithoutActivation => true;

    private int ContentHeight => Pad * 2 + _heights.Sum() + Math.Max(0, _entries.Count - 1) * Gap;

    private int TopOf(int index)
    {
        var top = Pad - _offset;
        for (var i = 0; i < index; i++) top += _heights[i] + Gap;
        return top;
    }

    private int IndexAt(int y)
    {
        var cursor = y + _offset - Pad;
        if (cursor < 0) return -1;
        var offset = 0;
        for (var i = 0; i < _heights.Length; i++)
        {
            if (cursor < offset + _heights[i]) return i;
            offset += _heights[i] + Gap;
        }
        return -1;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Painted in OnPaint together with the pictures: no background flash.
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

        // A single hairline border replaces the bulky white menu frame.
        using (var edge = new Pen(AppTheme.Current.Border, 1f))
            g.DrawRectangle(edge, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);

        var left = Pad + 1;
        for (var index = 0; index < _entries.Count; index++)
        {
            var top = TopOf(index);
            if (top + _heights[index] < 0) continue;
            if (top > ClientSize.Height) break;
            var thumb = _entries[index].Thumbnail;
            // Force 96 DPI: a high-DPI source would otherwise be scaled by GDI+ and
            // overflow the destination rectangle, cutting the right edge away.
            if (Math.Abs(thumb.HorizontalResolution - 96f) > 0.1f) thumb.SetResolution(96f, 96f);
            var scale = Math.Min(_cardWidth / (float)thumb.Width, _heights[index] / (float)thumb.Height);
            var width = Math.Max(1, (int)Math.Round(thumb.Width * scale));
            var height = Math.Max(1, (int)Math.Round(thumb.Height * scale));
            var rect = new Rectangle(left + (_cardWidth - width) / 2, top + (_heights[index] - height) / 2, width, height);
            // Pixel source rectangle: the whole picture, never a cropped edge.
            g.DrawImage(thumb, rect, 0, 0, thumb.Width, thumb.Height, GraphicsUnit.Pixel);
            if (index != _hover) continue;
            using var pen = new Pen(HoverEdge, 2f);
            g.DrawRectangle(pen, rect.X - 1, rect.Y - 1, rect.Width + 1, rect.Height + 1);
        }
    }

    // ------------------------------------------------------------ mouse tracking

    private const int WhMouseLl = 14;
    private const int WmMouseMove = 0x0200;
    private const int WmLButtonDown = 0x0201;
    private const int WmMouseWheel = 0x020A;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookData
    {
        public Point32 Screen;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr Extra;
    }

    private delegate IntPtr MouseHookProc(int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hook, MouseHookProc proc, IntPtr module, uint thread);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? name);

    private readonly MouseHookProc _hookProc;
    private IntPtr _hook;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // A field keeps the delegate alive for as long as the hook is installed.
        _hook = SetWindowsHookEx(WhMouseLl, _hookProc, GetModuleHandle(null), 0);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
        base.OnFormClosed(e);
    }

    private IntPtr OnMouseHook(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && !IsDisposed)
        {
            var info = Marshal.PtrToStructure<MouseHookData>(data);
            var screen = new Point(info.Screen.X, info.Screen.Y);
            switch (message.ToInt32())
            {
                case WmMouseMove:
                    SetHover(screen);
                    break;
                case WmLButtonDown when Bounds.Contains(screen):
                    // Swallow the click: otherwise Windows dismisses the open menu first.
                    PostClick(screen);
                    return (IntPtr)1;
                case WmMouseWheel when Bounds.Contains(screen):
                    ScrollBy(-(short)(info.MouseData >> 16));
                    return (IntPtr)1;
            }
        }
        return CallNextHookEx(_hook, code, message, data);
    }

    private void SetHover(Point screen)
    {
        var hit = Bounds.Contains(screen) ? IndexAt(PointToClient(screen).Y) : -1;
        if (hit == _hover) return;
        _hover = hit;
        Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    private void ScrollBy(int delta)
    {
        var max = Math.Max(0, ContentHeight - ClientSize.Height);
        var next = Math.Clamp(_offset + delta, 0, max);
        if (next == _offset) return;
        _offset = next;
        Invalidate();
    }

    private void PostClick(Point screen)
    {
        var entry = IndexAt(PointToClient(screen).Y);
        if (entry < 0) return;
        var chosen = _entries[entry];
        // Deferred: the hook must return before the menu is closed from inside it.
        BeginInvoke(() =>
        {
            if (IsDisposed) return;
            _click(chosen);
        });
    }
}
