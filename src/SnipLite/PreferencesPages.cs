namespace SnipLite;

internal sealed partial class PreferencesForm
{
    private readonly int[] _subPages = new int[10];
    private readonly HashSet<string> _sectionProperties = new();
    public event Action<bool>? HotkeyEditing;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<Keys, bool>? HotkeyAvailable { get; set; }

    /// <summary>Shared width for the General page pickers; fits the longest engine label.</summary>
    private int ChooserWidth => Math.Max(Px(200),
        TextRenderer.MeasureText("本地增强 (自动识别)", Font).Width + Px(56));

    private void SubPages(string[] names, params Action[] pages)
    {
        var selected = Math.Clamp(_subPages[_selected], 0, pages.Length - 1);
        var x = _labelX;
        for (var i = 0; i < names.Length; i++)
        {
            var index = i;
            var button = new FluentButton { Selected = i == selected }; StyleButton(button, names[i]);
            button.Location = new Point(x, _y);
            button.BackColor = i == selected ? Page : Field;
            button.FlatAppearance.BorderColor = i == selected ? _s.Accent : FieldEdge;
            button.Click += (_, _) => { _subPages[_selected] = index; ShowTab(_selected); };
            _page.Controls.Add(button); x += button.Width + Px(4);
        }
        _y += Math.Max(Px(44), FieldHeight + Px(12));
        _captions = false;
        pages[selected]();
        // Never inherit a scroll offset from the other sub tab: it would leave the
        // last row sliced in half at the bottom edge.
        _page.AutoScrollPosition = new Point(0, 0);
    }
    private object Value(string name) { _sectionProperties.Add(name); return typeof(AppSettings).GetProperty(name)!.GetValue(_s)!; }
    private void Set(string name, object value)
    {
        Change(() => typeof(AppSettings).GetProperty(name)!.SetValue(_s, value));
        if (name == nameof(_s.DisplayLanguage)) RefreshLanguage();
    }
    private void Flag(string label, string name) => Check(label, (bool)Value(name), value => Set(name, value));
    private void Number(string label, string name, int min, int max, string suffix = "")
        => Row(label, MakeNumber(min, max, Convert.ToInt32(Value(name)), 140, value => Set(name, value)), MakeStatic(suffix));
    private void Choice(string label, string name, string[] labels, object[] values, int width = 0, bool center = false)
    {
        var value = Value(name);
        Row(label, MakeChooser(labels, Math.Max(0, Array.IndexOf(values, value)), index => Set(name, values[index]), width, center));
    }
    /// <summary>Left/right slider with a live percentage readout, e.g. 100%.</summary>
    private void PercentSlider(string label, string name, int min, int max)
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
            Width = Px(210),
            Height = Px(32),
            BackColor = AppTheme.Current.Card
        };
        track.ValueChanged += (_, _) =>
        {
            readout.Text = track.Value + "%";
            Set(name, track.Value);
        };
        Row(label, track, readout);
    }

    private void ColorRow(string label, string name)
    {
        var button = new FluentButton { Tag = "color-swatch" }; StyleButton(button, "选择颜色");
        button.BackColor = Color.FromArgb((int)Value(name));
        button.ForeColor = button.BackColor.GetBrightness() < .45 ? Color.White : Ink;
        button.Click += (_, _) =>
        {
            using var dialog = new ColorDialog { Color = button.BackColor, FullOpen = true };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            button.BackColor = dialog.Color;
            button.ForeColor = dialog.Color.GetBrightness() < .45 ? Color.White : Ink;
            Set(name, dialog.Color.ToArgb());
        };
        Row(label, button);
    }
    private void FontRow(string label, string family, string size)
    {
        Value(family); Value(size);
        var button = new FluentButton(); StyleButton(button, $"{Value(family)}, {Value(size)}");
        button.Width = Math.Min(Px(320), FillWidth());
        button.Click += (_, _) =>
        {
            using var initial = new Font((string)Value(family), Convert.ToSingle(Value(size)));
            using var dialog = new FontDialog { Font = initial, MinSize = family == nameof(_s.InterfaceFont) ? 7 : 6, MaxSize = family == nameof(_s.InterfaceFont) ? 14 : 100, ShowEffects = false, FontMustExist = true };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            typeof(AppSettings).GetProperty(family)!.SetValue(_s, dialog.Font.Name);
            typeof(AppSettings).GetProperty(size)!.SetValue(_s, dialog.Font.SizeInPoints);
            if (family == nameof(_s.InterfaceFont)) Font = _s.CreateInterfaceFont();
            button.Text = $"{dialog.Font.Name}, {dialog.Font.SizeInPoints:0.#}";
            Persist();
        };
        Row(label, button);
    }
    private void FolderRow(string label, string name)
    {
        var text = MakeText((string)Value(name), 0); text.ReadOnly = true;
        var change = new FluentButton(); StyleButton(change, "更改");
        change.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { SelectedPath = text.Text };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            text.Text = dialog.SelectedPath; Set(name, dialog.SelectedPath);
        };
        var open = new FluentButton(); StyleButton(open, "打开"); open.Click += (_, _) => Open(text.Text, false);
        RowFill(label, text, open, change);
    }
    private void GeneralPage()
    {
        // Keep all four pickers identical in width, but leave enough room for the
        // "配置微软密钥" button on the translation row so its label never truncates.
        var configureWidth = TextRenderer.MeasureText(UiText.T("配置微软密钥"), Font).Width + Px(28);
        var chooserWidth = Math.Min(ChooserWidth, Math.Max(Px(200), FillWidth() - configureWidth - Px(ControlGap)));
        Section("语言与识别", () => { });
        Choice("显示语言", nameof(_s.DisplayLanguage),
            new[] { UiText.T("跟随系统") }.Concat(Localization.LanguageService.Languages.Select(l => l.NativeName)).ToArray(),
            new object[] { Localization.LanguageService.SystemDefault }.Concat(Localization.LanguageService.Languages.Select(l => (object)l.Code)).ToArray(),
            chooserWidth, true);
        Choice("识别引擎", nameof(_s.OcrEngine), new[] { "本地增强 (自动识别)", "Windows 系统识别" }, new object[] { OcrEngineKind.SimdPaddle, OcrEngineKind.Windows }, chooserWidth, true);
        Choice("系统识别语言", nameof(_s.OcrLanguage), new[] { "简体中文", "English", "跟随系统" }, new object[] { "zh-Hans", "en-US", "" }, chooserWidth, true);
        var translationServices = TranslationProviders.Choices.Select(choice => choice.Kind).ToArray();
        var translationLabels = TranslationProviders.Choices.Select(choice => choice.Label).ToArray();
        int CurrentServiceIndex() => Math.Max(0, Array.IndexOf(translationServices, _s.TranslationProvider));
        Value(nameof(_s.TranslationProvider));
        Button translationService = null!;
        translationService = MakeChooser(translationLabels, CurrentServiceIndex(), index =>
        {
            var requested = translationServices[index];
            if (requested != TranslationProviderKind.Microsoft || MicrosoftTranslatorCredentials.HasKey())
            {
                Set(nameof(_s.TranslationProvider), requested);
                return;
            }

            translationService.Text = UiText.T(translationLabels[CurrentServiceIndex()]);
            // The dropdown must finish closing before a modal dialog opens.
            BeginInvoke(() =>
            {
                if (translationService.IsDisposed || ConfigureMicrosoftTranslator(requireKey: true) != true) return;
                Set(nameof(_s.TranslationProvider), requested);
                if (!translationService.IsDisposed) translationService.Text = UiText.T(translationLabels[index]);
            });
        }, chooserWidth, true);
        var configureTranslator = new FluentButton();
        StyleButton(configureTranslator, "配置微软密钥");
        configureTranslator.Click += (_, _) =>
        {
            if (ConfigureMicrosoftTranslator() != false || _s.TranslationProvider != TranslationProviderKind.Microsoft) return;
            Set(nameof(_s.TranslationProvider), TranslationProviderKind.Bing);
            if (!translationService.IsDisposed) translationService.Text = UiText.T(translationLabels[0]);
        };
        Row("翻译服务", translationService, configureTranslator);
        Section("启动与运行", () => { });
        Value(nameof(_s.StartWithWindows)); Value(nameof(_s.StartAsAdministrator));
        Check("开机启动", _s.StartWithWindows, async value =>
        {
            if (await ApplyStartupAsync(value, _s.StartAsAdministrator)) Set(nameof(_s.StartWithWindows), value);
            else if (!IsDisposed) ShowTab(_selected);
        });
        Check("以管理员身份开机启动", _s.StartAsAdministrator, async value =>
        {
            if (!_s.StartWithWindows || await ApplyStartupAsync(true, value)) Set(nameof(_s.StartAsAdministrator), value);
            else if (!IsDisposed) ShowTab(_selected);
        });
        Flag("自动备份配置文件", nameof(_s.AutoBackup));
        Flag("保持快速响应", nameof(_s.KeepResponsive));
        Flag("增强版托盘菜单", nameof(_s.EnhancedTrayMenu));
        Section("配置与日志", () => { });
        Choice("日志级别", nameof(_s.LogLevel), new[] { "关闭", "仅错误", "普通", "调试" }, new object[] { "Off", "Error", "Normal", "Debug" });
        AddButton(_controlX, "打开日志文件", () => OpenFile(AppLog.FilePath));
        var path = MakeText(AppSettings.FilePath, FillWidth()); path.ReadOnly = true;
        Row("配置文件", path);
        var openFolder = new FluentButton(); StyleButton(openFolder, "打开所在文件夹"); openFolder.Click += (_, _) => Open(AppSettings.FilePath, true);
        var open = new FluentButton(); StyleButton(open, "打开"); open.Click += (_, _) => OpenFile(AppSettings.FilePath);
        var change = new FluentButton(); StyleButton(change, "更改"); change.Click += (_, _) =>
        {
            using var dialog = new SaveFileDialog { Filter = UiText.T("JSON 配置|*.json"), FileName = Path.GetFileName(AppSettings.FilePath), InitialDirectory = Path.GetDirectoryName(AppSettings.FilePath) };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try { _s.MoveConfiguration(dialog.FileName); path.Text = AppSettings.FilePath; }
            catch (Exception ex) { Toast.Error(UiText.T("配置位置修改失败：") + ex.Message); }
        };
        Row("", openFolder, open, change);
    }
    private bool? ConfigureMicrosoftTranslator(bool requireKey = false)
    {
        MicrosoftTranslatorCredentials? current = null;
        try { current = MicrosoftTranslatorCredentials.Load(); }
        catch (InvalidOperationException ex) { Toast.Error(ex.Message); }

        using var dialog = new Form
        {
            Text = "Microsoft Translator",
            Font = Font,
            AutoScaleDimensions = new SizeF(96, 96),
            AutoScaleMode = AutoScaleMode.Dpi,
            ClientSize = new Size(440, 164),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false
        };
        var key = new TextBox { Bounds = new Rectangle(128, 17, 294, 26), UseSystemPasswordChar = true, Text = current?.Key ?? "" };
        var region = new TextBox { Bounds = new Rectangle(128, 52, 294, 26), Text = current?.Region ?? "" };
        var keyField = new FluentInputFrame(key) { Bounds = new Rectangle(128, 14, 294, 32) };
        var regionField = new FluentInputFrame(region) { Bounds = new Rectangle(128, 49, 294, 32) };
        var save = new FluentButton { Text = UiText.T("保存"), Bounds = new Rectangle(254, 118, 80, 30), Enabled = !requireKey || !string.IsNullOrWhiteSpace(key.Text) };
        if (requireKey) key.TextChanged += (_, _) => save.Enabled = !string.IsNullOrWhiteSpace(key.Text);
        var cancel = new FluentButton { Text = UiText.T("取消"), Bounds = new Rectangle(342, 118, 80, 30), DialogResult = DialogResult.Cancel };
        dialog.Controls.AddRange(new Control[]
        {
            new Label { Text = UiText.T("Translator 密钥"), Bounds = new Rectangle(18, 20, 110, 26) },
            new Label { Text = UiText.T("资源区域"), Bounds = new Rectangle(18, 55, 110, 26) },
            new Label
            {
                Text = requireKey ? "请输入 Azure Translator 密钥。全局资源可留空区域。" : "全局资源可留空区域。留空密钥并保存可清除配置。",
                Bounds = new Rectangle(18, 86, 404, 28)
            },
            keyField, regionField, save, cancel
        });
        save.Click += (_, _) =>
        {
            try
            {
                MicrosoftTranslatorCredentials.Save(key.Text.Trim(), region.Text.Trim().ToLowerInvariant());
                dialog.DialogResult = DialogResult.OK;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                MessageBox.Show(dialog, UiText.T("无法保存微软翻译密钥，请检查本地目录权限后重试。"), dialog.Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };
        dialog.AcceptButton = save;
        dialog.CancelButton = cancel;
        UiText.Apply(dialog);
        AppTheme.Apply(dialog);
        dialog.HandleCreated += (_, _) => AppTheme.ApplyWindow(dialog);
        return dialog.ShowDialog(this) == DialogResult.OK ? !string.IsNullOrWhiteSpace(key.Text) : null;
    }

    private void InterfacePage()
    {
        HintText("界面与图标跟随 Windows 浅色 / 深色模式，切换后即时生效。");
        FontRow("界面字体", nameof(_s.InterfaceFont), nameof(_s.InterfaceFontSize));
        ColorRow("主题颜色", nameof(_s.AccentArgb));
        var trayStyle = Value(nameof(_s.TrayIconStyle));
        var iconButton = new FluentButton(); StyleButton(iconButton, "选择图标文件");
        iconButton.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = UiText.T("图标与图片|*.ico;*.png;*.bmp"), CheckFileExists = true };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                using var preview = TrayIcons.LoadCustom(dialog.FileName);
                _s.CustomTrayIcon = dialog.FileName; Set(nameof(_s.TrayIconStyle), "Custom"); ShowTab(_selected);
            }
            catch (Exception ex) { Toast.Error("图标无法读取：" + ex.Message); }
        };
        // Keep the picker and its button on one line, like the translation row.
        var trayStyles = new[] { "Auto", "Color", "Custom" };
        var trayWidth = Math.Min(ChooserWidth, Math.Max(Px(150), FillWidth() - iconButton.Width - Px(ControlGap)));
        Row("托盘图标", MakeChooser(new[] { "自动", "彩色", "自定义" },
            Math.Max(0, Array.IndexOf(trayStyles, trayStyle)), index => Set(nameof(_s.TrayIconStyle), trayStyles[index]),
            trayWidth, true), iconButton);
        Value(nameof(_s.CustomTrayIcon));
        Choice("关闭程序", nameof(_s.CloseButtonAction),
            new[] { "最小化到托盘", "直接关闭" },
            new object[] { CloseButtonAction.MinimizeToTray, CloseButtonAction.Exit }, ChooserWidth, true);
    }
    private void MagnifierPage()
    {
        Choice("颜色值", nameof(_s.ColorHex), new[] { "RGB", "HEX" }, new object[] { false, true });
        Flag("隐藏 HEX 颜色值开头的 #", nameof(_s.ColorHideHash)); Flag("Hex 颜色值大写", nameof(_s.ColorUppercase));
        Flag("显示操作提示", nameof(_s.MagnifierHints));
        Flag("选区前自动显示放大镜", nameof(_s.AutoMagnifier));
        Number("放大镜倍数", nameof(_s.MagnifierZoom), 2, 16, "倍");
        HintText("截屏时按住 Alt 显示放大镜，滚轮调整倍数。");
    }
    private void CaptureDisplayPage()
    {
        Number("边框宽度", nameof(_s.CaptureBorderWidth), 1, 10, "像素");
        ColorRow("遮罩颜色", nameof(_s.CaptureMaskColor));
        Number("遮罩不透明度", nameof(_s.MaskOpacity), 0, 200, "/ 255");
        Flag("显示锚点", nameof(_s.CaptureAnchors)); ColorRow("锚点描边颜色", nameof(_s.CaptureAnchorColor));
        Flag("放大镜中显示遮罩", nameof(_s.MagnifierMask)); Flag("放大镜中显示边框", nameof(_s.MagnifierBorder));
        Flag("放大镜中显示锚点", nameof(_s.MagnifierAnchors));
        Flag("显示全屏十字线", nameof(_s.ShowCrosshair)); Flag("显示快捷键提示", nameof(_s.CaptureShortcutHints));
    }
    private void CaptureBehaviorPage()
    {
        var gap = Px(10);
        var cardWidth = (ContentWidth() - gap) / 2;
        var height = AdaptiveRowHeight(Math.Max(TextHeight, Px(38)));
        var column = 0;

        // Two equal cards per row: the nine behaviour options fit in five rows.
        void Cell(Action<int, int> place)
        {
            place(_labelX + column * (cardWidth + gap), cardWidth);
            if (++column == 2) { column = 0; _y += height + gap; }
        }
        void FlagCell(string label, string name) => Cell((left, width) => CheckCell(left, width, height, label, name));

        FlagCell("自动检测窗口", nameof(_s.DetectWindows)); FlagCell("隐藏 1×1 提示", nameof(_s.HideOnePixelHint));
        FlagCell("自动检测界面元素", nameof(_s.DetectElements)); FlagCell("移出屏幕", nameof(_s.AllowOffscreenSelection));
        // One switch drives cursor capture; the legacy "disable" flag just mirrors it.
        Cell((left, width) => CursorCell(left, width, height));
        FlagCell("其他程序窗口激活时退出截屏", nameof(_s.ExitCaptureOnDeactivate));
        FlagCell("截图后自动复制", nameof(_s.CopyAfterCapture));
        FlagCell("复制到剪贴板时播放音效", nameof(_s.SoundOnCopy));
        FlagCell("保存截图时播放音效", nameof(_s.SoundOnSave));
        Cell((left, width) => NumberCell(left, width, height, "历史记录数", nameof(_s.HistoryImageLimit), 0, 100));
        if (column != 0) { column = 0; _y += height + gap; }

        var sound = MakeText((string)Value(nameof(_s.CaptureSound)), 0);
        sound.TextChanged += (_, _) => Set(nameof(_s.CaptureSound), sound.Text);
        var choose = new FluentButton(); StyleButton(choose, "更改"); choose.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog
            {
                Filter = UiText.T("WAV 音效|*.wav"),
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + @"\Media",
                CheckFileExists = true
            };
            if (dialog.ShowDialog(this) == DialogResult.OK) sound.Text = dialog.FileName;
        };
        var play = new FluentButton(); StyleButton(play, "试听");
        play.Click += async (_, _) =>
        {
            play.Enabled = false;
            try { if (!await Services.PlaySoundAsync(sound.Text) && !IsDisposed) Toast.Error("无法播放音效，请选择有效的 WAV 文件。"); }
            finally { if (!play.IsDisposed) play.Enabled = true; }
        };
        var resetSound = new FluentButton(); StyleButton(resetSound, "恢复默认");
        resetSound.Click += (_, _) => sound.Clear();
        RowFill("截图音效文件", sound, play, choose, resetSound);
    }

    /// <summary>Half-width card carrying one switch, mirroring the full-width Flag look.</summary>
    private void CheckCell(int left, int width, int height, string label, string name)
    {
        var card = NewSettingRow(left, width, height);
        AddRowIcon(card, label);
        var box = new FluentToggle
        {
            Text = label,
            AutoSize = false,
            Bounds = new Rectangle(Px(48), Px(6), Math.Max(Px(60), width - Px(62)), height - Px(12)),
            BackColor = AppTheme.Current.Card,
            ForeColor = Ink,
            Checked = (bool)Value(name)
        };
        box.CheckedChanged += (_, _) => Set(name, box.Checked);
        card.Controls.Add(box);
    }

    /// <summary>Cursor switch: keeps the legacy "disable capture" flag in sync in both directions.</summary>
    private void CursorCell(int left, int width, int height)
    {
        Value(nameof(_s.DisableCursorCapture));
        var card = NewSettingRow(left, width, height);
        AddRowIcon(card, "捕捉鼠标指针");
        var box = new FluentToggle
        {
            Text = "捕捉鼠标指针",
            AutoSize = false,
            Bounds = new Rectangle(Px(48), Px(6), Math.Max(Px(60), width - Px(62)), height - Px(12)),
            BackColor = AppTheme.Current.Card,
            ForeColor = Ink,
            Checked = (bool)Value(nameof(_s.CaptureCursor))
        };
        box.CheckedChanged += (_, _) =>
        {
            Set(nameof(_s.CaptureCursor), box.Checked);
            Set(nameof(_s.DisableCursorCapture), !box.Checked);
        };
        card.Controls.Add(box);
    }

    /// <summary>Half-width card with a right-aligned numeric field.</summary>
    private void NumberCell(int left, int width, int height, string label, string name, int min, int max)
    {
        var card = NewSettingRow(left, width, height);
        AddRowIcon(card, label);
        // Same total length as the "恢复默认" button above; the narrower field keeps
        // a small spinner reserve so three-digit values still fit.
        var fieldWidth = TextRenderer.MeasureText("恢复默认", Font).Width + Px(28);
        var number = MakeNumber(min, max, Convert.ToInt32(Value(name)), fieldWidth, value => Set(name, value));
        number.Width = fieldWidth;
        // Centred digits between the field edge and the compact +/- buttons.
        number.TextAlign = HorizontalAlignment.Center;
        var field = new FluentInputFrame(number) { Height = FieldHeight, SpinnerReserve = 26, Compact = true };
        field.SetBounds(width - Px(14) - field.Width, Math.Max(0, (height - field.Height) / 2), field.Width, field.Height);
        card.Controls.Add(field);
        card.Controls.Add(new Label
        {
            Text = label, AutoSize = false, AutoEllipsis = true,
            Bounds = new Rectangle(Px(48), 0, Math.Max(Px(60), field.Left - Px(48) - Px(8)), height),
            TextAlign = ContentAlignment.MiddleLeft, ForeColor = Ink, BackColor = Color.Transparent
        });
    }

    private void ActionRow(string label, string action, string exit)
        => ActionRowCore(label, action, exit,
            new[] { "复制到剪贴板", "贴到屏幕", "快速保存", "无操作" },
            new object[] { CaptureAction.Copy, CaptureAction.Pin, CaptureAction.QuickSave, CaptureAction.None },
            value => value is CaptureAction.Copy or CaptureAction.Pin or CaptureAction.QuickSave);

    private void RightClickRow()
        => ActionRowCore("鼠标右键", nameof(_s.RightClickAction), nameof(_s.RightClickExit),
            new[] { "快速保存", "保存文件", "弹出菜单", "退出截图", "无操作" },
            new object[] { RightClickBehavior.QuickSave, RightClickBehavior.SaveFile, RightClickBehavior.ShowMenu, RightClickBehavior.ExitCapture, RightClickBehavior.None },
            value => value is RightClickBehavior.QuickSave or RightClickBehavior.SaveFile);

    /// <summary>Action chooser plus a "then exit capture" toggle; the toggle is disabled for actions that never save.</summary>
    private void ActionRowCore(string label, string action, string exit, string[] labels, object[] values, Func<object, bool> exitEnabled)
    {
        var caption = UiText.T("然后退出截屏");
        var check = new FluentToggle
        {
            Text = caption, AutoSize = false,
            Width = TextRenderer.MeasureText(caption, Font).Width + Px(59),
            Height = Px(24),
            Checked = (bool)Value(exit)
        };
        check.CheckedChanged += (_, _) => Set(exit, check.Checked);
        var chooser = MakeChooser(labels, Math.Max(0, Array.IndexOf(values, Value(action))), i =>
        {
            Set(action, values[i]);
            check.Enabled = exitEnabled(values[i]);
        });
        check.Enabled = exitEnabled(Value(action));

        var height = AdaptiveRowHeight(Math.Max(TextHeight, Math.Max(FieldHeight, check.Height)));
        var card = NewSettingRow(height);
        AddRowIcon(card, label);
        // The caption + switch hug the card's right padding so every toggle on
        // the page lines up with the plain Flag rows above.
        check.SetBounds(card.Width - Px(14) - check.Width, Math.Max(0, (height - check.Height) / 2), check.Width, check.Height);
        var chooserLeft = Px(LabelW + ControlGap);
        chooser.Width = Math.Min(chooser.Width, Math.Max(Px(120), check.Left - Px(ControlGap) - chooserLeft));
        chooser.Location = new Point(chooserLeft, Math.Max(0, (height - chooser.Height) / 2));
        card.Controls.Add(chooser);
        card.Controls.Add(check);
        card.Controls.Add(new Label
        {
            Text = label, AutoSize = false, AutoEllipsis = true,
            Bounds = new Rectangle(Px(48), 0, Math.Max(Px(60), chooserLeft - Px(48) - Px(8)), height),
            TextAlign = ContentAlignment.MiddleLeft, ForeColor = Ink, BackColor = Color.Transparent
        });
        _y += height + Px(6);
    }
    private void CaptureKeysPage()
    {
        Flag("禁用鼠标滚轮功能", nameof(_s.DisableCaptureWheel)); Flag("按 Esc 时弹出确认对话框", nameof(_s.ConfirmEscape));
        Flag("忽略其他程序模拟的复制命令", nameof(_s.IgnoreInjectedCopy));
        Flag("截屏时屏蔽左 Win 键", nameof(_s.BlockLeftWindows)); Flag("截屏时屏蔽右 Win 键", nameof(_s.BlockRightWindows));
        ActionRow("左键双击", nameof(_s.DoubleClickAction), nameof(_s.DoubleClickExit));
        ActionRow("中键单击", nameof(_s.MiddleClickAction), nameof(_s.MiddleClickExit));
        ActionRow("回车键", nameof(_s.EnterAction), nameof(_s.EnterExit));
        RightClickRow();
    }
    private void LongCapturePage()
    {
        Flag("启用长截图功能", nameof(_s.LongCaptureEnabled));
        Choice("滚动模式", nameof(_s.LongCaptureMode),
            new[] { "自动滚动", "手动滚动" },
            new object[] { LongCaptureMode.Auto, LongCaptureMode.Manual }, ChooserWidth, true);
        Choice("自动滚动速度", nameof(_s.LongCaptureSpeed),
            new[] { "慢速", "适中", "快速" },
            new object[] { LongCaptureSpeed.Slow, LongCaptureSpeed.Normal, LongCaptureSpeed.Fast }, ChooserWidth, true);
        Choice("最大拼接高度", nameof(_s.LongCaptureMaxHeight),
            new[] { "50000 像素", "100000 像素", "无限制" },
            new object[] { 50000, 100000, 0 }, ChooserWidth, true);
    }
    /// <summary>Chooser bound to a property, sized for use inside a half-width grid cell.</summary>
    private Control MakeChoice(string name, string[] labels, object[] values, int width = 190, bool centeredRows = false,
        int popupRowWidth = 0, Func<int, Color?>? itemColor = null, int separatorBefore = -1)
    {
        var value = Value(name);
        return MakeChooser(labels, Math.Max(0, Array.IndexOf(values, value)), index => Set(name, values[index]), Px(width), true,
            centeredRows, popupRowWidth: popupRowWidth > 0 ? Px(popupRowWidth) : 0,
            itemColor: itemColor, separatorBefore: separatorBefore);
    }

    /// <summary>
    /// Bitrate picker with the custom value field living in the same card: the field is
    /// only revealed (and only takes width) while "自定义" is selected.
    /// </summary>
    private Control BitrateCell()
    {
        var custom = MakeBitrateField();
        custom.Bare = true;
        // One capsule for the whole cell: it has exactly the same bounds as the plain
        // pickers in the rows above and below, and the two borderless cells inside share
        // its outline, so the row can never look narrower than its neighbours.
        var capsule = new CapsuleFrame
        {
            Margin = Padding.Empty,
            BackColor = AppTheme.Current.Field,
            Width = Px(194),
            Height = FieldHeight
        };

        var labels = new[] { "低 (2 Mbps)", "中 (5 Mbps)", "高 (10 Mbps)", "极高 (20 Mbps)", "4K (40 Mbps)", "自定义" };
        var values = new object[]
        {
            VideoBitrateMode.Low, VideoBitrateMode.Medium, VideoBitrateMode.High,
            VideoBitrateMode.Ultra, VideoBitrateMode.Ultra4K, VideoBitrateMode.Custom
        };
        // 1 px separator between the two cells, painted above them once custom is on.
        var divider = new Panel { BackColor = AppTheme.Current.Border, Width = 1, Visible = false };

        Control chooser = null!;
        chooser = MakeChooser(labels, Math.Max(0, Array.IndexOf(values, Value(nameof(_s.RecBitrate)))), index =>
        {
            Set(nameof(_s.RecBitrate), values[index]);
            Reveal(values[index] is VideoBitrateMode.Custom);
        }, Px(104), true, standaloneCentered: "自定义");
        if (chooser is FluentButton segment)
        {
            segment.Segment = true;
            segment.BackColor = AppTheme.Current.Field;
        }
        chooser.Dock = DockStyle.None;
        custom.Dock = DockStyle.None;

        capsule.Controls.Add(chooser);
        capsule.Controls.Add(custom);
        capsule.Controls.Add(divider);

        void Apply(bool on)
        {
            var gap = Px(9);
            var width = Math.Max(Px(60), capsule.Width);
            var height = Math.Max(Px(20), capsule.Height - Px(8));
            var inset = Math.Max(1, Px(1));
            var picker = on ? Px(104) : width - inset * 2;
            chooser.SetBounds(inset, Px(4), picker, height);
            custom.Visible = on;
            var fieldLeft = inset + picker + gap;
            custom.SetBounds(fieldLeft, Px(4), Math.Max(Px(20), width - inset - fieldLeft), height);
            divider.Visible = on;
            divider.SetBounds(inset + picker + gap / 2, Px(7), 1, Math.Max(Px(8), capsule.Height - Px(14)));
        }

        void Reveal(bool on)
        {
            Apply(on);
            if (on) custom.FocusEditor();
        }

        capsule.SizeChanged += (_, _) => Apply(custom.Visible);
        Apply(_s.RecBitrate == VideoBitrateMode.Custom);
        return capsule;
    }

    /// <summary>Digits-only field with a grey "Mbps" suffix inside the box, no spinner.</summary>
    private FluentInputFrame MakeBitrateField()
    {
        var box = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = AppTheme.Current.Field,
            TextAlign = HorizontalAlignment.Center,
            Text = Convert.ToString(_s.RecCustomBitrateMbps),
            Width = Px(120)
        };
        box.KeyPress += (_, e) =>
        {
            if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true;
        };
        box.TextChanged += (_, _) =>
        {
            if (int.TryParse(box.Text, out var value) && value >= 1 && value <= 200)
                Set(nameof(_s.RecCustomBitrateMbps), value);
        };
        box.Leave += (_, _) =>
        {
            var value = int.TryParse(box.Text, out var parsed) ? Math.Clamp(parsed, 1, 200) : _s.RecCustomBitrateMbps;
            Set(nameof(_s.RecCustomBitrateMbps), value);
            box.Text = Convert.ToString(value);
        };
        return new FluentInputFrame(box) { Width = Px(58), Margin = Padding.Empty };
    }

    private void RecordPage()
    {
        // The four left-column pickers are laid out by fixed coordinates
        // (leftFixed: true): icon 14 / label 40 (105 wide) / picker 100 right-aligned,
        // so the labels can never be squeezed into an ellipsis.
        GridRow("输出格式", MakeChoice(nameof(_s.RecFormat),
                new[] { "MP4", "MOV", "MKV", "WEBM", "AVI" },
                new object[] { VideoContainer.Mp4, VideoContainer.Mov, VideoContainer.Mkv, VideoContainer.Webm, VideoContainer.Avi },
                centeredRows: true, popupRowWidth: 108),
            "分辨率", MakeChoice(nameof(_s.RecResolution),
                new[] { "跟随选区", "1280*720", "1K 1920*1080", "2K 2560*1440", "4K 3840*2160",
                    "720*1280", "1K 1080*1920", "2K 1440*2560", "4K 2160*3840" },
                new object[] { VideoSize.Native, VideoSize.P720, VideoSize.P1080, VideoSize.Qhd, VideoSize.Uhd,
                    VideoSize.Portrait720, VideoSize.Portrait1080, VideoSize.Portrait2K, VideoSize.Portrait4K },
                centeredRows: true,
                // Landscape picks read blue, portrait picks amber, split by a hairline rule.
                itemColor: index => index switch
                {
                    0 => null,
                    <= 4 => Color.FromArgb(0x00, 0x52, 0x9B),
                    _ => Color.FromArgb(0xC6, 0x51, 0x02)
                },
                separatorBefore: 5),
            74, 68, 46, true, true);

        GridRow("视频帧率", MakeChoice(nameof(_s.RecFps),
                new[] { "24 FPS", "25 FPS", "30 FPS", "60 FPS" },
                new object[] { VideoFps.Fps24, VideoFps.Fps25, VideoFps.Fps30, VideoFps.Fps60 }),
            "视频码率", BitrateCell(), 74, 68, 46, true, true);

        GridRow("录制时长", MakeChoice(nameof(_s.RecLength),
                new[] { "无限制", "30 秒", "1 分钟", "3 分钟", "5 分钟", "10 分钟", "30 分钟" },
                new object[] { VideoLength.Unlimited, VideoLength.S30, VideoLength.M1, VideoLength.M3,
                    VideoLength.M5, VideoLength.M10, VideoLength.M30 },
                centeredRows: true),
            "音频来源", MakeChoice(nameof(_s.RecAudio),
                new[] { "静音", "内置音频", "麦克风", "内置音频 + 麦克风" },
                new object[] { RecordAudioSource.Mute, RecordAudioSource.System,
                    RecordAudioSource.Microphone, RecordAudioSource.SystemAndMicrophone },
                centeredRows: true), 74, 68, 46, true, true);

        GridRow("音频声道", MakeChoice(nameof(_s.RecChannels),
                new[] { "立体声", "单声道" },
                new object[] { RecordChannels.Stereo, RecordChannels.Mono }),
            "计时器透明度", GridSlider(nameof(_s.RecTimerOpacity), 20, 100, 194, 46), 74, 0, 46, true, true);

        GridRow("", GridToggle("显示录制计时器", nameof(_s.RecShowTimer)),
            "", GridToggle("录制鼠标指针", nameof(_s.RecShowCursor)), 0, 0, 46);
    }

    private void GifPage()
    {
        // Columns are mirrored: the toggles own the 45 % left cell, while the three
        // controls share one right-anchored geometry in the 55 % right cell.
        var gifFps = MakeChoice(nameof(_s.GifFrameRate),
            new[] { "10 FPS", "15 FPS", "24 FPS" },
            new object[] { GifFps.Fps10, GifFps.Fps15, GifFps.Fps24 }, width: 148, centeredRows: true);
        GridRow("", GridToggle("录制鼠标指针", nameof(_s.GifShowCursor)),
            "GIF 帧率", gifFps, 0, 0, 45, rightAnchorRight: true);

        var gifLength = MakeChoice(nameof(_s.GifLengthMode),
            new[] { "5 秒", "15 秒", "30 秒", "60 秒", "无限制" },
            new object[] { GifLength.S5, GifLength.S15, GifLength.S30, GifLength.S60, GifLength.Unlimited },
            width: 148, centeredRows: true);
        GridRow("", GridToggle("循环播放", nameof(_s.GifLoop)),
            "录制时长", gifLength, 0, 0, 45, rightAnchorRight: true);

        // 148 px: track 104 px + readout 44 px, all three right edges pixel-identical.
        var gifOpacity = GridSlider(nameof(_s.GifTimerOpacity), 20, 100, 148, 44);
        GridRow("", GridToggle("显示录制计时器", nameof(_s.GifShowTimer)),
            "计时器透明度", gifOpacity, 0, 0, 45, rightAnchorRight: true);

        // Long option text gets the whole row so it never ellipsizes.
        Row("画质与尺寸缩放", MakeChoice(nameof(_s.GifSize),
            new[] { "原始尺寸 (高清)", "75% 缩放 (均衡)", "50% 缩放 (轻量)" },
            new object[] { GifSizing.Original, GifSizing.Balanced, GifSizing.Light },
            Math.Max(220, ContentWidth() - LabelW - 60)));
    }

    private void PinDisplayPage()
    {
        Flag("窗口阴影", nameof(_s.PinShadow)); ColorRow("阴影颜色", nameof(_s.PinShadowColor));
        Flag("在右键菜单显示助记键", nameof(_s.PinMenuShortcuts));
        Number("默认不透明度", nameof(_s.PinOpacityPercent), 10, 100, "%");
        Number("最大窗口尺寸", nameof(_s.PinMaxSize), 100, 20000, "像素");
        Number("快速缩略图宽度", nameof(_s.ThumbnailWidth), 20, 1000, "像素");
        Number("快速缩略图高度", nameof(_s.ThumbnailHeight), 20, 1000, "像素");
    }
    private void PinBehaviorPage()
    {
        Flag("使用截屏时的位置", nameof(_s.PinAtCapturePosition));
        Flag("使用鼠标滚轮缩放", nameof(_s.PinWheelZoom)); Flag("以鼠标指针为中心缩放", nameof(_s.PinZoomAtCursor));
        Flag("平滑缩放", nameof(_s.PinSmoothScaling)); Flag("缩略图接受重置操作", nameof(_s.ThumbnailAcceptReset));
        Flag("边框闪烁时使窗口半透明", nameof(_s.PinTranslucentFlash));
        Flag("切换分组时自动闪烁贴图", nameof(_s.FlashOnGroupSwitch));
        Flag("拖放操作时保留原始图", nameof(_s.KeepOriginalOnDrag));
        Flag("贴出新图片时闪烁", nameof(_s.FlashNewPin)); Flag("贴出新图片时激活", nameof(_s.ActivateNewPin));
        HintText("Alt+拖动可将图片拖到其他应用；右键可切换分组、缩略图和鼠标穿透。");
    }
    private void PinConversionPage()
    {
        Flag("文字转图片", nameof(_s.TextToImage)); Flag("总是忽略 HTML 格式", nameof(_s.IgnoreHtml));
        Flag("不把文件路径转化成贴图", nameof(_s.IgnoreFilePaths));
        Number("页边距", nameof(_s.TextImageMargin), 0, 200, "像素"); Number("图片最大宽度", nameof(_s.TextImageMaxWidth), 100, 12000, "像素");
        FontRow("字体", nameof(_s.TextImageFont), nameof(_s.TextImageFontSize));
        ColorRow("背景颜色", nameof(_s.TextImageBackground)); ColorRow("文本颜色", nameof(_s.TextImageForeground));
        AddButton(_controlX, "预览文字贴图", () =>
        {
            using var image = TextImageRenderer.Render("预览\nAaOo012", null, _s);
            using var preview = new Form { Text = UiText.T("文字贴图预览"), ClientSize = image.Size, StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false };
            preview.Controls.Add(new PictureBox { Image = image, Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom }); preview.ShowDialog(this);
        });
    }
    private void OutputFilePage()
    {
        Number("JPEG 图像质量", nameof(_s.JpegQuality), 30, 100);
        var pattern = MakeText((string)Value(nameof(_s.FileNamePattern)), FillWidth());
        var preview = MakeStatic(_s.ExpandFileName(DateTime.Now) + ".png");
        pattern.TextChanged += (_, _) => { Set(nameof(_s.FileNamePattern), pattern.Text); preview.Text = Services.ImageFileName(_s, ".png"); };
        Row("文件名", pattern); Row("预览", preview);
        Flag("记住上一次使用的图片扩展名", nameof(_s.RememberImageExtension));
        Flag("快速保存时显示通知", nameof(_s.QuickSaveToast)); FolderRow("快速保存路径", nameof(_s.QuickSaveDirectory));
        Flag("自动保存", nameof(_s.AutoSave)); FolderRow("自动保存路径", nameof(_s.SaveDirectory));
        HintText("命名支持 年 月 日 时 分");
    }
    private void OutputClipboardPage()
    {
        Flag("截屏：同时复制 Windows Bitmap 格式", nameof(_s.CaptureBitmapClipboard));
        Flag("贴图：同时复制 Windows Bitmap 格式", nameof(_s.PinBitmapClipboard));
        HintText("始终保留 PNG 格式；Bitmap 用于兼容只接受位图的应用。");
    }
    private void HotkeysPage()
    {
        Flag("启用全局快捷键", nameof(_s.HotkeysEnabled));

        // Two equal cards per row keep the whole hotkey list on one screen.
        var gap = Px(10);
        var cardHeight = AdaptiveRowHeight(Math.Max(Px(38), TextHeight));

        void PlaceItem(string label, string name, bool fullWidth = false, int left = 0, int width = 0)
        {
            var binding = (Keys)Value(name);
            string BindingText() => _s.HotkeyErrors.TryGetValue(name, out var error)
                ? $"{HotkeyDefinitions.Format(binding)} · {UiText.T(error)}" : HotkeyDefinitions.Format(binding);

            // The binding itself is the button: clicking the capsule starts recording.
            var text = new Label
            {
                Text = BindingText(), AutoSize = false, AutoEllipsis = true,
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Ink, BackColor = Color.Transparent, Cursor = Cursors.Hand
            };
            var box = new CapsuleFrame
            {
                HoverHighlight = true, Cursor = Cursors.Hand,
                Padding = new Padding(Px(4), 0, Px(4), 0), Height = Px(30)
            };
            box.Controls.Add(text);
            void Hover(bool on) => box.Hovered = on;
            text.MouseEnter += (_, _) => Hover(true);
            text.MouseLeave += (_, _) => Hover(false);
            box.MouseEnter += (_, _) => Hover(true);
            box.MouseLeave += (_, _) => Hover(false);

            void StartEdit()
            {
                HotkeyEditing?.Invoke(true);
                try
                {
                    using var dialog = new Form
                    {
                        Text = UiText.T(label), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
                        AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ShowInTaskbar = false,
                        MinimizeBox = false, MaximizeBox = false, Font = Font
                    };
                    var panel = new FlowLayoutPanel
                    {
                        AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false,
                        Padding = new Padding(Px(12))
                    };
                    var input = new HotkeyBox { Binding = binding, Width = Px(310) };
                    var status = new Label { AutoSize = true, Text = UiText.T("按下新快捷键，或按 Delete 清除。") };
                    var buttons = new FlowLayoutPanel { AutoSize = true };
                    var confirm = new FluentButton();
                    var cancel = new FluentButton { DialogResult = DialogResult.Cancel };
                    StyleButton(confirm, "确认");
                    StyleButton(cancel, "取消");
                    confirm.Enabled = false;
                    var stolen = new List<string>();
                    input.BindingChanged += candidate =>
                    {
                        confirm.Enabled = candidate != binding;
                        status.Text = UiText.T(confirm.Enabled ? "点击确认以保存更改。" : "当前绑定未改变。");
                    };
                    confirm.Click += (_, _) =>
                    {
                        var candidate = input.Binding;
                        stolen.Clear();
                        if (candidate != Keys.None)
                        {
                            // A key can only belong to one feature. Assigning it here
                            // moves it away from the feature that owned it instead of
                            // refusing the change — otherwise setting F1 on
                            // "截屏并自动复制" silently failed because the default F1
                            // was still held by plain capture, so the hotkey appeared dead.
                            foreach (var item in HotkeyDefinitions.Items)
                            {
                                if (item.Name == name) continue;
                                if ((Keys)typeof(AppSettings).GetProperty(item.Name)!.GetValue(_s)! != candidate) continue;
                                stolen.Add(item.Name);
                            }
                        }
                        if (candidate != Keys.None && name != nameof(AppSettings.CopyCoordinatesHotkey) &&
                            HotkeyAvailable?.Invoke(candidate) == false)
                        {
                            status.Text = UiText.T("组合键被占用或由系统保留，请重新输入。");
                            input.Focus();
                            return;
                        }
                        dialog.DialogResult = DialogResult.OK;
                    };
                    buttons.Controls.AddRange(new Control[] { confirm, cancel });
                    panel.Controls.AddRange(new Control[] { input, status, buttons });
                    dialog.Controls.Add(panel);
                    dialog.CancelButton = cancel;
                    dialog.ActiveControl = input;
                    AppTheme.Apply(dialog);
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    {
                        foreach (var other in stolen) Set(other, Keys.None);
                        Set(name, input.Binding);
                        binding = input.Binding;
                    }
                }
                finally
                {
                    HotkeyEditing?.Invoke(false);
                    text.Text = BindingText();
                }
            }
            text.Click += (_, _) => StartEdit();
            box.Click += (_, _) => StartEdit();

            if (fullWidth)
            {
                var card = NewSettingRow(cardHeight);
                var nameLabel = new Label
                {
                    Text = label, AutoSize = false, AutoEllipsis = true,
                    Bounds = new Rectangle(Px(14), 0, Px(LabelW - 16), cardHeight),
                    TextAlign = ContentAlignment.MiddleLeft, ForeColor = Ink, BackColor = Color.Transparent
                };
                card.Controls.Add(nameLabel);
                box.SetBounds(Px(LabelW + ControlGap), Math.Max(0, (cardHeight - box.Height) / 2), Px(132), box.Height);
                card.Controls.Add(box);

                var smart = new FluentToggle
                {
                    AutoSize = false, Width = Px(39), Height = Px(38),
                    AccessibleName = UiText.T("智能排除任务栏"),
                    Checked = (bool)Value(nameof(AppSettings.SmartFullScreenCapture))
                };
                smart.Bounds = new Rectangle(card.Width - Px(14) - smart.Width, (cardHeight - smart.Height) / 2, smart.Width, smart.Height);
                smart.Anchor = AnchorStyles.Right;
                smart.CheckedChanged += (_, _) => Set(nameof(AppSettings.SmartFullScreenCapture), smart.Checked);
                card.Controls.Add(smart);

                var caption = new Label
                {
                    Text = UiText.T("智能排除任务栏"), AutoSize = true, Font = Font,
                    ForeColor = Ink, BackColor = Color.Transparent
                };
                card.Controls.Add(caption);
                caption.Anchor = AnchorStyles.Right;

                // Position from the label's real AutoSize width (DPI/font metrics can
                // differ from the pre-parent size) so it never slides under the switch,
                // and re-run on every resize to keep the 8px gap.
                void PlaceCaption()
                {
                    caption.Width = caption.PreferredWidth;
                    caption.Height = caption.PreferredHeight;
                    caption.Left = smart.Left - caption.Width - Px(8);
                    caption.Top = (cardHeight - caption.Height) / 2;
                    box.Left = Math.Max(Px(LabelW), caption.Left - Px(16) - box.Width);
                    nameLabel.Width = Math.Max(Px(60), box.Left - Px(14) - Px(10));
                }
                PlaceCaption();
                card.Resize += (_, _) => PlaceCaption();
                _y += cardHeight + gap;
                return;
            }

            // Grid card: name on the left, key capsule hugging the right edge. No icon,
            // so the freed width goes to the capsule and longer combos stay readable.
            var half = NewSettingRow(left, width, cardHeight);
            var right = width - Px(12);
            var boxWidth = Math.Max(Px(96), Math.Min(Px(156), width / 3 + Px(36)));
            box.SetBounds(right - boxWidth, Math.Max(0, (cardHeight - box.Height) / 2), boxWidth, box.Height);
            half.Controls.Add(box);
            half.Controls.Add(new Label
            {
                Text = label, AutoSize = false, AutoEllipsis = true,
                Bounds = new Rectangle(Px(14), 0, Math.Max(Px(40), box.Left - Px(14) - Px(8)), cardHeight),
                TextAlign = ContentAlignment.MiddleLeft, ForeColor = Ink, BackColor = Color.Transparent
            });
        }

        var pinned = nameof(AppSettings.CustomCaptureHotkey);
        // Hidden rows keep their bindings and tray menu behaviour, they just leave the page.
        var hidden = new HashSet<string> { nameof(AppSettings.TogglePinsHotkey), nameof(AppSettings.SwitchGroupHotkey), pinned };

        // Full-width card for the row that also carries the "智能排除任务栏" switch.
        var pinnedLabel = HotkeyDefinitions.Items.First(item => item.Name == pinned).Label;
        PlaceItem(pinnedLabel, pinned, true);

        // Everything else uses one uniform two-column grid: the roomy cards keep
        // long combinations such as Ctrl+Alt+S on a single line.
        var rest = HotkeyDefinitions.Items.Where(item => !hidden.Contains(item.Name)).ToList();
        for (var i = 0; i < rest.Count; i += 2)
        {
            var width = (ContentWidth() - gap) / 2;
            PlaceItem(rest[i].Label, rest[i].Name, false, _labelX, width);
            if (i + 1 < rest.Count) PlaceItem(rest[i + 1].Label, rest[i + 1].Name, false, _labelX + width + gap, width);
            _y += cardHeight + gap;
        }
    }
    private void ModifierRow(string label, string property)
    {
        var values = new object[] { Keys.None, Keys.Control, Keys.Alt, Keys.Shift, Keys.Control | Keys.Shift, Keys.Control | Keys.Alt };
        var value = Value(property);
        MouseCard(label, MakeChooser(new[] { "无", "Ctrl", "Alt", "Shift", "Ctrl+Shift", "Ctrl+Alt" },
            Math.Max(0, Array.IndexOf(values, value)), index => Set(property, values[index]),
            center: true, centeredRows: true));
    }

    private void MouseRow(string label, string name)
    {
        var binding = (MouseBinding)Value(name);
        var modifiers = new[] { Keys.None, Keys.Control, Keys.Alt, Keys.Shift, Keys.Control | Keys.Shift, Keys.Control | Keys.Alt };
        var gestures = Enum.GetValues<PointerGesture>();
        var mod = MakeChooser(new[] { "无", "Ctrl", "Alt", "Shift", "Ctrl+Shift", "Ctrl+Alt" }, Math.Max(0, Array.IndexOf(modifiers, binding.Modifiers)), i =>
        { binding = binding with { Modifiers = modifiers[i] }; Set(name, binding); }, center: true, centeredRows: true);
        var action = MakeChooser(new[] { "无", "左键双击", "中键单击", "右键单击", "左键单击" }, Array.IndexOf(gestures, binding.Gesture), i =>
        { binding = binding with { Gesture = gestures[i] }; Set(name, binding); }, center: true, centeredRows: true);
        MouseCard(label, mod, action);
    }

    /// <summary>
    /// Mouse page row: the label column is wider than the shared default so
    /// captions such as "贴图缩放（滚轮 / +/-）" stay on one line. The pickers start
    /// 32px further right and the "+" sits in a full-height label, which keeps it
    /// optically centred between the two pickers.
    /// </summary>
    private void MouseCard(string label, params Control[] controls)
    {
        var height = AdaptiveRowHeight(Math.Max(TextHeight,
            Math.Max(FieldHeight, controls.Select(c => c.Height).DefaultIfEmpty(0).Max())));
        var card = NewSettingRow(height);
        AddRowIcon(card, label);
        var right = card.Width - Px(14);
        var first = Px(LabelW + ControlGap) + Px(32);
        foreach (var control in controls)
            if (string.IsNullOrEmpty(control.AccessibleName)) control.AccessibleName = label;

        if (controls.Length == 1)
        {
            var control = controls[0];
            control.Width = Math.Min(control.Width, Math.Max(Px(120), right - first));
            control.Location = new Point(first, Math.Max(0, (height - control.Height) / 2));
            card.Controls.Add(control);
        }
        else
        {
            var mod = controls[0];
            var action = controls[1];
            var actionWidth = Math.Min(action.Width, Math.Max(Px(120), right - first - Px(140)));
            action.SetBounds(right - actionWidth, Math.Max(0, (height - action.Height) / 2), actionWidth, action.Height);
            var modWidth = Math.Min(mod.Width, Math.Max(Px(90), action.Left - first - Px(34)));
            mod.SetBounds(first, Math.Max(0, (height - mod.Height) / 2), modWidth, mod.Height);

            var plus = new Label
            {
                Text = "+", AutoSize = false, ForeColor = Muted, BackColor = Color.Transparent,
                Bounds = new Rectangle(mod.Right + (action.Left - mod.Right - Px(18)) / 2, 0, Px(18), height),
                TextAlign = ContentAlignment.MiddleCenter
            };
            card.Controls.Add(mod);
            card.Controls.Add(plus);
            card.Controls.Add(action);
        }

        card.Controls.Add(new Label
        {
            Text = label, AutoSize = false, AutoEllipsis = true,
            Bounds = new Rectangle(Px(48), 0, Math.Max(Px(60), first - Px(48) - Px(8)), height),
            TextAlign = ContentAlignment.MiddleLeft, ForeColor = Ink, BackColor = Color.Transparent
        });
        _y += height + Px(6);
    }
    private void MousePage()
    {
        ModifierRow("贴图缩放（滚轮 / +/-）", nameof(_s.PinZoomModifiers));
        ModifierRow("调整透明度（滚轮 / +/-）", nameof(_s.PinOpacityModifiers));
        MouseRow("关闭贴图", nameof(_s.PinCloseMouse)); MouseRow("重置贴图", nameof(_s.PinResetMouse));
        MouseRow("快速缩略图", nameof(_s.PinThumbnailMouse)); MouseRow("复制文本", nameof(_s.PinCopyTextMouse));
        HintText("冲突时按上方功能优先匹配；可在任意页面恢复当前分组默认值。");
    }
    private void GridCheck(params (string Label, string Property, string Icon)[] items)
    {
        const int columns = 3;
        var gap = Px(10);
        var width = ContentWidth();
        var cardWidth = (width - gap * (columns - 1)) / columns;
        var height = AdaptiveRowHeight(Math.Max(TextHeight, Px(38)));
        for (var index = 0; index < items.Length; index += columns)
        {
            for (var column = 0; column < columns && index + column < items.Length; column++)
                CheckCard(_labelX + column * (cardWidth + gap), cardWidth, height, items[index + column]);
            _y += height + Px(8);
        }
    }

    /// <summary>Two cards per row: long tray labels stay complete without a scrollbar.</summary>
    private void TwoColumnCheck(params (string Label, string Property, string Icon)[] items)
    {
        const int columns = 2;
        var gap = Px(10);
        var cardWidth = (ContentWidth() - gap * (columns - 1)) / columns;
        var height = AdaptiveRowHeight(Math.Max(TextHeight, Px(38)));
        for (var index = 0; index < items.Length; index += columns)
        {
            for (var column = 0; column < columns && index + column < items.Length; column++)
                CheckCard(_labelX + column * (cardWidth + gap), cardWidth, height, items[index + column]);
            _y += height + Px(8);
        }
    }

    private void CheckCard(int left, int width, int height, (string Label, string Property, string Icon) item)
    {
        var card = new FluentSettingRow
        {
            Bounds = new Rectangle(left, _y, width, height)
        };
        _page.Controls.Add(card);
        var icon = new Panel
        {
            Bounds = new Rectangle(Px(12), (height - Px(20)) / 2, Px(20), Px(20)),
            BackColor = AppTheme.Current.Card
        };
        icon.Paint += (_, e) => FluentIcons.Draw(e.Graphics, item.Icon, icon.ClientRectangle, Ink);
        card.Controls.Add(icon);
        var box = new FluentToggle
        {
            Text = item.Label,
            AutoSize = false,
            Bounds = new Rectangle(Px(38), Px(10), Math.Max(Px(60), card.Width - Px(50)), card.Height - Px(20)),
            BackColor = AppTheme.Current.Card,
            ForeColor = Ink,
            Checked = (bool)Value(item.Property)
        };
        box.CheckedChanged += (_, _) => Set(item.Property, box.Checked);
        card.Controls.Add(box);
    }
    private void LayoutPage()
    {
        SubPages(new[] { "自定义工具栏", "托盘菜单" }, ToolbarButtonsPage, TrayMenuPage);
    }
    private void ToolbarButtonsPage()
    {
        Section("绘图工具", () => { });
        GridCheck(
            ("矩形（R）", nameof(_s.ShowToolRect), "rect"),
            ("椭圆（E）", nameof(_s.ShowToolEllipse), "ellipse"),
            ("箭头（A）", nameof(_s.ShowToolArrow), "arrow"),
            ("铅笔（P）", nameof(_s.ShowToolPen), "pen"),
            ("马克笔（H）", nameof(_s.ShowToolMarker), "marker"),
            ("马赛克（M）", nameof(_s.ShowToolMosaic), "mosaic"),
            ("文字（T）", nameof(_s.ShowToolText), "text"),
            ("橡皮擦（X）", nameof(_s.ShowToolEraser), "eraser"));
        Section("辅助识别", () => { });
        GridCheck(
            ("文字识别", nameof(_s.ShowToolOcr), "ocr"),
            ("截图翻译", nameof(_s.ShowToolTranslate), "translate"),
            ("二维码识别", nameof(_s.ShowToolQr), "qr"),
            ("长截图", nameof(_s.ShowToolLong), "long"),
            ("录屏", nameof(_s.ShowToolRec), "rec"),
            ("录制 GIF", nameof(_s.ShowToolGif), "gif"));
        Section("动作操作", () => { });
        GridCheck(
            ("撤销", nameof(_s.ShowToolUndo), "undo"),
            ("重做", nameof(_s.ShowToolRedo), "redo"),
            ("取消截图", nameof(_s.ShowToolClose), "close"),
            ("贴图", nameof(_s.ShowToolPin), "pin"),
            ("保存", nameof(_s.ShowToolSave), "save"),
            ("复制", nameof(_s.ShowToolCopy), "copy"));
    }
    private void TrayMenuPage()
    {
        // Five rows of short entries, then the long labels get a full-width card each.
        GridCheck(
            ("截屏", nameof(_s.TrayItemSnip), "capture"),
            ("长截图", nameof(_s.TrayItemLong), "long"),
            ("禁用快捷键", nameof(_s.TrayItemDisableHotkeys), "keyboard"),

            ("录屏", nameof(_s.TrayItemRecord), "rec"),
            ("录制 GIF", nameof(_s.TrayItemGif), "gif"),
            ("文字识别", nameof(_s.TrayItemOcr), "ocr"),

            ("鼠标穿透", nameof(_s.TrayItemMouseThrough), "mouse"),
            ("二维码识别", nameof(_s.TrayItemQr), "qr"),
            ("关闭全部贴图", nameof(_s.TrayItemClosePins), "close"),

            ("清空截屏历史", nameof(_s.TrayItemClearHistory), "close"),
            ("恢复历史贴图", nameof(_s.TrayItemRestorePins), "undo"),
            ("截屏历史", nameof(_s.TrayItemHistory), "capture"),

            ("首选项", nameof(_s.TrayItemPreferences), "gear"),
            ("关于", nameof(_s.TrayItemAbout), "about"),
            ("重新启动", nameof(_s.TrayItemRestart), "update"));

        // Same vertical breath as Section() leaves between the toolbar page groups.
        _y += Px(14);
        TwoColumnCheck(
            ("截屏并自动复制", nameof(_s.TrayItemSnipCopy), "copy"),
            ("全屏截图并自动保存", nameof(_s.TrayItemFullSave), "save"),
            ("贴图 / 恢复贴图", nameof(_s.TrayItemPin), "pin"),
            ("隐藏 / 显示所有贴图", nameof(_s.TrayItemTogglePins), "pin"),
            ("切换到另一贴图分组", nameof(_s.TrayItemSwitchGroup), "layout"));
    }
    private static void OpenFile(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path)) File.WriteAllText(path, "");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { Toast.Error(UiText.T("打开文件失败：") + ex.Message); }
    }
}
