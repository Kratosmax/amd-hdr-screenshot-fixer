using System.Collections.Concurrent;
using System.IO;

namespace AmdHdrScreenshotFixer;

public sealed class ScreenshotWatcher : IDisposable
{
    private readonly ConfigStore configStore;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim processGate = new(1, 1);
    private FileSystemWatcher? watcher;
    private string? rootPath;

    public ScreenshotWatcher(ConfigStore configStore)
    {
        this.configStore = configStore;
    }

    public event Action<string, bool>? StatusChanged;
    public event Action<WatcherProcessingResult>? ProcessingCompleted;

    public bool IsRunning => watcher?.EnableRaisingEvents == true;

    public void Start(string path)
    {
        Stop();
        rootPath = Path.GetFullPath(path);
        if (!Directory.Exists(rootPath)) throw new DirectoryNotFoundException($"监听目录不存在：{rootPath}");

        watcher = new FileSystemWatcher(rootPath, "*.png")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
            EnableRaisingEvents = false
        };
        watcher.Created += FileChanged;
        watcher.Changed += FileChanged;
        watcher.Renamed += FileRenamed;
        watcher.Error += WatcherError;
        watcher.EnableRaisingEvents = true;
    }

    public void Stop()
    {
        if (watcher is not null)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Created -= FileChanged;
            watcher.Changed -= FileChanged;
            watcher.Renamed -= FileRenamed;
            watcher.Error -= WatcherError;
            watcher.Dispose();
            watcher = null;
        }

        foreach (var item in pending.Values) item.Cancel();
        pending.Clear();
        rootPath = null;
    }

    private void FileChanged(object sender, FileSystemEventArgs e) => Queue(e.FullPath);
    private void FileRenamed(object sender, RenamedEventArgs e) => Queue(e.FullPath);

    private void Queue(string path)
    {
        if (rootPath is null || !Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)) return;
        if (IsGeneratedOutput(rootPath, path)) return;

        var cancellation = new CancellationTokenSource();
        pending.AddOrUpdate(path, cancellation, (_, previous) =>
        {
            previous.Cancel();
            return cancellation;
        });
        _ = ProcessAfterStableAsync(path, cancellation);
    }

    private async Task ProcessAfterStableAsync(string path, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(500, cancellation.Token);
            await WaitUntilStableAsync(path, cancellation.Token);
            var sourceDirectory = Path.GetDirectoryName(path)!;
            var outputDirectory = Path.Combine(sourceDirectory, "fixed");
            var outputPath = Path.Combine(outputDirectory, Path.GetFileName(path));
            await processGate.WaitAsync(cancellation.Token);
            try
            {
                var config = configStore.Load();
                var calibration = configStore.LoadCalibration(config);
                Directory.CreateDirectory(outputDirectory);
                ImageProcessor.Export(path, outputPath, config, calibration,
                    config.RedGain, config.GreenGain, config.BlueGain, config.Exposure, config.PostContrast);
            }
            finally
            {
                processGate.Release();
            }
            ProcessingCompleted?.Invoke(new WatcherProcessingResult(path, outputPath));
            StatusChanged?.Invoke($"已处理：{Path.GetFileName(path)}", false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"监听处理失败：{ex.Message}", true);
        }
        finally
        {
            if (pending.TryGetValue(path, out var current) && ReferenceEquals(current, cancellation))
                pending.TryRemove(path, out _);
            cancellation.Dispose();
        }
    }

    private static async Task WaitUntilStableAsync(string path, CancellationToken cancellation)
    {
        long previousLength = -1;
        long previousWrite = -1;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                var file = new FileInfo(path);
                if (file.Exists && file.Length > 0 && file.Length == previousLength && file.LastWriteTimeUtc.Ticks == previousWrite)
                {
                    using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    return;
                }
                previousLength = file.Exists ? file.Length : -1;
                previousWrite = file.Exists ? file.LastWriteTimeUtc.Ticks : -1;
            }
            catch (IOException)
            {
            }
            await Task.Delay(250, cancellation);
        }
        throw new IOException("等待文件写入完成超时。");
    }

    private static bool IsGeneratedOutput(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        var directories = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return directories.Take(Math.Max(0, directories.Length - 1))
            .Any(part => part.Equals("fixed", StringComparison.OrdinalIgnoreCase));
    }

    private void WatcherError(object sender, ErrorEventArgs e)
    {
        StatusChanged?.Invoke($"监听异常：{e.GetException().Message}", true);
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
