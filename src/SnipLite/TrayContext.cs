namespace SnipLite;

internal sealed class TrayContext : ApplicationContext
{
    private readonly AppSettings _settings;
    private readonly NotifyIcon _tray;
    private readonly HotkeyHost _hotkeys = new();
    private readonly ThemeMonitor _themeMonitor = new();
    private readonly Localization.LanguageMonitor _languageMonitor;
    private readonly CaptureHistory _history = new();
    private CaptureForm? _overlay;
    private PreferencesForm? _prefs;
    private bool _editingHotkey;
    private string _hotkeySignature = "";
    private string _menuSignature = "";
    private uint? _lastPasteSequence;

    public TrayContext(bool captureOnStart = false, int openPreferencesTab = -1, bool openMenu = false)
    {
        _settings = AppSettings.Load();
        Localization.LanguageService.Configure(_settings.DisplayLanguage);
        _tray = new NotifyIcon { Icon = TrayIcons.Create(_settings), Text = "SnipLite", Visible = true };
        AppTheme.Changed += RefreshTheme;
        _tray.DoubleClick += (_, _) => StartCapture(CaptureMode.Snip);
        ApplySettings();
        _languageMonitor = new Localization.LanguageMonitor(ApplySettings);
        _languageMonitor.Start();
        void Once(Action action)
        {
            var timer = new System.Windows.Forms.Timer { Interval = 250 };
            timer.Tick += (_, _) => { timer.Stop(); timer.Dispose(); action(); }; timer.Start();
        }
        if (captureOnStart) Once(() => StartCapture(CaptureMode.Snip));
        if (openPreferencesTab >= 0) Once(() => ShowPreferences(openPreferencesTab));
        if (openMenu) Once(() => _tray.ContextMenuStrip?.Show(200, 200));
        if (_settings.KeepResponsive) _ = Task.Run(() =>
        {
            try { Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages(); } catch { }
        });
        AppLog.Write($"SnipLite 已启动 version={AppInfo.Version} exe={Application.ExecutablePath}");
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = SnipMenu.Create(); menu.Font = _settings.CreateInterfaceFont();
        menu.Items.Add(SnipMenu.Item("截屏", () => StartCapture(CaptureMode.Snip), HotkeyDefinitions.Format(_settings.CaptureHotkey)));
        if (_settings.EnhancedTrayMenu)
        {
            menu.Items.Add(SnipMenu.Item("截屏并自动复制", () => StartCapture(CaptureMode.Snip, true), HotkeyDefinitions.Format(_settings.CopyCaptureHotkey)));
            menu.Items.Add(SnipMenu.Item("全屏截图并自动保存", CustomCapture, HotkeyDefinitions.Format(_settings.CustomCaptureHotkey)));
        }
        menu.Items.Add(SnipMenu.Item("贴图 / 恢复贴图", PasteFromClipboard, HotkeyDefinitions.Format(_settings.PinHotkey)));
        menu.Items.Add(SnipMenu.Item("隐藏 / 显示所有贴图", PinForm.ToggleAll, HotkeyDefinitions.Format(_settings.TogglePinsHotkey)));
        if (_settings.EnhancedTrayMenu)
        {
            menu.Items.Add(SnipMenu.Item("切换到另一贴图分组", PinForm.SwitchGroup, HotkeyDefinitions.Format(_settings.SwitchGroupHotkey)));
            menu.Items.Add(SnipMenu.Item("恢复历史贴图", () => PinForm.RestoreLast(_settings)));
            menu.Items.Add(SnipMenu.Item("关闭全部贴图", PinForm.CloseAll));
            var history = new ToolStripMenuItem(UiText.T("截屏历史"));
            history.DropDownOpening += (_, _) =>
            {
                foreach (ToolStripItem item in history.DropDownItems) item.Dispose();
                history.DropDownItems.Clear();
                foreach (var entry in _history.Entries)
                    history.DropDownItems.Add(SnipMenu.Item(entry.Label, () => PinImage(entry.Image(), entry.Bounds)));
                if (history.DropDownItems.Count == 0) history.DropDownItems.Add(new ToolStripMenuItem(UiText.T("暂无截屏历史")) { Enabled = false });
            };
            menu.Items.Add(history);
            menu.Items.Add(SnipMenu.Item("清空截屏历史", _history.Clear));
        }
        menu.Items.Add(new ToolStripSeparator());
        var disable = SnipMenu.Item("禁用快捷键", () => { _settings.HotkeysEnabled = !_settings.HotkeysEnabled; _settings.Save(); ApplySettings(); });
        menu.Items.Add(disable);
        menu.Opening += (_, _) => disable.Checked = !_settings.HotkeysEnabled;
        menu.Items.Add(SnipMenu.Item("文字识别", () => StartCapture(CaptureMode.Ocr), HotkeyDefinitions.Format(_settings.OcrHotkey)));
        menu.Items.Add(SnipMenu.Item("二维码识别", () => StartCapture(CaptureMode.Qr), HotkeyDefinitions.Format(_settings.QrHotkey)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(SnipMenu.Item("首选项…", () => ShowPreferences(0)));
        menu.Items.Add(SnipMenu.Item("关于", () => ShowPreferences(7)));
        menu.Items.Add(SnipMenu.Item("重新启动", Restart));
        menu.Items.Add(SnipMenu.Item("退出", Exit));
        UiText.Apply(menu, _settings.DisplayLanguage);
        return menu;
    }

    private void ShowPreferences(int tab)
    {
        if (_prefs is { IsDisposed: false }) { _prefs.Activate(); _prefs.SelectTab(tab); return; }
        _prefs = new PreferencesForm(_settings, tab);
        _prefs.Applied += ApplySettings;
        _prefs.HotkeyAvailable = _hotkeys.CanRegister;
        _prefs.HotkeyEditing += editing => { _editingHotkey = editing; _hotkeySignature = ""; ApplyHotkeys(); };
        _prefs.FormClosed += (_, _) => { _prefs = null; _editingHotkey = false; _hotkeySignature = ""; ApplyHotkeys(); };
        _prefs.Show();
    }
    private void ApplySettings()
    {
        Localization.LanguageService.Configure(_settings.DisplayLanguage);
        Toast.Notifications = _settings.ShowToast; AppLog.Level = _settings.LogLevel;
        var icon = TrayIcons.Create(_settings); var oldIcon = _tray.Icon; _tray.Icon = icon; oldIcon?.Dispose();
        var tip = Localization.LanguageService.Format("SnipLite {0} | {1} 截图 | {2} 贴图", AppInfo.Version, HotkeyDefinitions.Format(_settings.CaptureHotkey), HotkeyDefinitions.Format(_settings.PinHotkey));
        _tray.Text = tip[..Math.Min(tip.Length, 63)];
        var signature = $"{_settings.EnhancedTrayMenu}|{Localization.LanguageService.CurrentLanguage}|{_settings.InterfaceFont}|{_settings.InterfaceFontSize}|" + string.Join(',', HotkeyDefinitions.Items.Select(item => typeof(AppSettings).GetProperty(item.Name)!.GetValue(_settings)));
        if (_menuSignature != signature)
        {
            var oldMenu = _tray.ContextMenuStrip; _tray.ContextMenuStrip = BuildMenu(); _menuSignature = signature;
            // Wait until ToolStrip's closing stack has unwound before disposal.
            if (oldMenu is not null) SynchronizationContext.Current?.Post(_ => oldMenu.Dispose(), null);
        }
        _history.Trim(_settings.HistoryImageLimit);
        PinForm.TrimHistory(_settings.HistoryImageLimit);
        PinForm.ApplySettingsToAll(); ApplyHotkeys();
    }
    private void ApplyHotkeys()
    {
        var signature = $"{_settings.HotkeysEnabled}|{_editingHotkey}|" + string.Join(',', HotkeyDefinitions.Items.Select(item => typeof(AppSettings).GetProperty(item.Name)!.GetValue(_settings)));
        if (signature == _hotkeySignature) return;
        _hotkeySignature = signature; _hotkeys.UnregisterAll(); _settings.HotkeyErrors.Clear();
        if (!_settings.HotkeysEnabled || _editingHotkey) return;
        var handlers = new Dictionary<string, Action>
        {
            [nameof(_settings.CaptureHotkey)] = () => StartCapture(CaptureMode.Snip),
            [nameof(_settings.CopyCaptureHotkey)] = () => StartCapture(CaptureMode.Snip, true),
            [nameof(_settings.CustomCaptureHotkey)] = CustomCapture,
            [nameof(_settings.PinHotkey)] = PasteFromClipboard,
            [nameof(_settings.TogglePinsHotkey)] = PinForm.ToggleAll,
            [nameof(_settings.SwitchGroupHotkey)] = PinForm.SwitchGroup,
            [nameof(_settings.MouseThroughHotkey)] = PinForm.ToggleMouseThroughActive,
            [nameof(_settings.TranslationHotkey)] = () => StartCapture(CaptureMode.Translation),
            [nameof(_settings.OcrHotkey)] = () => StartCapture(CaptureMode.Ocr),
            [nameof(_settings.QrHotkey)] = () => StartCapture(CaptureMode.Qr)
        };
        // "截屏并自动复制" registers before the plain capture hotkey: if both were
        // assigned the same key (e.g. F1, which the plain capture owns by default),
        // the copy-on-release feature wins instead of silently losing the key.
        var order = HotkeyDefinitions.Items
            .OrderBy(item => item.Name == nameof(_settings.CopyCaptureHotkey) ? 0 : 1);
        var used = new Dictionary<Keys, string>();
        foreach (var (label, name) in order)
        {
            var binding = (Keys)typeof(AppSettings).GetProperty(name)!.GetValue(_settings)!;
            if (binding == Keys.None || name == nameof(AppSettings.CopyCoordinatesHotkey)) continue;
            if (used.TryGetValue(binding, out var owner))
            {
                _settings.HotkeyErrors[name] = "与其他快捷键重复";
                AppLog.Write($"快捷键重复：{label} 与 {owner} 同为 {HotkeyDefinitions.Format(binding)}");
                continue;
            }
            used[binding] = label;
            if (_hotkeys.Register(binding, handlers[name]))
            {
                AppLog.Write($"快捷键已注册：{label} {HotkeyDefinitions.Format(binding)}");
                continue;
            }
            used.Remove(binding);
            _settings.HotkeyErrors[name] = "已被其他程序占用";
            AppLog.Write($"快捷键注册失败：{label} {HotkeyDefinitions.Format(binding)}");
            Toast.Error(Localization.LanguageService.Format("{0}快捷键 {1} 已被占用", UiText.T(label), HotkeyDefinitions.Format(binding)));
        }
    }
    /// <summary>
    /// Silent full-screen capture of the monitor under the pointer, with no overlay
    /// (an overlay from a hotkey used to leave a stuck transparent window). Full
    ///screen games/apps cover the whole physical monitor, so their capture uses
    /// <see cref="Screen.Bounds"/>; ordinary windows use <see cref="Screen.WorkingArea"/>
    /// so the taskbar is excluded. The pointer is never drawn.
    /// </summary>
    private void CustomCapture()
    {
        try
        {
            var screen = Screen.FromPoint(Cursor.Position);
            var bounds = IsFullScreenForeground(screen) ? screen.Bounds : screen.WorkingArea;
            using var image = new Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(image))
                g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);

            var path = Services.SaveCaptureFile(image, _settings, _settings.QuickSaveDirectory);
            AppLog.Write($"全屏截图并自动保存：{(bounds == screen.Bounds ? "全屏应用" : "桌面工作区")} {bounds.Width}×{bounds.Height} → {path}");
            if (_settings.QuickSaveToast) Toast.Show(UiText.T("已保存：") + Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            AppLog.Write("全屏截图并自动保存失败", ex);
            Toast.Error(UiText.T("截图输出失败：") + ex.Message);
        }
    }

    /// <summary>
    /// True when the active window covers the monitor's entire physical bounds and is
    /// not the desktop shell (Progman / WorkerW), i.e. a full-screen game or app.
    /// </summary>
    private static bool IsFullScreenForeground(Screen screen)
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return false;

        var name = new System.Text.StringBuilder(64);
        GetClassName(window, name, name.Capacity);
        if (name.ToString() is "Progman" or "WorkerW") return false;

        if (!GetWindowRect(window, out var rect)) return false;
        return rect.Left <= screen.Bounds.Left && rect.Top <= screen.Bounds.Top &&
               rect.Right >= screen.Bounds.Right && rect.Bottom >= screen.Bounds.Bottom;
    }
    private void StartCapture(CaptureMode mode, bool forceCopy = false, Rectangle? preset = null)
    {
        if (_overlay is { IsDisposed: false, Visible: true }) { _overlay.Activate(); return; }
        _overlay?.Dispose();
        try
        {
            if (forceCopy) AppLog.Write("截屏并自动复制：打开遮罩，框选松手即复制并退出");
            var overlay = new CaptureForm(SystemInformation.VirtualScreen, mode, _settings, preset, forceCopy); _overlay = overlay;
            overlay.Done += result =>
            {
                if (result is null || !result.KeepOpen) if (ReferenceEquals(_overlay, overlay)) _overlay = null;
                if (result is not null) HandleResult(result, forceCopy);
            };
            overlay.FormClosed += (_, _) => { if (ReferenceEquals(_overlay, overlay)) _overlay = null; };
            overlay.Show();
        }
        catch (Exception ex) { _overlay?.Dispose(); _overlay = null; Program.Report(ex); }
    }
    private void HandleResult(CaptureResult result, bool forceCopy)
    {
        var transferred = false;
        try
        {
            // The auto-copy overlay already wrote the bitmap to the clipboard, so the
            // tray must not touch history or auto-save (those could fail and used to
            // abort the whole result handling). Just dispose the bitmap.
            if (result.AlreadyCopied && forceCopy)
            {
                AppLog.Write("截屏并自动复制：已由遮罩写入剪贴板，跳过历史与自动保存");
                return;
            }
            _history.Add(result.Image, result.Bounds, _settings.HistoryImageLimit);
            if (_settings.AutoSave) Services.SaveCaptureFile(result.Image, _settings, _settings.SaveDirectory);
            if (result.Pin || result.Action == CaptureAction.Pin)
            { PinImage(result.Image, result.Bounds); transferred = true; }
            else if (result.Action == CaptureAction.QuickSave)
            {
                var path = Services.SaveCaptureFile(result.Image, _settings, _settings.QuickSaveDirectory);
                if (_settings.QuickSaveToast) Toast.Show(UiText.T("已保存：") + Path.GetFileName(path));

            }
            else if (!result.AlreadyCopied && (forceCopy || result.Action == CaptureAction.Copy || _settings.CopyAfterCapture))
            {
                Services.CopyImage(result.Image, _settings.CaptureBitmapClipboard, _settings);
                if (_history.Entries.Count > 0) _lastPasteSequence = GetClipboardSequenceNumber();

                Toast.Show(Localization.LanguageService.Format("已复制  {0} × {1}", result.Image.Width, result.Image.Height));
            }
        }
        catch (Exception ex) { AppLog.Write("截图输出失败", ex); Toast.Error(UiText.T("截图输出失败：") + ex.Message); }
        finally { if (!transferred) result.Image.Dispose(); }
    }
    private void PinImage(Bitmap image, Rectangle? bounds = null, bool stagger = false)
    {
        Point? point = _settings.PinAtCapturePosition && bounds is Rectangle r ? new Point(r.X + r.Width / 2, r.Y + r.Height / 2) : null;
        var pin = new PinForm(image, point, _settings.PinOpacityPercent, _settings);
        if (stagger) pin.ShowBesideExisting(); else pin.Show();
    }
    private void PasteFromClipboard()
    {
        if (TryPinActiveCapture(_overlay)) return;
        if (PinForm.RestoreHidden()) return;
        var sequence = GetClipboardSequenceNumber();
        var newClipboard = _lastPasteSequence != sequence;
        _lastPasteSequence = sequence;
        try
        {
            var image = newClipboard ? Services.ClipboardImage() : null;
            if (image is not null) { PinImage(image, stagger: true); return; }
            if (newClipboard && _settings.TextToImage && Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();
                if (!_settings.IgnoreFilePaths || !TextImageRenderer.IsFilePath(text))
                {
                    var html = !_settings.IgnoreHtml && Clipboard.ContainsText(TextDataFormat.Html) ? Clipboard.GetText(TextDataFormat.Html) : null;
                    PinImage(TextImageRenderer.Render(text, html, _settings), stagger: true); return;
                }
            }
            if (PinForm.RestoreLast(_settings)) return;
            if (PinForm.ActivateExisting()) return;
            Toast.Show(UiText.T("剪贴板中没有可贴出的内容，也没有可恢复的贴图"));
        }
        catch (Exception ex) { AppLog.Write("贴图失败", ex); Toast.Error(UiText.T("贴图失败：") + ex.Message); }
    }

    internal static bool TryPinActiveCapture(CaptureForm? overlay)
    {
        if (overlay is not { IsDisposed: false, Visible: true }) return false;
        // An active capture owns the key even before a selection is complete.
        // Never paste old clipboard contents over the capture overlay.
        overlay.TryPinSelection();
        return true;
    }
    private void Restart()
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath) { Arguments = "--wait-for-instance", UseShellExecute = true });
        Exit();
    }
    private void Exit()
    {
        AppTheme.Changed -= RefreshTheme;
        _themeMonitor.Dispose();
        _languageMonitor.Dispose();
        _tray.Visible = false; var icon = _tray.Icon; _tray.Dispose(); icon?.Dispose();
        _overlay?.Close(); _prefs?.Close(); _hotkeys.Dispose(); PinForm.CloseAllForExit(); ExitThread();
    }
    private void RefreshTheme()
    {
        var old = _tray.Icon;
        _tray.Icon = TrayIcons.Create(_settings);
        old?.Dispose();
        _overlay?.Invalidate();
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int length);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out RECT rect);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }
}
