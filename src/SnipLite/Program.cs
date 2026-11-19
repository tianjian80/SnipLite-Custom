namespace SnipLite;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Hook the crash handlers before anything else so a failure in static
        // initialisation or the very first theme pass is reported, not swallowed.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Fatal(e.Exception, "界面线程异常");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Fatal(e.ExceptionObject as Exception, "未处理异常");

        try
        {
            Start(args);
        }
        catch (Exception ex)
        {
            Fatal(ex, "启动异常");
        }
    }

    private static void Start(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // AppTheme.Refresh sets the colour mode once, from the saved skin. Calling
        // SetColorMode twice during startup aborted older builds.
        AppTheme.Refresh();

        if (args.Any(arg => arg.StartsWith("--startup-admin:")))
        {
            Environment.ExitCode = Startup.ConfigureAdminTask(args.Contains("--startup-admin:on"));
            return;
        }

        var startupSettings = AppSettings.Load();
        ThemeManager.Apply(startupSettings.Skin);
        Localization.LanguageService.Configure(startupSettings.DisplayLanguage);
        using var mutex = new Mutex(false, @"Global\SnipLite.SingleInstance");
        var waitForInstance = args.Contains("--wait-for-instance");
        try
        {
            if (!mutex.WaitOne(waitForInstance ? 5000 : 0))
            {
                // A hidden tray icon still keeps the app reachable: wake the running
                // instance and let it pop the preferences instead of starting a new one.
                if (!waitForInstance && WakeRunningInstance()) return;

                MessageBox.Show(Localization.LanguageService.Format("SnipLite 已有实例正在运行，本次没有启动新版。\n\n请先从托盘右键菜单退出旧版，再启动本文件。\n\n本文件版本：{0}\n本文件位置：{1}", AppInfo.Version, Application.ExecutablePath), UiText.T("SnipLite 已在运行"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
        }
        catch (AbandonedMutexException) { }

        // --capture starts a snip as soon as the message loop runs. It gives
        // other tools (and the test scripts) a way in when F1 belongs to
        // another screenshot program. --prefs[:N] and --menu open the
        // preferences dialog (optionally on tab N) and the tray menu for the
        // same reason.
        var prefsTab = -1;

        foreach (var arg in args)
        {
            if (!arg.StartsWith("--prefs", StringComparison.OrdinalIgnoreCase)) continue;

            prefsTab = 0;
            var colon = arg.IndexOf(':');

            if (colon > 0 && int.TryParse(arg[(colon + 1)..], out var index))
                prefsTab = Math.Clamp(index, 0, 7);
        }

        Application.Run(new TrayContext(
            args.Contains("--capture"),
            prefsTab,
            args.Contains("--menu")));
    }

    /// <summary>
    /// Signals the already running instance to open its preferences. Returns false
    /// when no such instance is listening (e.g. an older build).
    /// </summary>
    private static bool WakeRunningInstance()
    {
        try
        {
            using var signal = EventWaitHandle.OpenExisting(@"Global\SnipLite.Activate");
            signal.Set();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Startup or global crash: log the full stack to disk and show it once.</summary>
    private static void Fatal(Exception? ex, string title)
    {
        AppLog.Write(title, ex);
        var log = Path.Combine(Path.GetTempPath(), "SnipLite", "startup-error.log");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.AppendAllText(log,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {title} version={AppInfo.Version} exe={Application.ExecutablePath}\r\n{ex}\r\n\r\n");
        }
        catch
        {
            // logging must never throw
        }

        try
        {
            MessageBox.Show($"{title}\n\n{ex}\n\n日志：{log}", "SnipLite " + title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch
        {
            // Headless run: the log above is the only report.
        }
    }

    internal static void Report(Exception? ex)
    {
        AppLog.Write("未处理异常", ex);
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "SnipLite");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] version={AppInfo.Version} exe={Application.ExecutablePath}\r\n{ex}\r\n\r\n");
        }
        catch
        {
            // logging must never throw
        }

        try
        {
            Toast.Show(UiText.T("SnipLite 出错，详情见 %TEMP%\\SnipLite\\error.log"), 4000);
        }
        catch
        {
            // nothing else we can do
        }
    }
}
