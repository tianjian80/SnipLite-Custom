using System.Runtime.InteropServices;

namespace SnipLite;

/// <summary>
/// Base for every background/host window: forced WS_EX_TOOLWINDOW, no
/// WS_EX_APPWINDOW, and an explicit ITaskbarList.DeleteTab so the shell can
/// never keep a taskbar button for it.
/// </summary>
internal class ToolWindow : Form
{
    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    private class TaskbarList { }

    [ComImport, Guid("56FDF342-FD6D-11d0-958A-006097C9A090"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
    }

    private static readonly ITaskbarList? Shell = CreateShell();

    private static ITaskbarList? CreateShell()
    {
        try
        {
            var list = (ITaskbarList)new TaskbarList();
            list.HrInit();
            return list;
        }
        catch
        {
            return null;
        }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80;      // WS_EX_TOOLWINDOW
            cp.ExStyle &= ~0x40000;  // remove WS_EX_APPWINDOW
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RemoveTaskbarButton();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        RemoveTaskbarButton();
    }

    private void RemoveTaskbarButton()
    {
        HideTaskbarTab(Handle);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    /// <summary>
    /// Moves a tool/drop-down window to an exact screen position. WinForms may
    /// reposition drop-downs again, so this talks to the window directly.
    /// </summary>
    internal static void PlaceWindow(IntPtr handle, int x, int y)
    {
        try
        {
            const uint SwpNoSize = 0x0001, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010;
            SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
        }
        catch { }
    }

    /// <summary>
    /// Strips the taskbar button from any window, including WinForms drop-downs that
    /// are not ToolWindow instances. Safe to call repeatedly.
    /// </summary>
    internal static void HideTaskbarTab(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        try
        {
            const int GwlExstyle = -20;
            const int WsExToolWindow = 0x80;
            const int WsExAppWindow = 0x40000;
            var style = GetWindowLong(handle, GwlExstyle);
            style |= WsExToolWindow;
            style &= ~WsExAppWindow;
            SetWindowLong(handle, GwlExstyle, style);
        }
        catch { }
        try { Shell?.DeleteTab(handle); } catch { }
    }
}
