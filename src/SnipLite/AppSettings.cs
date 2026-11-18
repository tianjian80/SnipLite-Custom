using System.Text.Json;

namespace SnipLite;

internal sealed partial class AppSettings
{
    public string SaveDirectory { get; set; } = @"E:\";

    public string OcrLanguage { get; set; } = "zh-Hans";
    public OcrEngineKind OcrEngine { get; set; } = OcrEngineKind.SimdPaddle;
    public string TranslationSourceLanguage { get; set; } = "en";
    public string TranslationTargetLanguage { get; set; } = "zh-CN";
    public TranslationProviderKind TranslationProvider { get; set; } = TranslationProviderKind.Bing;

    /// <summary>Alpha of the dimming mask over the frozen desktop (0-200).</summary>
    public int MaskOpacity { get; set; } = 110;

    /// <summary>Magnifier scale used while Alt is held (4-16).</summary>
    public int MagnifierZoom { get; set; } = 6;

    /// <summary>Crosshair over the whole screen before a region is picked.</summary>
    public bool ShowCrosshair { get; set; } = true;

    /// <summary>Pinned images keep their own zoom; this is the opacity they open with.</summary>
    public int PinOpacityPercent { get; set; } = 100;

    /// <summary>Enter copies the result. Turn off to only save.</summary>
    public bool CopyAfterCapture { get; set; } = true;

    /// <summary>Every capture is written to <see cref="SaveDirectory"/> automatically.</summary>
    public bool AutoSave { get; set; }

    public bool HotkeysEnabled { get; set; } = true;

    /// <summary>Snipaste's file name pattern: Chinese date/time tokens such as 年月-日 时.分.</summary>
    public string FileNamePattern { get; set; } = "SnipLite_年月-日 时.分";

    public int JpegQuality { get; set; } = 92;

    /// <summary>Saved screen coordinates of the Preferences window, or MinValue if never positioned.</summary>
    public int PreferencesWindowX { get; set; } = int.MinValue;
    public int PreferencesWindowY { get; set; } = int.MinValue;

    /// <summary>Saved outer size of the Preferences window, or 0 when never resized.</summary>
    public int PreferencesWindowWidth { get; set; }
    public int PreferencesWindowHeight { get; set; }

    /// <summary>Theme colour for the annotation bar: active band, border, selection ring.</summary>
    public int AccentArgb { get; set; } = AnnotationBar.DefaultAccent.ToArgb();

    /// <summary>Show the small result toast after a capture (Snipaste "显示通知").</summary>
    public bool ShowToast { get; set; } = true;

    /// <summary>Mirror of the HKCU Run entry; applied by <see cref="Startup"/>.</summary>
    public bool StartWithWindows { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public Color Accent
    {
        get => Color.FromArgb(AccentArgb | unchecked((int)0xFF000000));
        set => AccentArgb = value.ToArgb();
    }

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnipLite");

    private static string LocationFile => Path.Combine(Dir, "settings-location.txt");
    public static string FilePath
    {
        get
        {
            try { if (File.Exists(LocationFile)) return Path.GetFullPath(File.ReadAllText(LocationFile).Trim()); }
            catch { }
            return Path.Combine(Dir, "settings.json");
        }
    }

    public void MoveConfiguration(string path)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        Directory.CreateDirectory(Dir);
        File.WriteAllText(LocationFile, path);
    }
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                if (loaded is not null) return loaded.Normalized();
            }
        }
        catch
        {
            // a corrupt settings file must never block startup
        }

        return new AppSettings();
    }

    /// <summary>Keep values the UI can produce but the engine cannot survive.</summary>
    private AppSettings Normalized()
    {
        MaskOpacity = Math.Clamp(MaskOpacity, 0, 200);
        MagnifierZoom = Math.Clamp(MagnifierZoom, 2, 16);
        PinOpacityPercent = Math.Clamp(PinOpacityPercent, 10, 100);
        JpegQuality = Math.Clamp(JpegQuality, 30, 100);
        NormalizePreferences();
        NormalizeVideo();

        if (string.IsNullOrWhiteSpace(SaveDirectory)) SaveDirectory = new AppSettings().SaveDirectory;
        if (string.IsNullOrWhiteSpace(FileNamePattern)) FileNamePattern = new AppSettings().FileNamePattern;
        if ((AccentArgb & 0x00FFFFFF) == 0) AccentArgb = AnnotationBar.DefaultAccent.ToArgb();

        return this;
    }

    public static AppSettings Reset() => new();

    /// <summary>Overwrite this instance in place so every holder sees the reset values.</summary>
    public void CopyFrom(AppSettings other)
    {
        foreach (var property in typeof(AppSettings).GetProperties().Where(p => p.CanWrite &&
                     p.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), true).Length == 0))
            property.SetValue(this, property.GetValue(other));
    }

    /// <summary>File name for a capture, expanded from <see cref="FileNamePattern"/>.</summary>
    public string ExpandFileName(DateTime now)
    {
        var name = FileNamePattern.Replace("年", now.ToString("yyyy"))
                   .Replace("月", now.ToString("MM"))
                   .Replace("日", now.ToString("dd"))
                   .Replace("时", now.ToString("HH"))
                   .Replace("分", now.ToString("mm"))
                   .Replace("秒", now.ToString("ss"));

        foreach (var bad in Path.GetInvalidFileNameChars())
            name = name.Replace(bad, '_');

        return string.IsNullOrWhiteSpace(name) ? "SnipLite" : name.Trim();
    }

    public void Save()
    {
        try
        {
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (AutoBackup && File.Exists(path)) File.Copy(path, path + ".bak", true);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, JsonOptions));
            File.Move(path + ".tmp", path, true);
        }
        catch (Exception ex)
        {
            AppLog.Write("无法保存设置", ex);
            Toast.Error(UiText.T("设置保存失败：") + ex.Message);
        }
    }
}
