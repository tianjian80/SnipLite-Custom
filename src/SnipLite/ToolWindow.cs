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
