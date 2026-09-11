namespace AmdHdrScreenshotFixer;

public static class UpdateRouteBuilder
{
    public static IReadOnlyList<UpdateRequestRoute> Build(Uri originalUri, UpdateNetworkSettings? networkSettings)
    {
        ArgumentNullException.ThrowIfNull(originalUri);
        var settings = (networkSettings ?? UpdateNetworkSettings.Default).Normalize();
        if (!originalUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return [new UpdateRequestRoute(originalUri, originalUri.Host, true)];

        return (settings.GithubProxies ?? [])
            .Select((proxy, index) => new { Proxy = proxy, Index = index })
            .Where(item => item.Proxy.Priority > 0)
            .OrderByDescending(item => item.Proxy.Priority)
            .ThenBy(item => item.Index)
            .Select(item => item.Proxy.IsDirect
                ? new UpdateRequestRoute(originalUri, "GitHub 直连", true)
                : new UpdateRequestRoute(new Uri($"{item.Proxy.BaseUrl}/{originalUri.AbsoluteUri}"),
                    new Uri(item.Proxy.BaseUrl).Host, false))
            .ToList();
    }
}
