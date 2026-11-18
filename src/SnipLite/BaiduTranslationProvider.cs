using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SnipLite;

/// <summary>
/// Baidu Translate open-platform "general text translation" API.
/// sign = MD5(appid + q + salt + secret).ToLower(), posted as form fields.
/// </summary>
internal sealed class BaiduTranslationProvider(
    HttpClient? client = null, BaiduTranslatorCredentials? credentials = null) : ITranslationProvider
{
    private const string Endpoint = "https://fanyi-api.baidu.com/api/trans/vip/translate";

    private static readonly HttpClient SharedClient = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(12),
        MaxResponseContentBufferSize = 1_048_576
    };

    public async Task<TranslationResult> TranslateAsync(string text, string source, string target, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException(UiText.T("没有识别到可翻译的文字，请调整选区后重试。"));
        if (text.Length > 12_000)
            throw new InvalidOperationException(UiText.T("文字较多，请缩小截图选区后翻译（最多 12000 字符）。"));
        source = TranslationLanguages.Normalize(source, true, "en");
        target = TranslationLanguages.Normalize(target, false, "zh-CN");
        var settings = credentials ?? BaiduTranslatorCredentials.Load();
        if (string.IsNullOrWhiteSpace(settings?.AppId) || string.IsNullOrWhiteSpace(settings.Secret))
            throw new InvalidOperationException(UiText.T("请在首选项 → 常规 → 专业翻译中配置百度翻译 APP ID 与密钥。"));
        if (source == target) return new(text, source);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(35));
        try
        {
            var translated = new StringBuilder();
            string? detected = null;
            foreach (var (chunk, separator) in GooglePublicTranslationProvider.SplitText(text))
            {
                if (string.IsNullOrWhiteSpace(chunk)) { translated.Append(separator); continue; }
                var (result, from) = await TranslateChunkAsync(settings, chunk, source, target, timeout.Token).ConfigureAwait(false);
                translated.Append(result).Append(separator);
                detected ??= from;
            }
            return new(translated.ToString(), detected ?? (source == "auto" ? null : source));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new InvalidOperationException(UiText.T("百度翻译请求超时，请稍后重试。")); }
        catch (HttpRequestException)
        { throw new InvalidOperationException(UiText.T("无法连接百度翻译服务，请检查网络。")); }
        catch (JsonException)
        { throw new InvalidOperationException(UiText.T("百度翻译返回了无法读取的结果，请稍后重试。")); }
    }

    private async Task<(string Text, string? From)> TranslateChunkAsync(
        BaiduTranslatorCredentials settings, string chunk, string source, string target, CancellationToken cancellationToken)
    {
        var salt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["q"] = chunk,
            ["from"] = ToBaiduCode(source),
            ["to"] = ToBaiduCode(target),
            ["appid"] = settings.AppId,
            ["salt"] = salt,
            ["sign"] = Sign(settings.AppId, chunk, salt, settings.Secret)
        });
        using var response = await (client ?? SharedClient).PostAsync(Endpoint, content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(Localization.LanguageService.Format("百度翻译请求失败（HTTP {0}）。", (int)response.StatusCode));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var root = document.RootElement;
        if (root.TryGetProperty("error_code", out var code))
        {
            var codeText = code.ValueKind == JsonValueKind.String ? code.GetString() : code.GetRawText();
            var message = root.TryGetProperty("error_msg", out var reason) && reason.ValueKind == JsonValueKind.String
                ? reason.GetString() : null;
            throw new InvalidOperationException((codeText is "52003" or "54001"
                ? UiText.T("百度翻译拒绝了请求，请检查 APP ID 与密钥。")
                : UiText.T("百度翻译请求失败：")) + (string.IsNullOrWhiteSpace(message) ? "" : " " + message));
        }
        if (!root.TryGetProperty("trans_result", out var results) || results.ValueKind != JsonValueKind.Array
            || results.GetArrayLength() == 0)
            throw new JsonException();
        var translated = new StringBuilder();
        foreach (var item in results.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("dst", out var dst)
                && dst.ValueKind == JsonValueKind.String)
                translated.Append(dst.GetString());
        }
        if (translated.Length == 0) throw new JsonException();
        var from = root.TryGetProperty("from", out var language) && language.ValueKind == JsonValueKind.String
            ? language.GetString() : null;
        return (translated.ToString(), from);
    }

    internal static string Sign(string appId, string text, string salt, string secret)
    {
        var raw = Encoding.UTF8.GetBytes(appId + text + salt + secret);
        return Convert.ToHexString(MD5.HashData(raw)).ToLowerInvariant();
    }

    /// <summary>Maps the app's language codes to Baidu's short codes.</summary>
    internal static string ToBaiduCode(string code) => code switch
    {
        "zh-CN" => "zh",
        "zh-TW" => "cht",
        "ja" => "jp",
        "ko" => "kor",
        "fr" => "fra",
        "es" => "spa",
        _ => code
    };
}
