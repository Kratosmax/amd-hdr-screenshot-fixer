namespace AmdHdrScreenshotFixer.Core;

public static class BoundedStream
{
    public static async Task<long> CopyToAsync(Stream source, Stream destination, long maximumBytes,
        CancellationToken cancellationToken = default)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) return total;
            total += read;
            if (total > maximumBytes) throw new InvalidDataException("下载内容超过允许大小。");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }
}

public static class UpdatePackageStager
{
    public static async Task StageAsync(Stream source, string temporaryPath, string packagePath, UpdateInfo update,
        IProgress<int> progress, CancellationToken cancellationToken = default)
    {
        try
        {
            await using (var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[64 * 1024];
                long total = 0;
                while (true)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    idle.CancelAfter(TimeSpan.FromSeconds(30));
                    var read = await source.ReadAsync(buffer, idle.Token);
                    if (read == 0) break;
                    total += read;
                    if (total > update.Size || total > UpdateManifestCodec.MaximumPackageSize)
                        throw new InvalidDataException("下载内容超过签名清单声明的大小。");
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    progress.Report((int)Math.Min(99, total * 100 / update.Size));
                }
                if (total != update.Size) throw new EndOfStreamException("更新包下载不完整。");
                await destination.FlushAsync(cancellationToken);
            }
            await UpdatePackageValidator.ValidateAsync(temporaryPath, update, cancellationToken);
            File.Move(temporaryPath, packagePath, true);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
