using System.Security.Cryptography;
using System.Text.Json;

namespace SnipLite;

/// <summary>Baidu Translate open-platform credentials (APP ID + secret key), DPAPI encrypted.</summary>
internal sealed class BaiduTranslatorCredentials(string appId, string secret)
{
    public string AppId { get; } = appId;
    public string Secret { get; } = secret;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnipLite", "baidu-translator.dat");

    public static bool HasKey()
    {
        try
        {
            var credentials = Load();
            return !string.IsNullOrWhiteSpace(credentials?.AppId) && !string.IsNullOrWhiteSpace(credentials?.Secret);
        }
        catch (InvalidOperationException) { return false; }
    }

    public static BaiduTranslatorCredentials? Load()
    {
        if (!File.Exists(FilePath)) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser);
            try { return JsonSerializer.Deserialize<BaiduTranslatorCredentials>(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(UiText.T("无法读取百度翻译密钥，请在首选项中重新配置。"));
        }
    }

    public static void Save(string appId, string secret)
    {
        if (appId.Length == 0 || secret.Length == 0)
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
            return;
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(new BaiduTranslatorCredentials(appId, secret));
        try
        {
            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temporary = FilePath + ".tmp";
            File.WriteAllBytes(temporary, encrypted);
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
