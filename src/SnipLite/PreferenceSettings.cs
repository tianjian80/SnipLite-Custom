using System.Text.Json.Serialization;

namespace SnipLite;

internal enum CaptureAction { Copy, Pin, QuickSave, None }
internal enum PointerGesture { None, LeftDoubleClick, MiddleClick, RightClick, LeftClick }
internal enum RightClickBehavior { QuickSave, ExitCapture, ShowMenu, SaveFile, None }
internal enum CloseButtonAction { MinimizeToTray, Exit }
internal enum LongCaptureMode { Auto, Manual }
internal enum LongCaptureSpeed { Slow, Normal, Fast }
internal sealed record MouseBinding(Keys Modifiers, PointerGesture Gesture);

internal sealed partial class AppSettings
{
    public string DisplayLanguage { get; set; } = Localization.LanguageService.SystemDefault;
    public bool StartAsAdministrator { get; set; }
    /// <summary>Hide the notification-area icon; hotkeys and a second launch still open the preferences.</summary>
    public bool HideTrayIcon { get; set; }
    /// <summary>Global skin key; see <see cref="ThemeManager"/> (Light / Dark / Warm / Tech).</summary>
    public string Skin { get; set; } = ThemeManager.DefaultKey;
    public bool AutoBackup { get; set; } = true;
    public bool KeepResponsive { get; set; } = true;
    public bool EnhancedTrayMenu { get; set; } = true;
    // Which entries the tray context menu shows. Factory set: 截屏 / 延时截图 /
    // 长截图 / 录屏 / 录制 GIF / 文字识别 on, everything else hidden.
    public bool TrayItemSnip { get; set; } = true;
    public bool TrayItemDelay { get; set; } = true;
    public bool TrayItemLong { get; set; } = true;
    public bool TrayItemSnipCopy { get; set; }
    public bool TrayItemFullSave { get; set; }
    public bool TrayItemRecord { get; set; } = true;
    public bool TrayItemGif { get; set; } = true;
    /// <summary>Global mouse-through state for pinned images, mirrored by the 按键 page switch.</summary>
    public bool MouseThroughEnabled { get; set; }
    public bool TrayItemPin { get; set; }
    public bool TrayItemTogglePins { get; set; }
    public bool TrayItemSwitchGroup { get; set; }
    public bool TrayItemRestorePins { get; set; }
    public bool TrayItemClosePins { get; set; }
    public bool TrayItemHistory { get; set; }
    public bool TrayItemClearHistory { get; set; }
    public bool TrayItemDisableHotkeys { get; set; }
    public bool TrayItemPreferences { get; set; }
    public bool TrayItemOcr { get; set; } = true;
    public bool TrayItemQr { get; set; }
    public bool TrayItemAbout { get; set; }
    public bool TrayItemRestart { get; set; }
    public string LogLevel { get; set; } = "Normal";
    public string InterfaceFont { get; set; } = "Microsoft YaHei UI";
    public float InterfaceFontSize { get; set; } = 9;
    /// <summary>Tray context-menu font size in points, fully independent of 界面字体.</summary>
    public double TrayFontSize { get; set; } = 10;
    /// <summary>Base longest edge (px) of a 截屏历史 thumbnail.</summary>
    public int HistoryThumbSize { get; set; } = 150;
    public string TrayIconStyle { get; set; } = "Auto";
    public string CustomTrayIcon { get; set; } = "";
    /// <summary>What the title-bar close button does: hide to tray or quit the app.</summary>
    public CloseButtonAction CloseButtonAction { get; set; } = CloseButtonAction.MinimizeToTray;
    // Which buttons the capture toolbar shows; every toggle defaults to on.
    public bool ShowToolRect { get; set; } = true;
    public bool ShowToolEllipse { get; set; } = true;
    public bool ShowToolArrow { get; set; } = true;
    public bool ShowToolPen { get; set; } = true;
    public bool ShowToolMarker { get; set; } = true;
    public bool ShowToolMosaic { get; set; } = true;
    public bool ShowToolText { get; set; } = true;
    public bool ShowToolEraser { get; set; } = true;
    public bool ShowToolUndo { get; set; } = true;
    public bool ShowToolRedo { get; set; } = true;
    public bool ShowToolOcr { get; set; } = true;
    public bool ShowToolRedact { get; set; } = true;
    public bool ShowToolTranslate { get; set; } = true;
    public bool ShowToolQr { get; set; } = true;
    public bool ShowToolClose { get; set; } = true;
    public bool ShowToolPin { get; set; } = true;
    public bool ShowToolSave { get; set; } = true;
    public bool ShowToolCopy { get; set; } = true;
    public bool ShowToolLong { get; set; } = true;
    public bool ColorHex { get; set; }
    public bool ColorHideHash { get; set; }
    public bool ColorUppercase { get; set; } = true;
    public bool MagnifierHints { get; set; } = true;
    public bool AutoMagnifier { get; set; } = true;
    public int CaptureBorderWidth { get; set; } = 1;
    public int CaptureMaskColor { get; set; } = Color.Black.ToArgb();
    public bool CaptureAnchors { get; set; } = true;
    public int CaptureAnchorColor { get; set; } = Color.White.ToArgb();
    public bool MagnifierMask { get; set; } = true;
    public bool MagnifierBorder { get; set; } = true;
    public bool MagnifierAnchors { get; set; }
    public bool CaptureShortcutHints { get; set; } = true;
    public bool DetectWindows { get; set; } = true;
    public bool DetectElements { get; set; } = true;
    public bool HideOnePixelHint { get; set; } = true;
    public bool CaptureCursor { get; set; }
    public bool DisableCursorCapture { get; set; }
    /// <summary>Let the finished selection be dragged past the screen edge (a small strip stays visible).</summary>
    public bool AllowOffscreenSelection { get; set; }
    public bool ExitCaptureOnDeactivate { get; set; }
    public int HistoryImageLimit { get; set; } = 20;
    public string CaptureSound { get; set; } = "";
    public bool SoundOnCopy { get; set; } = false;
    public bool SoundOnSave { get; set; } = false;
    public bool DisableCaptureWheel { get; set; }
    public bool ConfirmEscape { get; set; }
    public bool IgnoreInjectedCopy { get; set; } = true;
    public bool BlockLeftWindows { get; set; }
    public bool BlockRightWindows { get; set; }
    public CaptureAction DoubleClickAction { get; set; } = CaptureAction.Copy;
    public bool DoubleClickExit { get; set; } = true;
    public CaptureAction MiddleClickAction { get; set; } = CaptureAction.Pin;
    public bool MiddleClickExit { get; set; } = true;
    public CaptureAction EnterAction { get; set; } = CaptureAction.Copy;
    public bool EnterExit { get; set; } = true;
    public RightClickBehavior RightClickAction { get; set; } = RightClickBehavior.QuickSave;
    public bool RightClickExit { get; set; } = true;
    public bool PinShadow { get; set; } = true;
    public int PinShadowColor { get; set; } = Color.Black.ToArgb();
    public bool PinMenuShortcuts { get; set; } = true;
    public int PinMaxSize { get; set; } = 12000;
    public int ThumbnailWidth { get; set; } = 75;
    public int ThumbnailHeight { get; set; } = 75;
    public bool PinAtCapturePosition { get; set; } = true;
    public bool PinWheelZoom { get; set; } = true;
    public bool PinZoomAtCursor { get; set; } = true;
    public bool PinSmoothScaling { get; set; } = true;
    public bool ThumbnailAcceptReset { get; set; } = true;
    public bool FlashOnGroupSwitch { get; set; } = true;
    public bool KeepOriginalOnDrag { get; set; } = true;
    public bool FlashNewPin { get; set; } = true;
    public bool ActivateNewPin { get; set; } = true;
    public bool TextToImage { get; set; } = true;
    public bool IgnoreHtml { get; set; }
    public bool IgnoreFilePaths { get; set; } = true;
    public int TextImageMargin { get; set; } = 15;
    public int TextImageMaxWidth { get; set; } = 900;
    public string TextImageFont { get; set; } = "Microsoft YaHei UI";
    public float TextImageFontSize { get; set; } = 11;
    public int TextImageBackground { get; set; } = Color.White.ToArgb();
    public int TextImageForeground { get; set; } = Color.Black.ToArgb();
    public bool RememberImageExtension { get; set; } = true;
    public string LastImageExtension { get; set; } = ".png";
    public string QuickSaveDirectory { get; set; } = @"E:\";
    public bool QuickSaveToast { get; set; } = true;
    public bool CaptureBitmapClipboard { get; set; } = true;
    public bool PinBitmapClipboard { get; set; }
    // Factory hotkeys: 截屏 Ctrl+Alt+A, 首选项 F3, 全屏截图并自动保存 Shift+F1,
    // 录屏 小键盘 *; every other action ships unbound.
    public Keys CaptureHotkey { get; set; } = Keys.Control | Keys.Alt | Keys.A;
    public Keys CopyCaptureHotkey { get; set; } = Keys.None;
    public Keys PreferencesHotkey { get; set; } = Keys.F3;
    public Keys CustomCaptureHotkey { get; set; } = Keys.Shift | Keys.F1;
    public Keys LongCaptureHotkey { get; set; } = Keys.None;

    // Long screenshot: scroll the target window and stitch frames vertically.
    // Always enabled; the preference page only groups its settings.
    public LongCaptureMode LongCaptureMode { get; set; } = LongCaptureMode.Auto;
    public LongCaptureSpeed LongCaptureSpeed { get; set; } = LongCaptureSpeed.Normal;
    /// <summary>Maximum stitched height in pixels; 0 means no limit.</summary>
    public int LongCaptureMaxHeight { get; set; } = 50000;

    /// <summary>
    /// Full-screen capture excludes the taskbar for ordinary desktop windows and
    /// captures the whole monitor only for full-screen apps/games. When off, the
    /// whole monitor is always captured, taskbar included.
    /// </summary>
    public bool SmartFullScreenCapture { get; set; }
    /// <summary>Delay in seconds before the tray/hotkey delayed capture fires.</summary>
    public int DelayCaptureSeconds { get; set; } = 5;
    /// <summary>Show the small countdown badge; off means a silent background delay.</summary>
    public bool DelayShowCountdown { get; set; } = true;
    public Keys PinHotkey { get; set; } = Keys.None;
    public Keys TogglePinsHotkey { get; set; } = Keys.None;
    public Keys SwitchGroupHotkey { get; set; } = Keys.None;
    public Keys CopyCoordinatesHotkey { get; set; } = Keys.None;
    public Keys TranslationHotkey { get; set; } = Keys.None;
    public Keys OcrHotkey { get; set; } = Keys.None;
    public Keys RedactHotkey { get; set; } = Keys.None;
    public Keys DelayCaptureHotkey { get; set; } = Keys.None;
    /// <summary>Toggles a screen recording at the configured preset resolution.</summary>
    public Keys RecordHotkey { get; set; } = Keys.Multiply;
    /// <summary>Toggles a GIF recording.</summary>
    public Keys GifHotkey { get; set; } = Keys.None;
    public Keys PinZoomModifiers { get; set; } = Keys.None;
    public Keys PinOpacityModifiers { get; set; } = Keys.Control;
    public MouseBinding PinCloseMouse { get; set; } = new(Keys.None, PointerGesture.LeftDoubleClick);
    public MouseBinding PinResetMouse { get; set; } = new(Keys.None, PointerGesture.MiddleClick);
    public MouseBinding PinThumbnailMouse { get; set; } = new(Keys.Shift, PointerGesture.LeftDoubleClick);
    public MouseBinding PinCopyTextMouse { get; set; } = new(Keys.Shift, PointerGesture.RightClick);
    [JsonIgnore] public Dictionary<string, string> HotkeyErrors { get; } = new();

    public Font CreateInterfaceFont() => new(InterfaceFont, InterfaceFontSize);
    public string FormatColor(Color color, bool? hex = null)
    {
        if (!(hex ?? ColorHex)) return $"RGB({color.R}, {color.G}, {color.B})";
        var format = ColorUppercase ? "X2" : "x2";
        return (ColorHideHash ? "" : "#") + color.R.ToString(format) + color.G.ToString(format) + color.B.ToString(format);
    }
    private void NormalizePreferences()
    {
        DisplayLanguage = Localization.LanguageService.NormalizePreference(DisplayLanguage);
        Skin = ThemeManager.Normalize(Skin);
        if (!Enum.IsDefined(OcrEngine)) OcrEngine = OcrEngineKind.SimdPaddle;
        TranslationSourceLanguage = TranslationLanguages.Normalize(TranslationSourceLanguage, allowAuto: true, "en");
        TranslationTargetLanguage = TranslationLanguages.Normalize(TranslationTargetLanguage, allowAuto: false, "zh-CN");
        if (!Enum.IsDefined(TranslationProvider)) TranslationProvider = TranslationProviderKind.Bing;
        InterfaceFontSize = Math.Clamp(InterfaceFontSize, 7, 24);
        TextImageFontSize = Math.Clamp(TextImageFontSize, 6, 100);
        TextImageMargin = Math.Clamp(TextImageMargin, 0, 200);
        TextImageMaxWidth = Math.Clamp(TextImageMaxWidth, 100, 12000);
        CaptureBorderWidth = Math.Clamp(CaptureBorderWidth, 1, 10);
        PinMaxSize = Math.Clamp(PinMaxSize, 100, 20000);
        ThumbnailWidth = Math.Clamp(ThumbnailWidth, 20, 1000);
        ThumbnailHeight = Math.Clamp(ThumbnailHeight, 20, 1000);
        HistoryImageLimit = Math.Clamp(HistoryImageLimit, 0, 100);
        if (DelayCaptureSeconds is not (3 or 5 or 10)) DelayCaptureSeconds = 5;
        if (string.IsNullOrWhiteSpace(QuickSaveDirectory)) QuickSaveDirectory = new AppSettings().QuickSaveDirectory;
        if (string.IsNullOrWhiteSpace(InterfaceFont)) InterfaceFont = "Microsoft YaHei UI";
        if (string.IsNullOrWhiteSpace(TextImageFont)) TextImageFont = "Microsoft YaHei UI";
        if (!Enum.IsDefined(RightClickAction)) RightClickAction = RightClickBehavior.QuickSave;
        if (!Enum.IsDefined(CloseButtonAction)) CloseButtonAction = CloseButtonAction.MinimizeToTray;
    }
}
