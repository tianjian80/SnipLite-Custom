namespace SnipLite;

internal static class TrayIcons
{
    public static Icon LoadCustom(string path)
    {
        if (Path.GetExtension(path).Equals(".ico", StringComparison.OrdinalIgnoreCase)) return new Icon(path, 32, 32);
        using var image = new Bitmap(path);
        using var bitmap = new Bitmap(image, new Size(32, 32));
        var handle = bitmap.GetHicon();
        try { using var borrowed = Icon.FromHandle(handle); return (Icon)borrowed.Clone(); }
        finally { DestroyIcon(handle); }
    }
    public static Icon Create(AppSettings settings, bool recording = false)
    {
        // While recording the tray icon is always the base glyph in record red, even
        // when the user picked a custom image, so the state is unmistakable.
        if (!recording && settings.TrayIconStyle == "Custom" && File.Exists(settings.CustomTrayIcon))
        { try { return LoadCustom(settings.CustomTrayIcon); } catch (Exception ex) { AppLog.Write("自定义托盘图标读取失败", ex); } }
        var iconSize = SystemInformation.SmallIconSize.Width;
        using var logo = AppIcon.Load(iconSize);
        using var bitmap = logo.ToBitmap();
        var palette = AppTheme.Current;
        var tint = recording ? Color.FromArgb(229, 57, 53)
            : settings.TrayIconStyle == "Color" ? settings.Accent
            : palette.HighContrast ? SystemColors.WindowText
            : palette.TaskbarDark ? Color.FromArgb(245, 245, 245) : Color.FromArgb(40, 40, 43);
        // Auto follows the taskbar, which Windows can theme separately from application windows.
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
                bitmap.SetPixel(x, y, Color.FromArgb(bitmap.GetPixel(x, y).A, tint));
        }
        var handle = bitmap.GetHicon();
        try { using var borrowed = Icon.FromHandle(handle); return (Icon)borrowed.Clone(); }
        finally { DestroyIcon(handle); }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
}
