using AmdHdrScreenshotFixer.Core;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace AmdHdrScreenshotFixer;

internal sealed record PreparedUpdate(string PackagePath, string ManifestPath, string LauncherPath);

internal sealed class UpdateClient
{
    private const string ManifestBaseUrl =
        "https://github.com/Kratosmax/amd-hdr-screenshot-fixer/releases/latest/download/";
    private static readonly SemaphoreSlim DownloadGate = new(1, 1);
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"
    };

    public static Version CurrentVersion
    {
        get
        {
            var value = typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(value.Major, value.Minor, Math.Max(0, value.Build));
        }
    }

    public static Uri ReleasePageUri { get; } =
        new("https://github.com/Kratosmax/amd-hdr-screenshot-fixer/releases/latest");

    public bool CanInstallInPlace
    {
        get
        {
            try
            {
                _ = UpdateInstaller.GetInstalledChannel(AppContext.BaseDirectory);
                return File.Exists(Path.Combine(AppContext.BaseDirectory, "AmdHdrScreenshotFixer.Updater.exe"));
            }
            catch (InvalidOperationException) { return false; }
        }
    }

    private static string CurrentChannel
    {
        get
        {
            try { return UpdateInstaller.GetInstalledChannel(AppContext.BaseDirectory); }
            catch (InvalidOperationException) { return UpdateTrust.LiteChannel; }
        }
    }

    public async Task<UpdateInfo?> CheckAsync(UpdateNetworkSettings? networkSettings = null,
        CancellationToken cancellationToken = default)
    {
        var settings = (networkSettings ?? UpdateNetworkSettings.Default).Normalize();
        var original = new Uri(ManifestBaseUrl + UpdateTrust.GetManifestFileName(CurrentChannel));
        var routes = UpdateRouteBuilder.Build(original, settings);
        if (routes.Count == 0) throw new InvalidOperationException("没有启用的 GitHub 更新线路。");
        using var client = CreateClient(settings);
        var failures = new List<string>();
        foreach (var route in routes)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(12));
                using var response = await client.GetAsync(route.RequestUri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                response.EnsureSuccessStatusCode();
                EnsureAllowedResponse(response, route);
                if (response.Content.Headers.ContentLength is > UpdateManifestCodec.MaximumManifestSize)
                    throw new InvalidDataException("更新清单超过允许大小。");
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var memory = new MemoryStream();
                await BoundedStream.CopyToAsync(input, memory, UpdateManifestCodec.MaximumManifestSize, timeout.Token);
                var update = UpdateManifestCodec.ParseAndVerify(Encoding.UTF8.GetString(memory.ToArray()), CurrentChannel);
                return update.Version > CurrentVersion ? update : null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { failures.Add($"{route.DisplayName}: {ex.Message}"); }
        }
        throw new HttpRequestException("所有更新线路均失败：" + string.Join("；", failures));
    }

    public async Task<PreparedUpdate> DownloadAsync(UpdateInfo update, IProgress<int> progress,
        UpdateNetworkSettings? networkSettings = null,
        CancellationToken cancellationToken = default)
    {
        if (!CanInstallInPlace) throw new InvalidOperationException("当前目录不能执行就地更新。");
        await DownloadGate.WaitAsync(cancellationToken);
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AmdHdrScreenshotFixer", "updates", update.Version.ToString(3));
            Directory.CreateDirectory(root);
            CleanupOldUpdates(Path.GetDirectoryName(root)!);
            var package = Path.Combine(root, "package.zip");
            var temporary = package + ".download";
            var settings = (networkSettings ?? UpdateNetworkSettings.Default).Normalize();
            var routes = UpdateRouteBuilder.Build(update.DownloadUri, settings);
            if (routes.Count == 0) throw new InvalidOperationException("没有启用的 GitHub 更新线路。");
            using var client = CreateClient(settings);
            var failures = new List<string>();
            var downloaded = false;
            foreach (var route in routes)
            {
                TryDelete(temporary);
                try
                {
                    using var response = await client.GetAsync(route.RequestUri, HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken);
                    response.EnsureSuccessStatusCode();
                    EnsureAllowedResponse(response, route);
                    if (response.Content.Headers.ContentLength is { } size && size != update.Size)
                        throw new InvalidDataException("服务器返回的更新包大小与签名清单不一致。");
                    await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                    await UpdatePackageStager.StageAsync(source, temporary, package, update, progress, cancellationToken);
                    downloaded = true;
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    TryDelete(temporary);
                    failures.Add($"{route.DisplayName}: {ex.Message}");
                }
            }
            if (!downloaded) throw new HttpRequestException("所有下载线路均失败：" + string.Join("；", failures));
            var manifest = Path.Combine(root, "update.json");
            await File.WriteAllTextAsync(manifest, update.RawManifest, new UTF8Encoding(false), cancellationToken);
            var launcherDirectory = Path.Combine(root, "launcher");
            Directory.CreateDirectory(launcherDirectory);
            var files = update.Channel == UpdateTrust.FullChannel
                ? new[] { "AmdHdrScreenshotFixer.Updater.exe" }
                : new[] { "AmdHdrScreenshotFixer.Updater.exe", "AmdHdrScreenshotFixer.Updater.dll",
                    "AmdHdrScreenshotFixer.Updater.deps.json", "AmdHdrScreenshotFixer.Updater.runtimeconfig.json",
                    "AmdHdrScreenshotFixer.Core.dll" };
            foreach (var file in files)
                File.Copy(Path.Combine(AppContext.BaseDirectory, file), Path.Combine(launcherDirectory, file), true);
            progress.Report(100);
            return new PreparedUpdate(package, manifest,
                Path.Combine(launcherDirectory, "AmdHdrScreenshotFixer.Updater.exe"));
        }
        finally { DownloadGate.Release(); }
    }

    public static void LaunchUpdater(PreparedUpdate prepared)
    {
        using var current = Process.GetCurrentProcess();
        var start = new ProcessStartInfo(prepared.LauncherPath)
        {
            UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(prepared.LauncherPath)!
        };
        foreach (var value in new[] { "--package", prepared.PackagePath, "--manifest", prepared.ManifestPath,
                     "--target", AppContext.BaseDirectory, "--pid", Environment.ProcessId.ToString(),
                     "--process-start-ticks", current.StartTime.ToUniversalTime().Ticks.ToString() })
            start.ArgumentList.Add(value);
        _ = Process.Start(start) ?? throw new InvalidOperationException("无法启动外部更新器。");
    }

    private static HttpClient CreateClient(UpdateNetworkSettings settings)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(10), PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };
        if (settings.HttpProxy is not null)
        {
            handler.Proxy = new WebProxy(settings.HttpProxy);
            handler.UseProxy = true;
        }
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AmdHdrScreenshotFixer", CurrentVersion.ToString(3)));
        return client;
    }

    private static void EnsureAllowedResponse(HttpResponseMessage response, UpdateRequestRoute route)
    {
        var uri = response.RequestMessage?.RequestUri;
        var directAllowed = uri?.Scheme == Uri.UriSchemeHttps && AllowedHosts.Contains(uri.Host);
        var proxyAllowed = !route.IsDirect && uri is not null &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
            uri.Host.Equals(route.RequestUri.Host, StringComparison.OrdinalIgnoreCase);
        if (!directAllowed && !proxyAllowed)
            throw new InvalidDataException("更新请求被重定向到不受信任的地址。");
    }

    private static void CleanupOldUpdates(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var directory in Directory.EnumerateDirectories(root))
            try
            {
                if (Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddDays(-14)) Directory.Delete(directory, true);
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
