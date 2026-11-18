namespace SnipLite;

/// <summary>
/// Base for every background/host window: forced WS_EX_TOOLWINDOW and no
/// WS_EX_APPWINDOW, so Windows can never add a taskbar button or Alt+Tab entry.
/// </summary>
internal class ToolWindow : Form
{
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
}
