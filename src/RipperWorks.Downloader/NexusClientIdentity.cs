using System.Net.Http.Headers;
using System.Reflection;

namespace RipperWorks.Downloader;

internal static class NexusClientIdentity
{
    public const string ApplicationName = "RipperWorks";

    public static string ApplicationVersion { get; } = ResolveApplicationVersion(typeof(NexusClientIdentity).Assembly);

    public static string UserAgentValue { get; } = $"{ApplicationName}/{ApplicationVersion}";

    public static ProductInfoHeaderValue ProductInfoHeader { get; } = new(ApplicationName, ApplicationVersion);

    public static void ApplyHeaders(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Headers.Add("Application-Name", ApplicationName);
        request.Headers.Add("Application-Version", ApplicationVersion);
        request.Headers.UserAgent.Add(ProductInfoHeader);
    }

    internal static string ResolveApplicationVersion(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            throw new InvalidOperationException(
                $"Assembly '{assembly.GetName().Name}' does not declare an AssemblyInformationalVersionAttribute.");
        }

        var plusIndex = informationalVersion.IndexOf('+');
        var cleaned = plusIndex >= 0
            ? informationalVersion[..plusIndex].Trim()
            : informationalVersion.Trim();

        if (string.IsNullOrWhiteSpace(cleaned))
        {
            throw new InvalidOperationException(
                $"Assembly '{assembly.GetName().Name}' has an empty or invalid AssemblyInformationalVersion: '{informationalVersion}'.");
        }

        return cleaned;
    }
}
