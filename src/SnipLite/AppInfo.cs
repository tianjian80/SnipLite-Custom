using System.Reflection;

namespace SnipLite;

internal static class AppInfo
{
    public const string ProductName = "SnipLite修改版";
    public const string RepositoryUrl = "https://github.com/tianjian80/SnipLite-Custom";
    public const string IssuesUrl = RepositoryUrl + "/issues";
    public const string IssuesText = IssuesUrl;
    public const string ReleasesUrl = RepositoryUrl + "/releases";

    public static string Version { get; } = typeof(AppInfo).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "unknown";
}
