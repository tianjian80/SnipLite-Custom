using System.Drawing.Drawing2D;

namespace SnipLite;

/// <summary>
/// Windows 11-style navigation and setting cards, bound directly to AppSettings.
/// </summary>
internal sealed partial class PreferencesForm : ToolWindow
{
    // ---- design tokens (96 dpi units) -------------------------------------
    private const int WinW = 960;
    private const int WinH = 720;
    private const int SidebarW = 182;
    private const int FooterH = 56;         // footer zone below the page
    private const int PagePad = 6;
    private const int LabelW = 244;
    private const int ControlGap = 12;
    private const int RowH = 58;
    private const int CheckH = 54;
    private const int CaptionH = 24;        // group caption
    private const int SectionGap = 22;      // between groups
    private const int HintH = 24;           // hint line
    private const int ControlH = 32;

    private static Color Page => AppTheme.Current.Window;
    private static Color Chrome => AppTheme.Current.Window;
    private static Color Line => AppTheme.Current.Border;
    private static Color Ink => AppTheme.Current.Text;
    private static Color Muted => AppTheme.Current.Muted;
    private static Color Hint => AppTheme.Current.Muted;
    private static Color Field => AppTheme.Current.Field;
    private static Color FieldEdge => AppTheme.Current.Border;
    private static readonly string[] TabIcons = { "settings", "palette", "capture", "video", "pin", "output", "keyboard", "layout", "update", "about" };

    private readonly string[] _tabs = { "常规", "界面", "截屏", "视频", "贴图", "输出", "控制", "布局", "更新", "关于" };
    private readonly List<(Rectangle Rect, int Index)> _tabRects = new();

    private readonly AppSettings _s;
    private sealed class BufferedPage : Panel
    {
        private const int WmHScroll = 0x0114;
        private const int WmSize = 0x0005;
        private const int SbHorz = 0;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowScrollBar(IntPtr hWnd, int wBar, bool show);

        public BufferedPage()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            AutoScroll = true;
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            // Clamp before the base layout: the owner rebuilds the page one message
            // loop later, and until then the stale cards must not create a
            // horizontal scroll extent.
            ClampChildrenToWidth();
            base.OnLayout(e);
            HideHorizontalBar();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmHScroll) return;

            base.WndProc(ref m);

            if (m.Msg == WmSize) HideHorizontalBar();
        }

        private void ClampChildrenToWidth()
        {
            var maxRight = ClientSize.Width;

            foreach (Control child in Controls)
            {
                if (child.Width <= 0) continue;

                var available = maxRight - child.Left;
                if (available > 0 && child.Width > available) child.Width = available;
            }
        }

        private bool _hiding;

        private void HideHorizontalBar()
        {
            if (!IsHandleCreated || IsDisposed || _hiding) return;

            _hiding = true;
            try
            {
                if (HorizontalScroll.Maximum != 0) HorizontalScroll.Maximum = 0;
                if (HorizontalScroll.Value != 0) HorizontalScroll.Value = 0;
                ShowScrollBar(Handle, SbHorz, false);
            }
            finally { _hiding = false; }
        }
    }
    private readonly Panel _page = new BufferedPage();
    private readonly Button _reset = new FluentButton();

    private float _ui = 1f;
    private int _selected;
    private int _startTab;
    private int _hoverTab = -1;
    private bool _captions;
    private int _groups;

    private int _y;          // current row top, page coordinates
    private int _labelX;     // x of the label column
    private int _controlX;   // x of the first control

    private bool _ready;
    private bool _relayoutQueued;
    private int _laidOutWidth = -1;

    /// <summary>Raised after an option changed, so the tray can re-apply hotkeys etc.</summary>
    public event Action? Applied;

    /// <summary>Quits the app; the owner disposes the tray icon before exiting.</summary>
    public Action? ExitRequested;
    private bool _exiting;

    public PreferencesForm(AppSettings settings, int startTab = 0)
    {
        _s = settings;
        Localization.LanguageService.Configure(settings.DisplayLanguage);
        _startTab = startTab;

        Text = "SnipLite 首选项";
        Icon = AppIcon.Window;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Chrome;
        Font = settings.CreateInterfaceFont();
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;
        DoubleBuffered = true;
        ResizeRedraw = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);

        _page.BackColor = Page;
        _page.AutoScroll = true;

        StyleButton(_reset, "恢复默认");
        _reset.Click += (_, _) => ResetAll();

        Controls.Add(_page);
        Controls.Add(_reset);
        AppTheme.Changed += ApplyTheme;
        Localization.LanguageService.Changed += RefreshLanguage;
    }

    private bool _languageRefreshPending;
    private void RefreshLanguage()
    {
        if (IsDisposed || !IsHandleCreated || _languageRefreshPending) return;
        _languageRefreshPending = true;
        BeginInvoke(() =>
        {
            _languageRefreshPending = false;
            if (IsDisposed) return;
            Text = UiText.T("SnipLite 首选项");
            StyleButton(_reset, "恢复默认");
            PlaceChrome();
            ShowTab(_selected);
        });
    }

    private void ApplyTheme()
    {
        if (IsDisposed) return;
        AppTheme.Apply(this);
        if (_ready) PrepareTree(_page);   // labels back to opaque card colour
        AppTheme.ApplyWindow(this);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        AppTheme.ApplyWindow(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            AppTheme.Changed -= ApplyTheme;
            Localization.LanguageService.Changed -= RefreshLanguage;
        }
        base.Dispose(disposing);
    }

    // ---------------------------------------------------------------- layout

    private int Px(int value) => (int)Math.Round(value * _ui);

    // Rows scale with the configured interface font instead of a hard-coded
    // height, so larger fonts and high-DPI scaling keep comfortable padding.
    private int TextHeight => TextRenderer.MeasureText("国Ag", Font).Height;
    private int FieldHeight => Math.Max(Px(ControlH), TextHeight + Px(12));
    private int AdaptiveRowHeight(int contentHeight) => Math.Max(Px(44), contentHeight + Px(20));

    private Rectangle PageRect() => new(
        Px(SidebarW + 22),
        Px(86),
        ClientSize.Width - Px(SidebarW + 44),
        ClientSize.Height - Px(86 + FooterH));

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Text = UiText.T("SnipLite 首选项");

        _ui = DeviceDpi / 96f;
        StyleButton(_reset, UiText.T("恢复默认", _s.DisplayLanguage));

        var area = Screen.FromControl(this).WorkingArea;
        ClientSize = new Size(Math.Min(Px(WinW), area.Width - Px(32)), Math.Min(Px(WinH), area.Height - Px(64)));

        // Resize limits are outer sizes, so add the current non-client frame. The
        // window may grow freely but never shrink below its default size.
        var frame = new Size(Width - ClientSize.Width, Height - ClientSize.Height);
        MinimumSize = new Size(ClientSize.Width + frame.Width, ClientSize.Height + frame.Height);
        MaximumSize = Size.Empty;

        RestoreSavedBounds();

        PlaceChrome();

        _ready = true;
        ShowTab(Math.Clamp(_startTab, 0, _tabs.Length - 1));
        ApplyTheme();
    }

    /// <summary>
    /// Restores the last saved size (when it is at least the minimum size) and the
    /// last saved position when that rectangle still intersects an available
    /// working area (handles docked monitors being unplugged). Falls back to a
    /// centred window otherwise.
    /// </summary>
    private void RestoreSavedBounds()
    {
        var w = _s.PreferencesWindowWidth;
        var h = _s.PreferencesWindowHeight;
        if (w >= MinimumSize.Width && h >= MinimumSize.Height) Size = new Size(w, h);

        int x = _s.PreferencesWindowX, y = _s.PreferencesWindowY;
        if (x != int.MinValue && y != int.MinValue)
        {
            var rect = new Rectangle(x, y, Width, Height);
            foreach (var screen in Screen.AllScreens)
            {
                if (!rect.IntersectsWith(screen.WorkingArea)) continue;
                Location = new Point(x, y);
                return;
            }
        }
        CenterToScreen();
    }

    /// <summary>Persists position and size while the window is in its normal state.</summary>
    private void SaveBounds()
    {
        if (!_ready || WindowState != FormWindowState.Normal) return;
        _s.PreferencesWindowX = Location.X;
        _s.PreferencesWindowY = Location.Y;
        _s.PreferencesWindowWidth = Width;
        _s.PreferencesWindowHeight = Height;
        _s.Save();
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        SaveBounds();
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        SaveBounds();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SaveBounds();
        if (e.CloseReason == CloseReason.UserClosing && !_exiting)
        {
            if (_s.CloseButtonAction == CloseButtonAction.MinimizeToTray)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            // "直接关闭" quits the whole app. Let the owner dispose the tray icon
            // first, otherwise Windows keeps a ghost icon until the mouse hovers it.
            e.Cancel = true;
            _exiting = true;
            BeginInvoke(() =>
            {
                if (ExitRequested is not null) ExitRequested();
                else Application.Exit();
            });
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // No control should wear a focus highlight when the dialog appears:
        // the page is a read-then-adjust surface, not a form to be typed into.
        ActiveControl = null;
    }

    private void PlaceChrome()
    {
        _page.Bounds = PageRect();

        var footer = ClientSize.Height - Px(FooterH);
        _reset.Bounds = new Rectangle(
            ClientSize.Width - Px(28) - _reset.Width,
            footer + Px(8),
            _reset.Width,
            Px(32));
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);

        if (!_ready) return;

        PlaceChrome();
        _tabRects.Clear();
        Invalidate();

        // Card widths are derived from the client width, so a width change needs
        // the current page rebuilt. Coalesced to one rebuild per message loop.
        if (_laidOutWidth != ClientSize.Width) QueueContentRelayout();
    }

    /// <summary>Opaque erase: transparent child backgrounds rely on the parent
    /// paint, an unpainted strip would otherwise show up black while resizing.</summary>
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using var brush = new SolidBrush(Chrome);
        e.Graphics.FillRectangle(brush, ClientRectangle);
    }

    private void QueueContentRelayout()
    {
        if (_relayoutQueued) return;
        _relayoutQueued = true;
        BeginInvoke(() =>
        {
            _relayoutQueued = false;
            if (IsDisposed || Disposing || !_ready) return;
            if (_laidOutWidth == ClientSize.Width) return;
            ShowTab(_selected);
        });
    }

    private static readonly System.Reflection.PropertyInfo? DoubleBufferedProperty =
        typeof(Control).GetProperty("DoubleBuffered",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    private static readonly System.Reflection.PropertyInfo? ResizeRedrawProperty =
        typeof(Control).GetProperty("ResizeRedraw",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    /// <summary>
    /// Double-buffers and fully repaints every nested container of the page, and
    /// gives labels an opaque background matching their card instead of
    /// Color.Transparent. Native editors keep their own painting.
    /// </summary>
    private static void PrepareTree(Control root)
    {
        foreach (Control child in root.Controls)
        {
            if (child is not (TextBoxBase or ComboBox or NumericUpDown or UpDownBase))
            {
                try { DoubleBufferedProperty?.SetValue(child, true); }
                catch { /* control does not support the style */ }
            }

            try { ResizeRedrawProperty?.SetValue(child, true); }
            catch { /* control does not support the style */ }

            if (child is Label)
                child.BackColor = child.Parent?.BackColor ?? SystemColors.Control;

            PrepareTree(child);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            Close();
            return;
        }

        if (e.Control && e.KeyCode == Keys.Tab)
        {
            SelectTab((_selected + (e.Shift ? _tabs.Length - 1 : 1)) % _tabs.Length);
            e.Handled = e.SuppressKeyPress = true;
            return;
        }

        base.OnKeyDown(e);
    }

    // ----------------------------------------------------------------- paint

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        if (!_ready) return;

        var g = e.Graphics;
        var palette = AppTheme.Current;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var chrome = new SolidBrush(Chrome)) g.FillRectangle(chrome, e.ClipRectangle);
        using (var sidebar = new SolidBrush(palette.Sidebar)) g.FillRectangle(sidebar, 0, 0, Px(SidebarW), ClientSize.Height);
        FluentIcons.Draw(g, "capture", new Rectangle(Px(23), Px(26), Px(26), Px(26)), palette.Accent);
        using var brandFont = new Font(Font.FontFamily, Font.Size + 3, FontStyle.Bold);
        TextRenderer.DrawText(g, "SnipLite", brandFont, new Rectangle(Px(58), Px(25), Px(116), Px(30)), Ink, TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, UiText.T("首选项", _s.DisplayLanguage), Font, new Rectangle(Px(24), Px(65), Px(136), Px(24)), Muted);
        using var headingFont = new Font(Font.FontFamily, Font.Size + 10, FontStyle.Bold);
        TextRenderer.DrawText(g, UiText.T(_tabs[_selected], _s.DisplayLanguage), headingFont,
            new Rectangle(PageRect().X + Px(PagePad), Px(20), PageRect().Width, Px(44)), Ink, TextFormatFlags.VerticalCenter);

        if (_tabRects.Count == 0) LayoutTabs(g);

        // Center the icon + gap + label block as one unit. Using the widest label
        // for every row keeps the icon column and the text column aligned.
        var iconSize = Px(20);
        var gap = Px(11);
        var maxTextWidth = 0;
        foreach (var tab in _tabs)
            maxTextWidth = Math.Max(maxTextWidth, TextRenderer.MeasureText(g, UiText.T(tab, _s.DisplayLanguage), Font,
                new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width);
        var blockWidth = iconSize + gap + maxTextWidth;
        var navRect = new Rectangle(Px(10), 0, Px(SidebarW - 20), 0);
        // Center the label column, not the whole block: subtract half of the icon
        // and gap so the text shares the header's vertical center line.
        var blockLeft = navRect.Left + Math.Max(Px(6), (navRect.Width - blockWidth) / 2 - (iconSize + gap) / 2);

        foreach (var (rect, index) in _tabRects)
        {
            var active = index == _selected;

            if (active || index == _hoverTab)
            {
                using var path = FluentDrawing.Rounded(rect, Px(5));
                using var fill = new SolidBrush(active ? palette.Card : palette.Hover);
                g.FillPath(fill, path);
            }
            if (active)
            {
                using var indicator = new SolidBrush(palette.Accent);
                using var path = FluentDrawing.Rounded(new Rectangle(rect.Left, rect.Top + Px(10), Px(3), rect.Height - Px(20)), Px(2));
                g.FillPath(indicator, path);
            }
            FluentIcons.Draw(g, TabIcons[index], new Rectangle(blockLeft, rect.Y + Px(8), iconSize, iconSize), Ink);
            TextRenderer.DrawText(g, UiText.T(_tabs[index], _s.DisplayLanguage), Font,
                new Rectangle(blockLeft + iconSize + gap, rect.Y, maxTextWidth + Px(6), rect.Height), Ink,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
        var lineHeight = TextRenderer.MeasureText(g, "Ag", Font, new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Height;
        var versionRect = new Rectangle(Px(4), ClientSize.Height - Px(70), Px(SidebarW - 8), lineHeight * 2);
        TextRenderer.DrawText(g, AppInfo.ProductName, Font,
            new Rectangle(versionRect.X, versionRect.Y, versionRect.Width, lineHeight), Hint,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        TextRenderer.DrawText(g, AppInfo.Version, Font,
            new Rectangle(versionRect.X, versionRect.Y + lineHeight, versionRect.Width, lineHeight), Hint,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        var footerY = ClientSize.Height - Px(FooterH) + Px(13);
        FluentIcons.Draw(g, "check", new Rectangle(PageRect().X + Px(6), footerY, Px(17), Px(17)), palette.Accent);
        var themeText = palette.HighContrast ? "跟随 Windows · 高对比度" : palette.Dark ? "跟随 Windows · 深色" : "跟随 Windows · 浅色";
        TextRenderer.DrawText(g, UiText.T(themeText, _s.DisplayLanguage), Font,
            new Rectangle(PageRect().X + Px(30), footerY - Px(2), Px(260), Px(23)), Muted, TextFormatFlags.VerticalCenter);
    }

    private void LayoutTabs(Graphics g)
    {
        _tabRects.Clear();

        for (var i = 0; i < _tabs.Length; i++)
        {
            // The layout tab joins the main list under "控制"; only Updates/About
            // stay pinned above the version line at the bottom.
            var y = i < 8 ? Px(105 + i * 40) : ClientSize.Height - Px(162 - (i - 8) * 40);
            _tabRects.Add((new Rectangle(Px(10), y, Px(SidebarW - 20), Px(36)), i));
        }
    }

    // ------------------------------------------------------------ hit testing

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var hover = _tabRects.FindIndex(t => t.Rect.Contains(e.Location));

        if (hover == _hoverTab) return;

        var previous = _hoverTab;
        _hoverTab = hover;
        Cursor = hover >= 0 ? Cursors.Hand : Cursors.Default;
        InvalidateTab(previous);
        InvalidateTab(hover);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);

        if (_hoverTab < 0) return;

        var previous = _hoverTab;
        _hoverTab = -1;
        InvalidateTab(previous);
    }

    private void InvalidateTab(int index)
    {
        if (index < 0 || index >= _tabRects.Count) return;
        var rect = _tabRects[index].Rect; rect.Inflate(2, 2); rect.Height++;
        Invalidate(rect);
    }
    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        if (!_ready) return;
        _tabRects.Clear(); Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button != MouseButtons.Left) return;

        foreach (var (rect, index) in _tabRects)
        {
            if (!rect.Contains(e.Location)) continue;

            if (index != _selected) ShowTab(index);
            return;
        }
    }

    /// <summary>Switch pages from outside (the tray "帮助" entry opens 关于).</summary>
    public void SelectTab(int index)
    {
        if (!_ready)
        {
            _startTab = index;
            return;
        }

        index = Math.Clamp(index, 0, _tabs.Length - 1);
        if (index != _selected) ShowTab(index);
    }
    internal void SelectSubPage(int tab, int sub) { _subPages[tab] = sub; ShowTab(tab); }

    // ----------------------------------------------------------------- pages

    private void ShowTab(int index)
    {
        if (!_ready) return;

        _selected = index;
        _tabRects.Clear();
        _sectionProperties.Clear();
        _page.AutoScrollPosition = Point.Empty;

        _page.SuspendLayout();

        while (_page.Controls.Count > 0)
            _page.Controls[0].Dispose();

        _y = Px(PagePad);
        _labelX = Px(PagePad);
        _controlX = _labelX + Px(LabelW + ControlGap);
        _groups = 0;

        // Pages with a single group need no caption repeating the tab name.
        _captions = index is 0 or 1 or 5;

        switch (index)
        {
            case 0: BuildGeneral(); break;
            case 1: BuildInterface(); break;
            case 2: BuildCapture(); break;
            case 3: BuildVideo(); break;
            case 4: BuildPin(); break;
            case 5: BuildOutput(); break;
            case 6: BuildControl(); break;
            case 7: BuildLayout(); break;
            case 8: BuildUpdate(); break;
            default: BuildAbout(); break;
        }

        _page.AutoScrollMinSize = new Size(0, _y + Px(PagePad));
        _page.ResumeLayout();
        UiText.Apply(_page, _s.DisplayLanguage);
        AppTheme.Apply(_page);
        PrepareTree(_page);
        _laidOutWidth = ClientSize.Width;
        _reset.Visible = index <= 7;
        ActiveControl = null;
        Invalidate();
    }

    private void BuildGeneral() => GeneralPage();
    private void BuildInterface() => SubPages(new[] { "常规", "放大镜" }, InterfacePage, MagnifierPage);
    private void BuildCapture() => SubPages(new[] { "显示", "行为", "按键", "长截图" }, CaptureDisplayPage, CaptureBehaviorPage, CaptureKeysPage, LongCapturePage);
    private void BuildVideo() => SubPages(new[] { "录屏", "GIF动画" }, RecordPage, GifPage);
    private void BuildPin() => SubPages(new[] { "显示", "行为", "转换" }, PinDisplayPage, PinBehaviorPage, PinConversionPage);
    private void BuildOutput() => SubPages(new[] { "文件", "剪贴板" }, OutputFilePage, OutputClipboardPage);
    private void BuildControl() => SubPages(new[] { "全局快捷键", "鼠标" }, HotkeysPage, MousePage);
    private void BuildLayout() => LayoutPage();
    private void BuildUpdate()
    {
        Row("当前版本", MakeStatic($"{AppInfo.ProductName} {AppInfo.Version}"));
        Row("发布版本", MakeWebLink(AppInfo.ReleasesUrl));
        var status = MakeStatic("");
        var check = new FluentButton();
        var download = new FluentButton();
        string Local(string text) => UiText.T(text, _s.DisplayLanguage);
        StyleButton(check, Local("检查更新"));
        StyleButton(download, Local("下载安装包"));
        download.Enabled = false;
        Row("检查更新", check, download);
        Row("更新状态", status);

        (string Tag, Version Version)? release = null;
        check.Click += async (_, _) =>
        {
            check.Enabled = download.Enabled = false;
            status.Text = Local("正在检查更新…");
            try
            {
                var latest = await ReleaseUpdates.CheckAsync();
                if (status.IsDisposed) return;
                release = latest;
                var current = Version.Parse(AppInfo.Version.Split('-')[0]);
                download.Enabled = latest.Version > current ||
                    latest.Version == current && AppInfo.Version.Contains('-');
                status.Text = download.Enabled ? $"{Local("发现新版本")} {latest.Tag}" : Local("已是最新版本");
            }
            catch (Exception ex)
            {
                AppLog.Write("检查更新失败", ex);
                if (!status.IsDisposed) status.Text = Local("检查更新失败，请重试");
            }
            finally { if (!check.IsDisposed) check.Enabled = true; }
        };

        download.Click += async (_, _) =>
        {
            if (release is not { } latest) return;
            var file = $"SnipLite-{latest.Tag[1..]}-win-x64-setup.exe";
            using var dialog = new SaveFileDialog { FileName = file, Filter = "Windows installer|*.exe", AddExtension = true };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            check.Enabled = download.Enabled = false;
            status.Text = Local("正在下载安装包…");
            try
            {
                await ReleaseUpdates.DownloadAsync(latest.Tag, file, dialog.FileName);
                if (!status.IsDisposed) status.Text = Local("已保存安装包，请退出 SnipLite 后运行安装程序");
            }
            catch (Exception ex)
            {
                AppLog.Write("下载更新失败", ex);
                if (!status.IsDisposed) status.Text = Local("下载失败，请重试");
            }
            finally { if (!check.IsDisposed) check.Enabled = download.Enabled = true; }
        };
    }
    private void BuildAbout()
    {
        Row("程序", MakeStatic($"{AppInfo.ProductName} {AppInfo.Version}"));
        var executable = MakeText(Application.ExecutablePath, FillWidth()); executable.ReadOnly = true; Row("程序文件", executable);
        Row("运行环境", MakeStatic(".NET 10 · WinForms")); Row("识别引擎", MakeStatic("SimdPaddleOCR · Windows OCR · ZXing.Net"));
        Row("GitHub 项目", MakeWebLink(AppInfo.RepositoryUrl));
        Row("问题反馈", MakeWebLink(AppInfo.IssuesUrl, AppInfo.IssuesText));
        var license = new LinkLabel { Text = "MIT License / Third-party notices", AutoSize = true,
            LinkColor = _s.Accent, BackColor = Page, Cursor = Cursors.Hand, TabStop = true };
        license.LinkClicked += (_, _) => LicenseNotices.Show(this);
        Row("开源许可证", license);
    }

    private LinkLabel MakeWebLink(string url, string? text = null)
    {
        var link = new LinkLabel
        {
            Text = text ?? url, AutoSize = false, AutoEllipsis = true,
            Size = new Size(FillWidth(), Px(24)), TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Page, LinkColor = _s.Accent, ActiveLinkColor = _s.Accent,
            VisitedLinkColor = _s.Accent, LinkBehavior = LinkBehavior.HoverUnderline,
            Cursor = Cursors.Hand, TabStop = true
        };
        link.LinkClicked += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { AppLog.Write("打开项目网址失败", ex); Toast.Error(UiText.T("无法打开浏览器，请手动访问：") + url); }
        };
        return link;
    }
    // ------------------------------------------------------------- sections

    /// <summary>Width of the page's content column.</summary>
    // Reserved width must scale with the UI, otherwise a high-DPI scrollbar makes
    // the cards wider than the viewport and a horizontal bar appears.
    private int ContentWidth() => PageRect().Width - Px(PagePad * 2) - Px(SystemInformation.VerticalScrollBarWidth);

    /// <summary>Width left for a field that starts in the control column.</summary>
    private int FillWidth() => ContentWidth() - Px(LabelW + ControlGap + 16);

    /// <summary>
    /// A named group of rows. Pages with more than one group show a caption and
    /// breathe a little; single-group pages start straight at the page padding.
    /// </summary>
    private void Section(string title, Action content)
    {
        if (_groups++ > 0) _y += Px(SectionGap);

        if (_captions)
        {
            var height = TextRenderer.MeasureText(title, Font).Height + Px(12);
            _page.Controls.Add(new Label
            {
                Text = title,
                AutoSize = false,
                Bounds = new Rectangle(_labelX, _y, ContentWidth(), height),
                ForeColor = Muted,
                BackColor = Page,
                TextAlign = ContentAlignment.MiddleLeft
            });

            _y += height;
        }

        content();
    }

    // --------------------------------------------------------------- helpers

    private void Change(Action apply)
    {
        apply();
        Persist();
    }

    private void Persist()
    {
        Localization.LanguageService.Configure(_s.DisplayLanguage);
        _s.Save();
        Applied?.Invoke();
    }

    private async Task<bool> ApplyStartupAsync(bool enabled, bool administrator)
    {
        Enabled = false;
        try
        {
            var applied = await Task.Run(() => Startup.TryApply(enabled, administrator));
            if (!applied && !IsDisposed) Toast.Error(UiText.T("开机启动设置未修改。"));
            return applied;
        }
        finally { if (!IsDisposed) Enabled = true; }
    }

    private async void ResetAll()
    {
        var defaults = AppSettings.Reset();
        if (_selected == 0 && !await ApplyStartupAsync(defaults.StartWithWindows, defaults.StartAsAdministrator)) return;
        if (IsDisposed) return;
        foreach (var name in _sectionProperties)
        {
            var property = typeof(AppSettings).GetProperty(name)!;
            property.SetValue(_s, property.GetValue(defaults));
        }
        Font = _s.CreateInterfaceFont();
        Toast.Notifications = _s.ShowToast;
        Persist();
        ShowTab(_selected);
    }
    private void Row(string label, params Control[] controls)
    {
        var content = Math.Max(controls.Select(c => c.Height).DefaultIfEmpty(0).Max(), TextHeight);
        if (controls.Any(c => c is TextBoxBase or NumericUpDown)) content = Math.Max(content, FieldHeight);
        var height = AdaptiveRowHeight(content);
        var card = NewSettingRow(height);
        if (label.Length > 0)
        {
            AddRowIcon(card, FluentIcons.ForLabel(label));
            card.Controls.Add(new Label { Text = label, AutoSize = false, AutoEllipsis = true, Bounds = new Rectangle(Px(48), Px(10), Px(LabelW - 52), height - Px(20)),
                TextAlign = ContentAlignment.MiddleLeft, ForeColor = Ink, BackColor = Color.Transparent });
        }
        var x = Px(LabelW + ControlGap);
        foreach (var original in controls)
        {
            if (string.IsNullOrEmpty(original.AccessibleName)) original.AccessibleName = label;
            var control = original is TextBoxBase or NumericUpDown ? new FluentInputFrame(original) { Height = FieldHeight } : original;
            control.Location = new Point(x, Math.Max(0, (height - control.Height) / 2));
            if (control.Width > card.Width - x - Px(14)) control.Width = Math.Max(Px(40), card.Width - x - Px(14));
            if (string.IsNullOrEmpty(control.AccessibleName)) control.AccessibleName = label;
            card.Controls.Add(control);
            x += control.Width + Px(ControlGap);
        }
        _y += height + Px(6);
    }

    /// <summary>
    /// Two equal cards on one line, so a page with many short options fits on one
    /// screen instead of scrolling. Each cell keeps the icon / label / control rhythm
    /// of the full-width rows, just measured against half the content width.
    /// </summary>
    private void GridRow(string leftLabel, Control? left, string rightLabel, Control? right,
        int leftLabelWidth = 0, int rightLabelWidth = 0, int leftPercent = 42,
        bool leftFixed = false, bool rightFixed = false,
        bool leftAnchorRight = false, bool rightAnchorRight = false)
    {
        var gap = Px(10);
        // Left column is mostly short labels (MOV, 25 FPS, 立体声); the right column
        // carries the wide pickers and the slider, so it gets the larger share.
        var width = Math.Max(Px(180), (ContentWidth() - gap) * leftPercent / 100);
        var content = Math.Max(TextHeight, Math.Max(left?.Height ?? 0, right?.Height ?? 0));
        var height = AdaptiveRowHeight(content);

        var first = new FluentSettingRow { Bounds = new Rectangle(_labelX, _y, width, height) };
        var second = new FluentSettingRow { Bounds = new Rectangle(_labelX + width + gap, _y, ContentWidth() - width - gap, height) };
        _page.Controls.Add(first);
        _page.Controls.Add(second);

        if (left is not null) FillGridCell(first, leftLabel, left, leftLabelWidth, leftFixed, false, leftAnchorRight);
        if (right is not null) FillGridCell(second, rightLabel, right, rightLabelWidth, false, rightFixed, rightAnchorRight);
        _y += height + gap;
    }

    private void FillGridCell(Control card, string label, Control control, int labelWidth = 0,
        bool fixedLayout = false, bool fixedRight = false, bool rightAnchor = false)
    {
        if (label.Length > 0) AddRowIcon(card, FluentIcons.ForLabel(label));
        var x = Px(48);
        var room = Math.Max(Px(60), card.Width - x - Px(12));

        if (fixedLayout)
        {
            // Pixel-exact column used by the four short record pickers: icon at 14,
            // label at 40, picker 116 wide glued to the 12 px inner right padding so
            // "25 FPS" / "无限制" / "立体声" never need an ellipsis.
            var icon = card.Controls[card.Controls.Count - 1];
            icon.Left = Px(14);
            if (string.IsNullOrEmpty(control.AccessibleName)) control.AccessibleName = label;

            var picker = Px(116);
            var labelBox = Math.Clamp(card.Width - Px(12) - picker - Px(40) - Px(6), Px(62), Px(105));
            if (Px(40) + labelBox + Px(6) + picker > card.Width - Px(12))
                picker = Math.Max(Px(52), card.Width - Px(12) - Px(40) - labelBox - Px(6));

            card.Controls.Add(new Label
            {
                Text = label,
                AutoSize = false,
                AutoEllipsis = false, // the measured column always fits four CJK glyphs
                Bounds = new Rectangle(Px(40), Px(6), labelBox, card.Height - Px(12)),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Ink,
                BackColor = Color.Transparent
            });

            control.Width = picker;
            control.Height = Math.Min(control.Height, Math.Max(Px(26), card.Height - Px(16)));
            control.Location = new Point(Px(40) + labelBox + Px(6), Math.Max(Px(4), (card.Height - control.Height) / 2));
            control.Visible = true;
            card.Controls.Add(control);
            control.BringToFront();
            return;
        }

        if (fixedRight)
        {
            // One shared geometry for the whole column, so the three pickers are
            // pixel-identical: right edge 12 px inside the card, every control 194 wide.
            if (string.IsNullOrEmpty(control.AccessibleName)) control.AccessibleName = label;
            var rightEdge = card.Width - Px(12);
            var ctrlWidth = Px(194);
            var ctrlLeft = rightEdge - ctrlWidth;
            // Never let the wide control eat into the label column.
            var minLabel = Px(56);
            if (ctrlLeft < Px(42) + minLabel)
            {
                ctrlLeft = Px(42) + minLabel;
                ctrlWidth = rightEdge - ctrlLeft;
            }

            card.Controls.Add(new Label
            {
                Text = label,
                AutoSize = false,
                // Full-height single line: its middle is the card's middle, exactly
                // like the picker next to it.
                Bounds = new Rectangle(Px(42), 0, Math.Max(minLabel, ctrlLeft - Px(42)), card.Height),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Ink,
                BackColor = Color.Transparent
            });

            control.Width = ctrlWidth;
            if (control.Height > card.Height - Px(10)) control.Height = Math.Max(Px(28), card.Height - Px(10));
            control.Location = new Point(ctrlLeft, Math.Max(0, (card.Height - control.Height) / 2));
            control.Visible = true;
            card.Controls.Add(control);
            control.BringToFront();
            return;
        }

        if (rightAnchor && !fixedLayout && !fixedRight)
        {
            // Control glued to the 12 px inner right padding; the label takes whatever
            // is left instead of pushing the control past the card on narrow widths.
            if (string.IsNullOrEmpty(control.AccessibleName)) control.AccessibleName = label;
            var rightEdge = card.Width - Px(12);
            var ctrlWidth = Math.Min(control.Width, Math.Max(Px(84), rightEdge - Px(40) - Px(8)));
            var ctrlLeft = rightEdge - ctrlWidth;

            card.Controls.Add(new Label
            {
                Text = label,
                AutoSize = false,
                AutoEllipsis = false,
                Bounds = new Rectangle(Px(40), 0, Math.Max(Px(56), ctrlLeft - Px(40) - Px(8)), card.Height),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Ink,
                BackColor = Color.Transparent
            });

            control.Width = ctrlWidth;
            if (control.Height > card.Height - Px(10)) control.Height = Math.Max(Px(28), card.Height - Px(10));
            control.Location = new Point(ctrlLeft, Math.Max(0, (card.Height - control.Height) / 2));
            control.Visible = true;
            card.Controls.Add(control);
            control.BringToFront();
            return;
        }

        if (control is FluentToggle toggle)
        {
            if (label.Length == 0) AddRowIcon(card, FluentIcons.ForLabel(toggle.Text));
            toggle.Bounds = new Rectangle(x, Px(8), Math.Max(Px(80), room), card.Height - Px(16));
            toggle.BackColor = AppTheme.Current.Card;
            card.Controls.Add(toggle);
            return;
        }

        if (string.IsNullOrEmpty(control.AccessibleName)) control.AccessibleName = label;
        // A shared label column keeps every picker in the column on the same x / width.
        var needed = labelWidth > 0
            ? Px(labelWidth)
            : Math.Min(TextRenderer.MeasureText(label, Font).Width + Px(6), Math.Max(Px(56), room - Px(84)));
        var available = Math.Max(Px(84), room - needed - Px(8));
        control.Width = available;
        if (control.Height > card.Height - Px(10)) control.Height = Math.Max(Px(28), card.Height - Px(10));

        var labelRect = Math.Max(Px(40), needed);

        if (label.Length > 0)
        {
            card.Controls.Add(new Label
            {
                Text = label,
                AutoSize = false,
                AutoEllipsis = true,
                Bounds = new Rectangle(x, Px(8), labelRect, card.Height - Px(16)),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Ink,
                BackColor = Color.Transparent
            });
        }

        // Right edge first: a picker capped by MaximumSize still lines up with its
        // neighbours instead of floating away from the card edge.
        var left = label.Length > 0
            ? Math.Max(x + labelRect + Px(8), card.Width - Px(12) - control.Width)
            : x;
        control.Location = new Point(left, Math.Max(0, (card.Height - control.Height) / 2));
        card.Controls.Add(control);
    }

    /// <summary>Compact switch used inside a grid cell; the label doubles as its icon key.</summary>
    private FluentToggle GridToggle(string text, string name)
    {
        var box = new FluentToggle
        {
            Text = text,
            AutoSize = false,
            Checked = (bool)Value(name),
            BackColor = AppTheme.Current.Card,
            ForeColor = Ink,
            Width = Px(200),
            Height = Px(30)
        };
        box.CheckedChanged += (_, _) => Set(name, box.Checked);
        return box;
    }

    /// <summary>Slider plus a live percentage inside one grid cell.</summary>
    private Control GridSlider(string name, int min, int max, int width = 110, int readoutWidth = 46)
    {
        var value = Math.Clamp(Convert.ToInt32(Value(name)), min, max);
        var readout = MakeStatic(value + "%");
        var track = new TrackBar
        {
            Minimum = min,
            Maximum = max,
            Value = value,
            TickFrequency = Math.Max(1, (max - min) / 4),
            SmallChange = 1,
            LargeChange = 10,
            AutoSize = false,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            BackColor = AppTheme.Current.Card
        };
        track.ValueChanged += (_, _) =>
        {
            readout.Text = track.Value + "%";
            Set(name, track.Value);
        };

        var host = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            BackColor = AppTheme.Current.Card,
            Margin = Padding.Empty,
            Width = Px(width),
            Height = Px(30)
        };
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Px(readoutWidth)));
        host.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        // Keep the track short so the label and the readout never get squeezed out.
        host.MaximumSize = new Size(Px(width), Px(30));
        readout.Margin = Padding.Empty;
        readout.AutoSize = false;
        readout.Dock = DockStyle.Fill;
        if (readout is Label readoutLabel) readoutLabel.TextAlign = ContentAlignment.MiddleRight;
        host.Controls.Add(track, 0, 0);
        host.Controls.Add(readout, 1, 0);
        return host;
    }

    private FluentSettingRow NewSettingRow(int height) => NewSettingRow(_labelX, ContentWidth(), height);

    /// <summary>A card at an explicit column position, used by the two-column grids.</summary>
    private FluentSettingRow NewSettingRow(int x, int width, int height)
    {
        var row = new FluentSettingRow { Bounds = new Rectangle(x, _y, width, height) };
        _page.Controls.Add(row);
        return row;
    }

    private void AddRowIcon(Control row, string iconId)
    {
        var icon = new Panel { Bounds = new Rectangle(Px(15), (row.Height - Px(20)) / 2, Px(20), Px(20)), BackColor = AppTheme.Current.Card };
        icon.Paint += (_, e) => FluentIcons.Draw(e.Graphics, iconId, icon.ClientRectangle, Ink);
        row.Controls.Add(icon);
    }

    /// <summary>A row whose first control stretches to fill the remaining width.</summary>
    private void RowFill(string label, Control fill, params Control[] trailing)
    {
        var used = 0;

        foreach (var control in trailing)
            used += control.Width + Px(ControlGap);

        fill.Width = Math.Max(Px(120), FillWidth() - used);

        var all = new Control[trailing.Length + 1];
        all[0] = fill;
        trailing.CopyTo(all, 1);

        Row(label, all);
    }

    private void Check(string text, bool value, Action<bool> onChange, string? icon = null)
    {
        var card = NewSettingRow(AdaptiveRowHeight(Math.Max(TextHeight, Px(38))));
        AddRowIcon(card, icon ?? FluentIcons.ForLabel(text));
        var box = new FluentToggle
        {
            Text = text,
            AutoSize = false,
            Bounds = new Rectangle(Px(48), Px(10), card.Width - Px(62), card.Height - Px(20)),
            BackColor = AppTheme.Current.Card,
            ForeColor = Ink,
            Checked = value
        };

        box.CheckedChanged += (_, _) => onChange(box.Checked);

        card.Controls.Add(box);
        _y += card.Height + Px(6);
    }

    private void AddLabel(string text, int x, int y, int width)
    {
        _page.Controls.Add(new Label
        {
            Text = text,
            AutoSize = false,
            Bounds = new Rectangle(x, y, Px(width), Px(RowH)),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Ink,
            BackColor = Page
        });
    }

    private void HintText(string text)
    {
        _y += Px(4);
        text = UiText.T(text, _s.DisplayLanguage);
        var height = Math.Max(Px(22), TextRenderer.MeasureText(text, Font,
            new Size(ContentWidth(), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + Px(4));

        _page.Controls.Add(new Label
        {
            Text = text,
            AutoSize = false,
            Bounds = new Rectangle(_labelX, _y, ContentWidth(), height),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Hint,
            BackColor = Page
        });

        _y += height + Px(2);
    }

    private static Control MakeStatic(string text) => new Label
    {
        Text = text,
        AutoSize = true,
        ForeColor = Ink,
        BackColor = Color.Transparent,
        Margin = Padding.Empty
    };

    /// <summary>
    /// A combo box drawn the way the rest of the app looks: a flat grey field
    /// that opens the same menu the tray uses. The native flat ComboBox loses
    /// its text while unfocused, and its blue selection fights the palette.
    /// </summary>
    private Button MakeChooser(string[] options, int selected, Action<int> onChange, int width = 0, bool center = false,
        bool centeredRows = false, string? standaloneCentered = null, int popupRowWidth = 0,
        Func<int, Color?>? itemColor = null, int separatorBefore = -1)
    {
        // Language names stay in their native scripts so users can always find their language.
        var nativeLanguages = options.SequenceEqual(new[] { UiText.T("跟随系统") }
            .Concat(Localization.LanguageService.Languages.Select(l => l.NativeName)));
        if (!nativeLanguages) options = options.Select(option => UiText.T(option, _s.DisplayLanguage)).ToArray();
        var button = new FluentButton
        {
            Tag = nativeLanguages ? "localization:native" : null,
            DropDown = true,
            Text = options[Math.Clamp(selected, 0, options.Length - 1)],
            AutoSize = false,
            FlatStyle = FlatStyle.Flat,
            BackColor = Field,
            ForeColor = Ink,
            TextAlign = center ? ContentAlignment.MiddleCenter : ContentAlignment.MiddleLeft,
            Padding = new Padding(Px(8), 0, Px(26), 0),
            Width = width > 0 ? width : Px(190),
            Height = FieldHeight,
            TabStop = true
        };

        button.FlatAppearance.BorderColor = FieldEdge;
        button.FlatAppearance.MouseOverBackColor = AppTheme.Current.Hover;
        button.FlatAppearance.MouseDownBackColor = AppTheme.Current.AccentSoft;

        // One menu for the life of the chooser, owned by the button so it is
        // disposed together with it. Re-creating (and disposing) the menu on
        // every click upsets ToolStripManager's message filter.
        var standalone = standaloneCentered is null ? null : (nativeLanguages ? standaloneCentered : UiText.T(standaloneCentered, _s.DisplayLanguage));
        var menu = center ? SnipMenu.CreateComboPopup(centeredRows, standalone) : SnipMenu.Create();
        var items = new ToolStripMenuItem[options.Length];
        // Row metrics are derived from the widest option, never from a width the
        // button may change later: otherwise the labels centre outside the popup.
        var widest = options.Select(o => TextRenderer.MeasureText(o, menu.Font).Width).DefaultIfEmpty(0).Max();
        var rowHeight = Math.Max(Px(28), TextRenderer.MeasureText("国", menu.Font).Height + Px(12));
        // A popupRowWidth caps the popup to the picker's own width: rows are that
        // narrow and the 4 px menu padding lands the edges exactly on the button.
        int RowWidth() => popupRowWidth > 0
            ? Math.Max(popupRowWidth, widest + Px(32))
            : Math.Max(Px(120), Math.Max(button.Width - Px(8), widest + Px(56)));

        button.ContextMenuStrip = menu;

        for (var i = 0; i < options.Length; i++)
        {
            var index = i;
            if (i == separatorBefore && center) menu.Items.Add(new ToolStripSeparator { Tag = "dash:#A0A0A0" });

            items[i] = SnipMenu.Item(options[i], () =>
            {
                button.Text = options[index];
                onChange(index);
            });
            items[i].Tag = null;
            if (nativeLanguages) items[i].Tag = "localization:native";
            items[i].Text = options[i];
            // Only non-null colours are overridden; the rest inherit the menu ink.
            if (itemColor?.Invoke(i) is { } ink) items[i].ForeColor = ink;
            if (center)
            {
                // Fixed row width, refreshed on open so the row always matches the
                // actual popup width and every label stays centred.
                items[i].AutoSize = false;
                items[i].Size = new Size(RowWidth(), rowHeight);
            }

            menu.Items.Add(items[i]);
        }

        menu.Opening += (_, _) =>
        {
            for (var i = 0; i < options.Length; i++)
                items[i].Checked = options[i] == button.Text;

            if (center)
            {
                var row = RowWidth();
                foreach (var item in items) item.Size = new Size(row, rowHeight);
                menu.MinimumSize = new Size(row + Px(8), 0);
            }
            else
            {
                menu.MinimumSize = new Size(button.Width, 0);
            }
        };

        button.Click += (_, _) => menu.Show(button, new Point(0, button.Height + Px(2)));
        button.KeyDown += (_, e) =>
        {
            if (e.KeyCode is not (Keys.Down or Keys.Up or Keys.F4)) return;
            menu.Show(button, new Point(0, button.Height + Px(2)));
            items[Math.Clamp(Array.FindIndex(options, option => option == button.Text), 0, items.Length - 1)].Select();
            e.Handled = e.SuppressKeyPress = true;
        };
        return button;
    }

    private NumericUpDown MakeNumber(int min, int max, int value, int width, Action<int> onChange)
    {
        var number = new NumericUpDown
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value, min, max),
            Width = Px(width),
            BackColor = Field,
            ForeColor = Ink,
            TextAlign = HorizontalAlignment.Right
        };

        number.ValueChanged += (_, _) => onChange((int)number.Value);

        return number;
    }

    private TextBox MakeText(string value, int width) => new()
    {
        Text = value,
        Width = width,
        BackColor = Field,
        ForeColor = Ink,
        BorderStyle = BorderStyle.FixedSingle
    };

    private void StyleButton(Button button, string text)
    {
        text = UiText.T(text, _s.DisplayLanguage);
        button.Text = text;
        button.AutoSize = false;
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = Field;
        button.ForeColor = Ink;
        button.FlatAppearance.BorderColor = FieldEdge;
        button.FlatAppearance.MouseOverBackColor = AppTheme.Current.Hover;
        button.FlatAppearance.MouseDownBackColor = AppTheme.Current.AccentSoft;
        using var metrics = IsHandleCreated ? CreateGraphics() : null;
        var measured = metrics is null ? TextRenderer.MeasureText(text, Font) : TextRenderer.MeasureText(metrics, text, Font);
        button.Height = Math.Max(Px(ControlH), measured.Height + Px(10));
        button.Width = measured.Width + Px(28);
    }

    private void AddButton(int x, string text, Action action, string? hint = null)
    {
        var button = new FluentButton();
        StyleButton(button, text);
        var top = _y;
        button.Bounds = new Rectangle(x, top, button.Width, Math.Max(Px(28), TextHeight + Px(8)));
        button.Click += (_, _) => action();

        _page.Controls.Add(button);
        if (hint is null)
        {
            _y = top + Px(RowH + 2);
            return;
        }

        // Keep the explainer right under its button instead of one row lower.
        hint = UiText.T(hint, _s.DisplayLanguage);
        var hintHeight = Math.Max(Px(22), TextRenderer.MeasureText(hint, Font,
            new Size(ContentWidth(), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + Px(4));
        var hintTop = button.Bottom + Px(7);
        _page.Controls.Add(new Label
        {
            Text = hint,
            AutoSize = false,
            Bounds = new Rectangle(_labelX, hintTop, ContentWidth(), hintHeight),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Hint,
            BackColor = Page
        });
        _y = hintTop + hintHeight + Px(10);
    }

    private static string Hex(Color color) =>
        "#" + color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2");

    private void Open(string path, bool revealFile)
    {
        try
        {
            if (revealFile)
            {
                System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");
                return;
            }

            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start("explorer.exe", path);
        }
        catch
        {
        }
    }
}
