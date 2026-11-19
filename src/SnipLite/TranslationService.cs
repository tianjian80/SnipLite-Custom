using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace SnipLite;

internal sealed record TranslationLanguage(string Code, string Name);

internal static class TranslationLanguages
{
    public static readonly TranslationLanguage[] All =
    {
        new("auto", "自动检测语言"), new("en", "英语"), new("zh-CN", "中文简体"), new("zh-TW", "中文繁体"),
        new("ja", "日语"), new("ko", "韩语"), new("fr", "法语"), new("de", "德语"), new("es", "西班牙语"),
        new("ru", "俄语"), new("it", "意大利语"), new("pt", "葡萄牙语")
    };

    public static string Normalize(string? code, bool allowAuto, string fallback)
        => All.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase) && (allowAuto || l.Code != "auto"))?.Code ?? fallback;
    public static string Name(string? code) => UiText.T(All.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase))?.Name ?? code ?? "未知语言");
}

internal sealed record TranslationResult(string Text, string? DetectedLanguage);

internal enum TranslationProviderKind { Google, Microsoft, Bing, MyMemory, Baidu }

internal static class TranslationProviders
{
    /// <summary>Key-free engines, listed in their own preferences card.</summary>
    public static readonly (TranslationProviderKind Kind, string Label)[] FreeChoices =
    {
        (TranslationProviderKind.Bing, "必应翻译"),
        (TranslationProviderKind.Google, "谷歌翻译"),
        (TranslationProviderKind.MyMemory, "翻译记忆")
    };

    /// <summary>Credentialed engines, listed in the dedicated paid card.</summary>
    public static readonly (TranslationProviderKind Kind, string Label)[] PaidChoices =
    {
        (TranslationProviderKind.Microsoft, "微软翻译"),
        (TranslationProviderKind.Baidu, "百度翻译")
    };

    public static readonly (TranslationProviderKind Kind, string Label)[] Choices =
        FreeChoices.Concat(PaidChoices).ToArray();

    public static string Label(TranslationProviderKind kind)
        => UiText.T(Choices.FirstOrDefault(choice => choice.Kind == kind).Label ?? Choices[0].Label);

    public static ITranslationProvider Create(TranslationProviderKind kind) => kind switch
    {
        TranslationProviderKind.Google => new GooglePublicTranslationProvider(),
        TranslationProviderKind.Microsoft => new MicrosoftTranslationProvider(),
        TranslationProviderKind.MyMemory => new MyMemoryTranslationProvider(),
        TranslationProviderKind.Baidu => new BaiduTranslationProvider(),
        _ => BingWebTranslationProvider.Shared
    };
}

internal interface ITranslationProvider
{
    Task<TranslationResult> TranslateAsync(string text, string source, string target, CancellationToken cancellationToken);
}

/// <summary>Google's public web endpoint, not the credentialed Cloud Translation API.</summary>
internal sealed class GooglePublicTranslationProvider : ITranslationProvider
{
    private static readonly HttpClient SharedClient = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(12), MaxResponseContentBufferSize = 1_048_576
    };
    private readonly HttpClient _client;
    public GooglePublicTranslationProvider(HttpClient? client = null) => _client = client ?? SharedClient;

    private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    public async Task<TranslationResult> TranslateAsync(string text, string source, string target, CancellationToken cancellationToken)
        => (await TranslateBatchAsync(new[] { text }, source, target, cancellationToken).ConfigureAwait(false))[0];

    public async Task<IReadOnlyList<TranslationResult>> TranslateBatchAsync(IReadOnlyList<string> texts,
        string source, string target, CancellationToken cancellationToken)
    {
        if (texts.Count == 0) return Array.Empty<TranslationResult>();
        if (texts.Any(string.IsNullOrWhiteSpace)) throw new InvalidOperationException(UiText.T("没有识别到可翻译的文字，请调整选区后重试。"));
        if (texts.Sum(text => text.Length) > 12_000) throw new InvalidOperationException(UiText.T("文字较多，请缩小截图选区后翻译（最多 12000 字符）。"));
        source = TranslationLanguages.Normalize(source, true, "en");
        target = TranslationLanguages.Normalize(target, false, "zh-CN");
        cancellationToken.ThrowIfCancellationRequested();
        if (source == target) return texts.Select(text => new TranslationResult(text, source)).ToArray();

        var marker = CreateBatchMarker(texts);
        var merged = texts.Count == 1 ? texts[0] : string.Join($"\n\n{marker}\n\n", texts);
        try
        {
            var result = await TranslateGoogleOnceAsync(merged, source, target, cancellationToken).ConfigureAwait(false);
            return SplitBatchResult(result, marker, texts.Count);
        }
        catch (InvalidOperationException) when (!cancellationToken.IsCancellationRequested)
        {
            // Do not retry Google line by line after throttling; degrade to the existing free Bing channel.
            try
            {
                var fallback = await BingWebTranslationProvider.Shared.TranslateAsync(merged, source, target, cancellationToken).ConfigureAwait(false);
                return SplitBatchResult(fallback, marker, texts.Count);
            }
            catch (InvalidOperationException error)
            {
                throw new InvalidOperationException(UiText.T("谷歌翻译暂时不可用，备用翻译也未能完成，请稍后重试。"), error);
            }
        }
    }

    private async Task<TranslationResult> TranslateGoogleOnceAsync(string text, string source, string target, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(35));
        var url = "https://translate.googleapis.com/translate_a/single?client=gtx&dt=t&ie=UTF-8&oe=UTF-8"
            + $"&sl={Uri.EscapeDataString(source)}&tl={Uri.EscapeDataString(target)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["q"] = text })
        };
        request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9,zh-CN;q=0.8,zh;q=0.7");
        request.Headers.Referrer = new Uri("https://translate.google.com/");
        try
        {
            using var response = await _client.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new InvalidOperationException(UiText.T("翻译请求过于频繁，请稍后重试。"));
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(UiText.T("翻译服务暂时不可用，请稍后重试。"));
            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return ParseResponse(json);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new InvalidOperationException(UiText.T("翻译请求超时，请检查网络后重试。")); }
        catch (HttpRequestException)
        { throw new InvalidOperationException(UiText.T("无法连接翻译服务，请检查网络后重试。")); }
        catch (JsonException)
        { throw new InvalidOperationException(UiText.T("翻译服务返回了无法读取的结果，请稍后重试。")); }
    }

    private static string CreateBatchMarker(IReadOnlyList<string> texts)
    {
        if (texts.Count == 1) return "";
        var input = string.Concat(texts);
        string marker;
        do { marker = Random.Shared.NextInt64(10_000_000_000, 100_000_000_000).ToString(CultureInfo.InvariantCulture); }
        while (input.Contains(marker, StringComparison.Ordinal));
        return marker;
    }

    private static IReadOnlyList<TranslationResult> SplitBatchResult(TranslationResult merged, string marker, int count)
    {
        if (count == 1) return new[] { merged };
        var parts = merged.Text.Split(marker, StringSplitOptions.TrimEntries);
        if (parts.Length != count || parts.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException(UiText.T("翻译服务返回了无法拆分的结果，请稍后重试。"));
        return parts.Select(part => new TranslationResult(part, merged.DetectedLanguage)).ToArray();
    }

    internal static TranslationResult ParseResponse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0 || root[0].ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException(UiText.T("翻译服务返回了无效结果，请稍后重试。"));
        var text = new StringBuilder();
        foreach (var segment in root[0].EnumerateArray())
            if (segment.ValueKind == JsonValueKind.Array && segment.GetArrayLength() > 0 && segment[0].ValueKind == JsonValueKind.String)
                text.Append(segment[0].GetString());
        if (text.Length == 0) throw new InvalidOperationException(UiText.T("翻译服务未返回译文，请稍后重试。"));
        var detected = root.GetArrayLength() > 2 && root[2].ValueKind == JsonValueKind.String ? root[2].GetString() : null;
        return new(text.ToString(), detected);
    }

    // Keep short paragraphs intact; split unusually long ones at whitespace and never inside a surrogate pair.
    internal static IEnumerable<(string Text, string Separator)> SplitText(string text)
    {
        var offset = 0;
        while (offset < text.Length)
        {
            var end = Math.Min(offset + 600, text.Length);
            if (end < text.Length)
            {
                var split = end;
                while (split > offset + 300 && !char.IsWhiteSpace(text[split - 1])) split--;
                if (split > offset + 300) end = split;
                if (char.IsHighSurrogate(text[end - 1])) end--;
            }
            var raw = text[offset..end];
            var trimmed = raw.TrimEnd();
            var trailing = raw[trimmed.Length..];
            if (trimmed.Length > 0) yield return (trimmed, trailing);
            else yield return ("", trailing);
            offset = end;
        }
    }
}
