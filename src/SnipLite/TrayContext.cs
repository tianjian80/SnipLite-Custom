namespace SnipLite;

internal sealed class TrayContext : ApplicationContext
{
    private readonly AppSettings _settings;
    private readonly NotifyIcon _tray;
    private ContextMenuStrip? _trayMenu;
    private readonly HotkeyHost _hotkeys = new();
    private RecordingForm? _recorder;
    private readonly System.Windows.Forms.Timer _recordWatch = new() { Interval = 120 };
    private bool _recordingIcon;

    /// <summary>Red tray glyph while any recording (screen or GIF) is running.</summary>
    private void SyncRecordingIcon()
    {
        var active = RecordingSession.Current is not null;
        if (active == _recordingIcon || _trayDisposed) return;
        _recordingIcon = active;
        var old = _tray.Icon;
        _tray.Icon = TrayIcons.Create(_settings, active);
        old?.Dispose();
    }
    private readonly ThemeMonitor _themeMonitor = new();
    private readonly Localization.LanguageMonitor _languageMonitor;
    private readonly CaptureHistory _history = new();
    /// <summary>
    /// Scrollable thumbnail list for the history dropdown. Cards keep a 16:9 shape,
    /// the wheel scrolls smoothly and no native scrollbar is shown.
    /// </summary>
    private sealed class HistoryPanel : Control
    {
        private const int Pad = 6;
        private const int Gap = 6;
        private readonly IReadOnlyList<CaptureHistory.Entry> _entries;
        private readonly int _cardWidth;
        private readonly int _cardHeight;
        private readonly Action<CaptureHistory.Entry> _click;
        private int _offset;
        private int _hover = -1;

        public HistoryPanel(IReadOnlyList<CaptureHistory.Entry> entries, Size size, int cardWidth, int cardHeight, Action<CaptureHistory.Entry> click)
        {
            _entries = entries;
            _cardWidth = cardWidth;
            _cardHeight = cardHeight;
            _click = click;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = SnipMenuRenderer.Back;
            Size = size;
            TabStop = true;
        }

        private int Stride => CardHeight + Gap;
        /// <summary>
        /// Effective card height: the constructor's 16:9 height, capped so the visible
        /// cards always fit the hosted client area exactly (the drop-down keeps a
        /// couple of pixels for its own border, which used to trigger ▲ / ▼ bars).
        /// </summary>
        private int CardHeight
        {
            get
            {
                var count = Math.Max(1, Math.Min(_entries.Count, 5));
                var fit = (Height - Pad * 2 - (count - 1) * Gap) / count;
                return Math.Min(_cardHeight, Math.Max(24, fit));
            }
        }
        private int ContentHeight
        {
            get
            {
                var card = CardHeight;
                return Pad * 2 + _entries.Count * card + Math.Max(0, _entries.Count - 1) * Gap;
            }
        }

        private int IndexAt(int y)
        {
            y += _offset - Pad;
            if (y < 0) return -1;
            var index = y / Stride;
            if (index >= _entries.Count || y % Stride >= CardHeight) return -1;
            return index;
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Focus(); }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var hit = IndexAt(e.Y);
            Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
            if (hit == _hover) return;
            _hover = hit;
            Invalidate();
        }
        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            Cursor = Cursors.Default;
            if (_hover < 0) return;
            _hover = -1;
            Invalidate();
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            var max = Math.Max(0, ContentHeight - Height);
            var next = Math.Clamp(_offset - e.Delta / 2, 0, max);
            if (next == _offset) return;
            _offset = next;
            Invalidate();
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            var hit = IndexAt(e.Y);
            if (hit >= 0) _click(_entries[hit]);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            var cardHeight = CardHeight;
            for (var index = 0; index < _entries.Count; index++)
            {
                var top = Pad + index * Stride - _offset;
                if (top + cardHeight < 0) continue;
                if (top > Height) break;
                var card = new Rectangle(Pad, top, _cardWidth, cardHeight);
                using (var canvas = new SolidBrush(Color.FromArgb(245, 245, 245))) g.FillRectangle(canvas, card);
                var thumb = _entries[index].Thumbnail;
                var scale = Math.Min((card.Width - 2) / (float)thumb.Width, (card.Height - 2) / (float)thumb.Height);
                var width = Math.Max(1, (int)Math.Round(thumb.Width * scale));
                var height = Math.Max(1, (int)Math.Round(thumb.Height * scale));
                var rect = new Rectangle(card.X + (card.Width - width) / 2, card.Y + (card.Height - height) / 2, width, height);
                g.DrawImage(thumb, rect);
                using var border = new Pen(index == _hover ? AppTheme.Current.Accent : Color.FromArgb(208, 208, 208), 1f);
                g.DrawRectangle(border, card.X, card.Y, card.Width - 1, card.Height - 1);
            }
        }
    }
    private CaptureForm? _overlay;
    private PreferencesForm? _prefs;
    private bool _editingHotkey;
    private string _hotkeySignature = "";
    private string _menuSignature = "";
    private bool _trayDisposed;
    private uint? _lastPasteSequence;

    public TrayContext(bool captureOnStart = false, int openPreferencesTab = -1, bool openMenu = false)
    {
        _settings = AppSettings.Load();
        Localization.LanguageService.Configure(_settings.DisplayLanguage);
        _tray = new NotifyIcon { Icon = TrayIcons.Create(_settings), Text = "SnipLite", Visible = true };
        // Watching the shared session keeps the red icon correct no matter which code
        // path started or stopped the recording (card, hotkey, duration limit, cancel).
        _recordWatch.Tick += (_, _) => SyncRecordingIcon();
        _recordWatch.Start();
        Application.ApplicationExit += (_, _) => DisposeTrayIcon();
        AppTheme.Changed += RefreshTheme;
        // The menu is shown manually so it can always open towards the right of
        // the cursor instead of letting WinForms flip it left in the right half.
        _tray.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) ShowTrayMenu(); };
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
        if (openMenu) Once(() => _trayMenu?.Show(new Point(200, 200)));
        if (_settings.KeepResponsive) _ = Task.Run(() =>
        {
            try { Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages(); } catch { }
        });
        AppLog.Write($"SnipLite 已启动 version={AppInfo.Version} exe={Application.ExecutablePath}");
    }

    private ContextMenuStrip BuildMenu()
    {
        // Compact tray panel: 9.75pt rows, thin padding and small glyphs.
        var menu = SnipMenu.CreateTray(compact: true);
        menu.Font = new Font(menu.Font.FontFamily, 9.75f);
        // Entries are added through the per-item flags from 首选项 → 布局 → 托盘菜单.
        void Add(bool visible, string text, Action action, string key = "")
        {
            if (visible) menu.Items.Add(SnipMenu.Item(text, action, key, verticalPadding: 3));
        }
        // Same as Add, but hands the item back so its Enabled state can follow the
        // history list (e.g. grey out 清空截屏历史 when there is nothing to clear).
        ToolStripMenuItem? AddItem(bool visible, string text, Action action)
        {
            if (!visible) return null;
            var item = SnipMenu.Item(text, action, verticalPadding: 3);
            menu.Items.Add(item);
            return item;
        }
        // An unbound hotkey shows nothing on the right instead of a "未设置" label.
        static string Key(Keys key) => key == Keys.None ? "" : HotkeyDefinitions.Format(key);

        Add(_settings.TrayItemSnip, "截屏", () => StartCapture(CaptureMode.Snip), Key(_settings.CaptureHotkey));
        Add(_settings.TrayItemDelay, "延时截图", StartDelayedCapture, Key(_settings.DelayCaptureHotkey));
        if (_settings.EnhancedTrayMenu)
        {
            Add(_settings.TrayItemSnipCopy, "截屏并自动复制", () => StartCapture(CaptureMode.Snip, true), Key(_settings.CopyCaptureHotkey));
            Add(_settings.TrayItemFullSave, "全屏截图并自动保存", CustomCapture, Key(_settings.CustomCaptureHotkey));
        }
        Add(_settings.TrayItemLong, "长截图", () => StartCapture(CaptureMode.Snip, longCapture: true), Key(_settings.LongCaptureHotkey));
        Add(_settings.TrayItemRecord, "录屏", () => StartCapture(CaptureMode.Record), Key(_settings.RecordHotkey));
        Add(_settings.TrayItemGif, "录制 GIF", () => StartCapture(CaptureMode.Gif), Key(_settings.GifHotkey));
        Add(_settings.TrayItemPin, "贴图 / 恢复贴图", PasteFromClipboard, Key(_settings.PinHotkey));
        Add(_settings.TrayItemTogglePins, "隐藏 / 显示所有贴图", PinForm.ToggleAll, Key(_settings.TogglePinsHotkey));
        if (_settings.EnhancedTrayMenu)
        {
            Add(_settings.TrayItemSwitchGroup, "切换到另一贴图分组", PinForm.SwitchGroup, Key(_settings.SwitchGroupHotkey));
            Add(_settings.TrayItemRestorePins, "恢复历史贴图", () => PinForm.RestoreLast(_settings));
            Add(_settings.TrayItemClosePins, "关闭全部贴图", PinForm.CloseAll);
            ToolStripMenuItem? clearHistory = null;
            if (_settings.TrayItemHistory)
            {
                var history = new ToolStripMenuItem(UiText.T("截屏历史"))
                {
                    // Always unfold to the right of the tray panel.
                    DropDownDirection = ToolStripDropDownDirection.Right
                };
                if (history.DropDown is ToolStripDropDownMenu drop)
                {
                    // The hosted panel owns padding, painting and scrolling.
                    drop.AutoSize = false;
                    drop.ShowImageMargin = false;
                    drop.ShowCheckMargin = false;
                    drop.Padding = Padding.Empty;
                    drop.Font = menu.Font;
                    drop.Renderer = menu.Renderer;
                    // The thumbnail popup is a plain drop-down, not a Form: strip its
                    // taskbar button at the window level as soon as it exists.
                    drop.HandleCreated += (_, _) => ToolWindow.HideTaskbarTab(drop.Handle);
                    // WinForms adds ▲ / ▼ overflow strips when the hosted item is even a
                    // pixel taller than the display rectangle. Hide them: the panel has
                    // its own wheel scrolling, and the bars clip the first/last card.
                    drop.Layout += (_, _) => HideScrollButtons(drop);
                }
                // Size the column to the real gap between the open tray menu and the
                // right screen edge; the panel scrolls instead of growing off-screen.
                void FitHistory()
                {
                    // Files deleted outside the app disappear from the list before it opens.
                    _history.PruneMissing();
                    var dropMenu = (ToolStripDropDownMenu)history.DropDown!;
                    var screen = menu.Bounds.Width > 0
                        ? Screen.FromRectangle(menu.Bounds).WorkingArea
                        : Screen.FromPoint(Cursor.Position).WorkingArea;
                    var available = menu.Bounds.Width > 0 ? screen.Right - menu.Bounds.Right : 240;
                    if (menu.Visible && available < 220)
                    {
                        menu.Left = Math.Max(screen.Left, screen.Right - 240 - menu.Width);
                        available = screen.Right - menu.Bounds.Right;
                    }
                    available = Math.Max(160, available);
                    var cardWidth = Math.Max(120, available - 12);
                    var cardHeight = Math.Max(68, (int)Math.Round(cardWidth * 9 / 16.0));
                    var entries = _history.Entries;
                    var contentHeight = 12 + entries.Count * cardHeight + Math.Max(0, entries.Count - 1) * 6;
                    // At most five cards are visible; the rest scroll with the wheel.
                    var maxHeight = Math.Min(5 * cardHeight + 36, screen.Height - 16);
                    var height = Math.Min(contentHeight, maxHeight);
                    var signature = string.Join('|', entries.Select(entry => entry.Time.Ticks));
                    var key = $"{signature}@{available}x{cardHeight}";
                    if (history.Tag as string == key) return;
                    history.Tag = key;
                    var stale = history.DropDownItems.Cast<ToolStripItem>().ToArray();
                    history.DropDownItems.Clear();
                    foreach (var item in stale) item.Dispose();
                    if (entries.Count == 0)
                    {
                        history.DropDownItems.Add(new ToolStripMenuItem(UiText.T("暂无截屏历史")) { Enabled = false });
                        dropMenu.Size = new Size(available, 40);
                        return;
                    }
                    // The hosted item must stay a couple of pixels shorter than the
                    // drop-down: ToolStripDropDownMenu reserves that strip for its own
                    // border and would otherwise enable its ▲ / ▼ overflow bars.
                    var panelHeight = Math.Max(60, height - 4);
                    var panel = new HistoryPanel(entries, new Size(available, panelHeight), cardWidth, cardHeight, entry =>
                    {
                        // The file may have been deleted after the menu was filled.
                        if (entry.FilePath is { Length: > 0 } path && !File.Exists(path))
                        {
                            _history.Remove(entry);
                            history.DropDown?.Close();
                            menu.Close();
                            return;
                        }
                        history.DropDown?.Close();
                        menu.Close();
                        PinImage(entry.Image(), entry.Bounds);
                    });
                    history.DropDownItems.Add(new ToolStripControlHost(panel)
                    {
                        AutoSize = false,
                        Size = new Size(available, panelHeight),
                        Margin = Padding.Empty,
                        Padding = Padding.Empty
                    });
                    dropMenu.Size = new Size(available, height);
                }
                menu.Opening += (_, _) => FitHistory();
                // Grey out both history entries when nothing is left: a disabled item
                // never opens its drop-down, so no empty box / stray arrow appears.
                menu.Opening += (_, _) =>
                {
                    var any = _history.Entries.Count > 0;
                    history.Enabled = any;
                    if (clearHistory is not null) clearHistory.Enabled = any;
                };
                // Re-check right after showing: the real menu bounds and screen gap are
                // known then, so the submenu always ends flush with the screen edge.
                history.DropDownOpening += (_, _) =>
                {
                    FitHistory();
                    if (_history.Entries.Count == 0) history.Enabled = false;
                };
                // Bottom-align the panel with the tray menu (it grows upwards) and keep
                // its right edge exactly on the screen edge.
                history.DropDownOpened += (_, _) =>
                {
                    var dropDown = history.DropDown;
                    if (dropDown is null) return;
                    ToolWindow.HideTaskbarTab(dropDown.Handle);
                    HideScrollButtons(dropDown);
                    var screen = Screen.FromRectangle(menu.Bounds).WorkingArea;
                    var top = Math.Max(screen.Top, menu.Bounds.Bottom - dropDown.Height);
                    dropDown.Location = new Point(menu.Bounds.Right, top);
                };
                menu.Items.Add(history);
            }
            clearHistory = AddItem(_settings.TrayItemClearHistory, "清空截屏历史", _history.Clear);
        }
        menu.Items.Add(new ToolStripSeparator());
        var disable = SnipMenu.Item("禁用快捷键", () => { _settings.HotkeysEnabled = !_settings.HotkeysEnabled; _settings.Save(); ApplySettings(); }, verticalPadding: 3);
        if (_settings.TrayItemDisableHotkeys)
        {
            menu.Items.Add(disable);
            menu.Opening += (_, _) => disable.Checked = !_settings.HotkeysEnabled;
        }
        Add(_settings.TrayItemOcr, "文字识别", () => StartCapture(CaptureMode.Ocr), Key(_settings.OcrHotkey));
        Add(_settings.TrayItemQr, "二维码识别", () => StartCapture(CaptureMode.Qr));
        menu.Items.Add(new ToolStripSeparator());
        // 退出 is permanent: hiding every entry must never lock the user out.
        Add(_settings.TrayItemPreferences, "首选项…", () => ShowPreferences(0), Key(_settings.PreferencesHotkey));
        Add(_settings.TrayItemAbout, "关于", () => ShowPreferences(9));
        Add(_settings.TrayItemRestart, "重新启动", Restart);
        menu.Items.Add(SnipMenu.Item("退出", Exit, verticalPadding: 3));
        TrimSeparators(menu.Items);
        UiText.Apply(menu, _settings.DisplayLanguage);
        return menu;
    }

    /// <summary>
    /// Hides the internal ▲ / ▼ overflow strips that a drop-down creates when its
    /// hosted item is a pixel taller than the display rectangle. Their items are
    /// private, so they are located through the type hierarchy. The hosted panel
    /// keeps handling the mouse wheel itself.
    /// </summary>
    private static void HideScrollButtons(ToolStripDropDown drop)
    {
        try
        {
            SetPrivate(drop, "RequiresScrollButtons", false);
            foreach (var name in new[] { "UpScrollButton", "DownScrollButton" })
            {
                switch (GetPrivate(drop, name))
                {
                    case ToolStripItem item:
                        item.Visible = false;
                        item.Available = false;
                        item.Height = 0;
                        break;
                    case Control control:
                        control.Visible = false;
                        control.Height = 0;
                        break;
                }
            }
        }
        catch
        {
            // Purely cosmetic: never let a layout quirk break the tray menu.
        }
    }

    private static object? GetPrivate(object target, string name)
    {
        for (var type = target.GetType(); type is not null; type = type.BaseType)
        {
            const System.Reflection.BindingFlags Flags =
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.DeclaredOnly;
            var property = type.GetProperty(name, Flags);
            if (property is not null) return property.GetValue(target);
            var field = type.GetField(name, Flags);
            if (field is not null) return field.GetValue(target);
        }
        return null;
    }

    private static void SetPrivate(object target, string name, object value)
    {
        for (var type = target.GetType(); type is not null; type = type.BaseType)
        {
            const System.Reflection.BindingFlags Flags =
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.DeclaredOnly;
            var property = type.GetProperty(name, Flags);
            if (property is { CanWrite: true })
            {
                property.SetValue(target, value);
                return;
            }
            var field = type.GetField(name, Flags);
            if (field is not null)
            {
                field.SetValue(target, value);
                return;
            }
        }
    }

    /// <summary>Drops separators left dangling when the items around them are hidden.</summary>
    private static void TrimSeparators(ToolStripItemCollection items)
    {
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (items[i] is not ToolStripSeparator) continue;
            if (i == 0 || i == items.Count - 1 || items[i + 1] is ToolStripSeparator) items.RemoveAt(i);
        }
    }

    /// <summary>
    /// Tray-icon menu only: positions the panel near the cursor but keeps 220px free on the
    /// right (218px thumbnail dropdown + gap) so the history submenu ends at the screen edge.
    /// Ordinary right-click menus are never repositioned.
    /// </summary>
    private void ShowTrayMenu()
    {
        var menu = _trayMenu;
        if (menu is null) return;
        menu.AutoClose = true;
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        var width = menu.GetPreferredSize(Size.Empty).Width;
        var x = Math.Min(Cursor.Position.X, area.Right - width - 240);
        x = Math.Max(area.Left, x);
        menu.Show(new Point(x, Cursor.Position.Y), ToolStripDropDownDirection.AboveRight);
        if (menu.IsHandleCreated) ToolWindow.HideTaskbarTab(menu.Handle);
        // A drop-down shown without a foreground owner never receives the
        // outside-click notification that closes it, so claim foreground for the
        // menu itself once its handle exists.
        if (menu.IsHandleCreated) Win32.SetForegroundWindow(menu.Handle);
    }

    /// <summary>The preferences hotkey toggles: visible window closes, otherwise it opens.</summary>
    private void TogglePreferences()
    {
        if (_prefs is { IsDisposed: false } prefs && prefs.Visible && prefs.WindowState != FormWindowState.Minimized)
        {
            prefs.Close();
            return;
        }
        ShowPreferences(0);
    }
    private void ShowPreferences(int tab)
    {
        if (_prefs is { IsDisposed: false })
        {
            if (!_prefs.Visible) _prefs.Show();
            if (_prefs.WindowState == FormWindowState.Minimized) _prefs.WindowState = FormWindowState.Normal;
            _prefs.Activate(); _prefs.SelectTab(tab); _prefs.BringToFront();
            Win32.SetForegroundWindow(_prefs.Handle);
            return;
        }
        _prefs = new PreferencesForm(_settings, tab);
        _prefs.Applied += ApplySettings;
        _prefs.ExitRequested = Exit;
        _prefs.HotkeyAvailable = _hotkeys.CanRegister;
        _prefs.HotkeyEditing += editing => { _editingHotkey = editing; _hotkeySignature = ""; ApplyHotkeys(); };
        _prefs.FormClosed += (_, _) => { _prefs = null; _editingHotkey = false; _hotkeySignature = ""; ApplyHotkeys(); };
        _prefs.Show();
        _prefs.Activate();
        Win32.SetForegroundWindow(_prefs.Handle);
    }
    private void ApplySettings()
    {
        Localization.LanguageService.Configure(_settings.DisplayLanguage);
        Toast.Notifications = _settings.ShowToast; AppLog.Level = _settings.LogLevel;
        var icon = TrayIcons.Create(_settings, _recordingIcon); var oldIcon = _tray.Icon; _tray.Icon = icon; oldIcon?.Dispose();
        var tip = Localization.LanguageService.Format("SnipLite {0} | {1} 截图 | {2} 贴图", AppInfo.Version, HotkeyDefinitions.Format(_settings.CaptureHotkey), HotkeyDefinitions.Format(_settings.PinHotkey));
        _tray.Text = tip[..Math.Min(tip.Length, 63)];
        var signature = $"{_settings.EnhancedTrayMenu}|{Localization.LanguageService.CurrentLanguage}|{_settings.InterfaceFont}|{_settings.InterfaceFontSize}|" + string.Join(',', HotkeyDefinitions.Items.Select(item => typeof(AppSettings).GetProperty(item.Name)!.GetValue(_settings)))
            + "|" + string.Join(',', typeof(AppSettings).GetProperties().Where(p => p.Name.StartsWith("TrayItem", StringComparison.Ordinal)).OrderBy(p => p.Name).Select(p => p.GetValue(_settings)));
        if (_menuSignature != signature)
        {
            var oldMenu = _trayMenu; _trayMenu = BuildMenu(); _menuSignature = signature;
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
            [nameof(_settings.PreferencesHotkey)] = TogglePreferences,
            [nameof(_settings.CopyCaptureHotkey)] = () => StartCapture(CaptureMode.Snip, true),
            [nameof(_settings.CustomCaptureHotkey)] = CustomCapture,
            [nameof(_settings.LongCaptureHotkey)] = () => StartCapture(CaptureMode.Snip, longCapture: true),
            [nameof(_settings.RecordHotkey)] = () => ToggleRecording(false),
            [nameof(_settings.GifHotkey)] = () => ToggleRecording(true),
            [nameof(_settings.PinHotkey)] = PasteFromClipboard,
            [nameof(_settings.TogglePinsHotkey)] = PinForm.ToggleAll,
            [nameof(_settings.SwitchGroupHotkey)] = PinForm.SwitchGroup,
            [nameof(_settings.TranslationHotkey)] = () => StartCapture(CaptureMode.Translation),
            [nameof(_settings.OcrHotkey)] = () => StartCapture(CaptureMode.Ocr),
            [nameof(_settings.RedactHotkey)] = RedactHotkey,
            [nameof(_settings.DelayCaptureHotkey)] = StartDelayedCapture
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
    /// <summary>
    /// Starts a screen recording of the active monitor at the configured resolution, or
    /// stops the session in progress (second press saves it). Recordings started this way
    /// show no floating chrome, so the hotkey is the only control.
    /// </summary>
    private void ToggleRecording(bool gif)
    {
        if (RecordingSession.Current is { } active)
        {
            active.Stop(true);
            return;
        }
        if (_recorder is not null) return;
        try
        {
            var region = Screen.FromPoint(Cursor.Position).WorkingArea;
            var showControls = gif ? _settings.GifShowTimer : _settings.RecShowTimer;
            var form = new RecordingForm(region, _settings, gif, showControls);
            form.FormClosed += (_, _) => _recorder = null;
            _recorder = form;
            form.Show();
        }
        catch (Exception ex)
        {
            AppLog.Write("录屏启动失败", ex);
            Toast.Error(UiText.T("录屏启动失败：") + ex.Message);
        }
    }

    private void CustomCapture()
    {
        try
        {
            var screen = Screen.FromPoint(Cursor.Position);
            // Toggle off skips window detection entirely (taskbar included);
            // on, only full-screen apps get Bounds, desktops use WorkingArea.
            var bounds = _settings.SmartFullScreenCapture && !IsFullScreenForeground(screen)
                ? screen.WorkingArea : screen.Bounds;
            using var image = new Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(image))
            {
                g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
                if (_settings.CaptureCursor) Win32.DrawCursor(g, bounds.Location);
            }

            // Every full-screen capture is its own history record too; keep the saved
            // path so deleting the file externally also clears the record.
            var path = Services.SaveCaptureFile(image, _settings, _settings.QuickSaveDirectory);
            _history.Add(image, bounds, _settings.HistoryImageLimit, path);
            var mode = !_settings.SmartFullScreenCapture ? "全屏（含任务栏）"
                : bounds == screen.Bounds ? "全屏应用" : "桌面工作区";
            AppLog.Write($"全屏截图并自动保存：{mode} {bounds.Width}×{bounds.Height} → {path}");
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
    private DelayCountdownForm? _countdown;
    private System.Windows.Forms.Timer? _delayTimer;

    /// <summary>
    /// Runs the configured delay (3/5/10 s) then grabs the full screen and saves
    /// silently. The badge is optional: with it off the wait leaves no footprint.
    /// </summary>
    private void StartDelayedCapture()
    {
        if (_overlay is { IsDisposed: false, Visible: true }) { _overlay.Activate(); return; }
        if (_countdown is { IsDisposed: false } || _delayTimer is not null) return;

        var seconds = _settings.DelayCaptureSeconds is 3 or 5 or 10 ? _settings.DelayCaptureSeconds : 5;
        if (!_settings.DelayShowCountdown)
        {
            var timer = new System.Windows.Forms.Timer { Interval = seconds * 1000 };
            _delayTimer = timer;
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                timer.Dispose();
                if (ReferenceEquals(_delayTimer, timer)) _delayTimer = null;
                DelayedFullScreenCapture();
            };
            timer.Start();
            return;
        }

        var form = new DelayCountdownForm(seconds);
        _countdown = form;
        form.Finished += () => { _countdown = null; DelayedFullScreenCapture(); };
        form.FormClosed += (_, _) => { if (ReferenceEquals(_countdown, form)) _countdown = null; };
        form.Show();
    }

    /// <summary>Full-screen grab honouring 智能排除任务栏, then save + history + toast.</summary>
    private void DelayedFullScreenCapture()
    {
        try
        {
            var screen = Screen.FromPoint(Cursor.Position);
            var bounds = _settings.SmartFullScreenCapture && !IsFullScreenForeground(screen)
                ? screen.WorkingArea : screen.Bounds;
            using var image = new Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(image))
            {
                g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
                if (_settings.CaptureCursor) Win32.DrawCursor(g, bounds.Location);
            }

            var path = Services.SaveCaptureFile(image, _settings, DefaultCaptureFolder());
            _history.Add(image, bounds, _settings.HistoryImageLimit, path);
            AppLog.Write($"延时截图：{bounds.Width}×{bounds.Height} → {path}");
            if (_settings.QuickSaveToast) Toast.Show(UiText.T("已保存：") + Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            AppLog.Write("延时截图失败", ex);
            Toast.Error(UiText.T("截图输出失败：") + ex.Message);
        }
    }

    /// <summary>Configured output folder, otherwise 图片, otherwise 桌面.</summary>
    private string DefaultCaptureFolder()
    {
        foreach (var candidate in new[] { _settings.SaveDirectory, _settings.QuickSaveDirectory })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try { Directory.CreateDirectory(candidate); return candidate; }
            catch { /* fall through to the user folders */ }
        }

        foreach (var folder in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                     Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
                 })
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            try { Directory.CreateDirectory(folder); return folder; }
            catch { }
        }

        return Path.GetTempPath();
    }

    /// <summary>Redacts the current selection, or starts a capture that redacts once framed.</summary>
    private void RedactHotkey()
    {
        if (_overlay is { IsDisposed: false, Visible: true }) { _overlay.RequestAutoRedact(); return; }
        StartCapture(CaptureMode.Snip, autoRedact: true);
    }

    private void StartCapture(CaptureMode mode, bool forceCopy = false, Rectangle? preset = null, bool longCapture = false, bool autoRedact = false)
    {
        if (_overlay is { IsDisposed: false, Visible: true }) { _overlay.Activate(); return; }
        _overlay?.Dispose();
        try
        {
            if (forceCopy) AppLog.Write("截屏并自动复制：打开遮罩，框选松手即复制并退出");
            var overlay = new CaptureForm(SystemInformation.VirtualScreen, mode, _settings, preset, forceCopy, longCapture, autoRedact); _overlay = overlay;
            overlay.Done += result =>
            {
                if (result is null || !result.KeepOpen) if (ReferenceEquals(_overlay, overlay)) _overlay = null;
                if (result is not null) HandleResult(result, forceCopy);
            };
            // Explicit save / save-as lives entirely inside the overlay: record it here.
            overlay.Saved += (image, bounds, path) =>
            {
                try { _history.Add(image, bounds, _settings.HistoryImageLimit, path); }
                catch (Exception ex) { AppLog.Write("记录截屏历史失败", ex); }
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
            // The auto-copy overlay already wrote the bitmap to the clipboard; record the
            // capture in history anyway, then skip the other output steps.
            if (result.AlreadyCopied && forceCopy)
            {
                if (!result.HistoryRecorded) _history.Add(result.Image, result.Bounds, _settings.HistoryImageLimit);
                AppLog.Write("截屏并自动复制：已由遮罩写入剪贴板，仅记录历史");
                return;
            }
            string? savedPath = null;
            if (_settings.AutoSave)
                savedPath = Services.SaveCaptureFile(result.Image, _settings, _settings.SaveDirectory);
            if (!result.HistoryRecorded) _history.Add(result.Image, result.Bounds, _settings.HistoryImageLimit, savedPath);
            if (result.Pin || result.Action == CaptureAction.Pin)
            { PinImage(result.Image, result.Bounds); transferred = true; }
            else if (result.Action == CaptureAction.QuickSave)
            {
                var path = Services.SaveCaptureFile(result.Image, _settings, _settings.QuickSaveDirectory);
                if (!result.HistoryRecorded && savedPath is null) _history.ReplaceLatestPath(path);
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
        _tray.Visible = false; DisposeTrayIcon();
        _overlay?.Close(); _prefs?.Close(); _hotkeys.Dispose(); PinForm.CloseAllForExit(); ExitThread();
    }

    /// <summary>Removes the tray icon immediately so Windows leaves no ghost behind.</summary>
    private void DisposeTrayIcon()
    {
        if (_trayDisposed) return;
        _trayDisposed = true;
        _recordWatch.Stop();
        _recordWatch.Dispose();
        var icon = _tray.Icon;
        _tray.Visible = false;
        _tray.Dispose();
        icon?.Dispose();
    }
    private void RefreshTheme()
    {
        var old = _tray.Icon;
        _tray.Icon = TrayIcons.Create(_settings, _recordingIcon);
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
