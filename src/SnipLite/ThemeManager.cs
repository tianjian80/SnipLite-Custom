namespace SnipLite;

/// <summary>The full colour set a skin supplies; every surface in the app derives from it.</summary>
internal sealed record SkinColors(
    Color Window, Color Sidebar, Color Card, Color Field, Color Border, Color Text,
    Color Muted, Color Hover, Color Accent, Color AccentSoft, Color OnAccent, Color Disabled);

/// <summary>One selectable global skin.</summary>
internal sealed record SkinDefinition(string Key, string Name, bool Dark, SkinColors Colors);

/// <summary>
/// Resolves and applies the global skin. <see cref="AppTheme"/> asks for the current
/// palette here, so a skin switch repaints the preferences window, its cards, the
/// dropdowns, the search box and the tray menu through the existing Changed event.
/// </summary>
internal static class ThemeManager
{
    public const string DefaultKey = "Light";

    private static string _key = DefaultKey;

    public static IReadOnlyList<SkinDefinition> Skins { get; } = new[]
    {
        // 简约浅灰: the quiet neutral default.
        new SkinDefinition("Light", "简约浅灰", false, new SkinColors(
            Color.FromArgb(0xF3, 0xF5, 0xF8), Color.FromArgb(0xED, 0xF0, 0xF4),
            Color.FromArgb(0xFF, 0xFF, 0xFF), Color.FromArgb(0xFA, 0xFB, 0xFC),
            Color.FromArgb(0xE2, 0xE6, 0xEC), Color.FromArgb(0x1F, 0x23, 0x29),
            Color.FromArgb(0x66, 0x6F, 0x7A), Color.FromArgb(0xEA, 0xED, 0xF2),
            Color.FromArgb(0x00, 0x67, 0xC0), Color.FromArgb(0xE5, 0xF0, 0xFB),
            Color.FromArgb(0xFF, 0xFF, 0xFF), Color.FromArgb(0xA8, 0xB0, 0xBA))),

        // 深邃暗黑.
        new SkinDefinition("Dark", "深邃暗黑", true, new SkinColors(
            Color.FromArgb(0x18, 0x1A, 0x1F), Color.FromArgb(0x1C, 0x1F, 0x25),
            Color.FromArgb(0x23, 0x27, 0x2F), Color.FromArgb(0x2A, 0x2F, 0x38),
            Color.FromArgb(0x32, 0x38, 0x44), Color.FromArgb(0xEA, 0xEC, 0xEF),
            Color.FromArgb(0x9A, 0xA4, 0xB2), Color.FromArgb(0x2F, 0x35, 0x3F),
            Color.FromArgb(0x5A, 0xA9, 0xF0), Color.FromArgb(0x24, 0x38, 0x4C),
            Color.FromArgb(0x0B, 0x1A, 0x28), Color.FromArgb(0x6C, 0x76, 0x83))),

        // 暖纸护眼.
        new SkinDefinition("Warm", "暖纸护眼", false, new SkinColors(
            Color.FromArgb(0xF5, 0xEF, 0xE6), Color.FromArgb(0xEF, 0xE7, 0xDA),
            Color.FromArgb(0xFC, 0xF9, 0xF2), Color.FromArgb(0xFE, 0xFC, 0xF7),
            Color.FromArgb(0xE3, 0xD9, 0xC6), Color.FromArgb(0x3B, 0x32, 0x28),
            Color.FromArgb(0x7A, 0x6E, 0x5D), Color.FromArgb(0xEF, 0xE6, 0xD6),
            Color.FromArgb(0xA9, 0x71, 0x3B), Color.FromArgb(0xF3, 0xE7, 0xD6),
            Color.FromArgb(0xFF, 0xFF, 0xFF), Color.FromArgb(0xB3, 0xA8, 0x93))),

        // 极客青蓝.
        new SkinDefinition("Tech", "极客青蓝", false, new SkinColors(
            Color.FromArgb(0xEB, 0xF2, 0xF7), Color.FromArgb(0xE2, 0xEC, 0xF4),
            Color.FromArgb(0xF8, 0xFB, 0xFE), Color.FromArgb(0xFF, 0xFF, 0xFF),
            Color.FromArgb(0xC8, 0xD9, 0xEA), Color.FromArgb(0x16, 0x2B, 0x42),
            Color.FromArgb(0x52, 0x6A, 0x84), Color.FromArgb(0xE1, 0xEC, 0xF6),
            Color.FromArgb(0x1F, 0x77, 0xC9), Color.FromArgb(0xDD, 0xEB, 0xF7),
            Color.FromArgb(0xFF, 0xFF, 0xFF), Color.FromArgb(0x93, 0xA9, 0xBE)))
    };

    public static string CurrentKey => _key;

    public static SkinDefinition Current
    {
        get
        {
            // Painting and search-index building read this on hot paths: never throw,
            // an unknown key simply resolves to the default skin.
            try { return Find(_key) ?? Skins[0]; }
            catch { return Skins[0]; }
        }
    }

    public static SkinDefinition? Find(string? key) =>
        Skins.FirstOrDefault(skin => string.Equals(skin.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>A missing or unknown key (old settings.json) falls back to 简约浅灰.</summary>
    public static string Normalize(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return DefaultKey;
        try { return Find(key!)?.Key ?? DefaultKey; }
        catch { return DefaultKey; }
    }

    public static string Name(string? key) => Find(Normalize(key))?.Name ?? "简约浅灰";

    /// <summary>Switches the global skin; a changed palette raises <see cref="AppTheme.Changed"/>.</summary>
    public static void Apply(string? key)
    {
        _key = Normalize(key);
        try { AppTheme.Refresh(); }
        catch (Exception ex)
        {
            // A skin must never take the whole application down.
            AppLog.Write("应用皮肤失败", ex);
            _key = DefaultKey;
        }
    }

    internal static ThemePalette Palette()
    {
        try
        {
            // Windows high contrast always wins: readability beats styling.
            if (SystemInformation.HighContrast) return new ThemePalette(false, false, true);
            var skin = Current;
            return new ThemePalette(skin.Dark, skin.Dark, false, skin.Colors);
        }
        catch (Exception ex)
        {
            AppLog.Write("解析皮肤失败", ex);
            return AppTheme.Fallback;
        }
    }
}
