namespace AmdHdrScreenshotFixer;

public sealed class FixerConfig
{
    public string Suffix { get; set; } = "_fixed";
    public string? CalibrationFile { get; set; } = "calibration.json";
    public string? WatchPath { get; set; }
    public string? SkippedVersion { get; set; }
    public bool StartWithWindows { get; set; }
    public bool AutoUpdateEnabled { get; set; } = true;
    public UpdateNetworkSettings UpdateNetwork { get; set; } = UpdateNetworkSettings.Default;
    public double RedGain { get; set; } = 1.0;
    public double GreenGain { get; set; } = 1.0;
    public double BlueGain { get; set; } = 1.0;
    public double Exposure { get; set; }
    public double PostContrast { get; set; } = 1.0;
    public double PostSaturation { get; set; } = 1.0;
    public double PostBlackPoint { get; set; }
    public double PostWhitePoint { get; set; } = 1.0;
    public double Gamma { get; set; } = 1.0;
    public double BlackPoint { get; set; }
    public double WhitePoint { get; set; } = 1.0;
    public double Contrast { get; set; } = 1.0;
    public double Saturation { get; set; } = 1.0;
}

public sealed class CalibrationData
{
    public string? Mode { get; set; }
    public double[]? Red { get; set; }
    public double[]? Green { get; set; }
    public double[]? Blue { get; set; }
    public int[]? Luma { get; set; }
    public double Saturation { get; set; } = 1.0;
    public int GridSize { get; set; }
    public double[]? Lut3D { get; set; }
    public CalibrationQuality? Quality { get; set; }
}

public sealed class CalibrationQuality
{
    public double MeanDeltaE { get; set; }
    public double P95DeltaE { get; set; }
    public double Score { get; set; }
    public bool MeetsTarget { get; set; }
    public int PairCount { get; set; }
}

public sealed record CalibrationPair(string AmdPath, string ReferencePath);

public sealed record CalibrationProgress(int Percent, string Message);

public sealed record CalibrationResult(CalibrationData Calibration, CalibrationQuality Quality, int GridSize);

public sealed record GithubProxySetting(string BaseUrl, int Priority, bool IsDirect = false);

public sealed record UpdateNetworkSettings(List<GithubProxySetting>? GithubProxies = null, string? HttpProxy = null)
{
    public static UpdateNetworkSettings Default => new([new GithubProxySetting(string.Empty, 10, true)]);

    public UpdateNetworkSettings Normalize()
    {
        var proxies = new List<GithubProxySetting>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasDirect = false;
        foreach (var proxy in GithubProxies ?? [])
        {
            if (proxy.IsDirect)
            {
                if (!hasDirect)
                {
                    proxies.Add(new GithubProxySetting(string.Empty, Math.Clamp(proxy.Priority, 0, 10), true));
                    hasDirect = true;
                }
                continue;
            }
            if (TryNormalizeGithubProxy(proxy.BaseUrl, out var baseUrl) && seen.Add(baseUrl))
                proxies.Add(new GithubProxySetting(baseUrl, Math.Clamp(proxy.Priority, 0, 10)));
        }
        if (!hasDirect) proxies.Insert(0, new GithubProxySetting(string.Empty, proxies.Count == 0 ? 10 : 1, true));
        return new UpdateNetworkSettings(proxies,
            TryNormalizeHttpProxy(HttpProxy, out var httpProxy) ? httpProxy : null);
    }

    public static bool TryNormalizeGithubProxy(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (!TryCreateHttpUri(value, true, out var uri) || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return false;
        normalized = uri.AbsoluteUri.TrimEnd('/');
        return true;
    }

    public static bool TryNormalizeHttpProxy(string? value, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!TryCreateHttpUri(value, false, out var uri) || !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return false;
        normalized = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }

    private static bool TryCreateHttpUri(string? value, bool allowHttps, out Uri uri)
    {
        if (Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var parsed) &&
            (parsed.Scheme == Uri.UriSchemeHttp || allowHttps && parsed.Scheme == Uri.UriSchemeHttps) &&
            !string.IsNullOrWhiteSpace(parsed.Host))
        {
            uri = parsed;
            return true;
        }
        uri = null!;
        return false;
    }
}

public sealed record UpdateRequestRoute(Uri RequestUri, string DisplayName, bool IsDirect);

public sealed record PixelFrame(int Width, int Height, double DpiX, double DpiY, byte[] Pixels, int Stride);

public sealed record WatcherProcessingResult(string SourcePath, string OutputPath);
