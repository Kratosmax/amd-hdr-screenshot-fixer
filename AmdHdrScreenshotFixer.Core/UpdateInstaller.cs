using System.IO.Compression;
using System.Text.Json;

namespace AmdHdrScreenshotFixer.Core;

public static class UpdateInstaller
{
    private const string TransactionDirectory = ".amd-hdr-screenshot-fixer-update";
    private const string MarkerName = "amd-hdr-screenshot-fixer-install.json";

    public static string GetInstalledChannel(string targetDirectory)
    {
        var root = EnsureInstallRoot(targetDirectory);
        var marker = JsonSerializer.Deserialize<PackageMetadata>(File.ReadAllText(Path.Combine(root, MarkerName)))
            ?? throw new InvalidOperationException("安装标记无效。");
        return marker.Channel;
    }

    public static string EnsureInstallRoot(string targetDirectory, string? expectedChannel = null)
    {
        var root = Path.GetFullPath(targetDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var markerPath = Path.Combine(root, MarkerName);
        if (!File.Exists(markerPath) || !File.Exists(Path.Combine(root, "AmdHdrScreenshotFixer.exe")))
            throw new InvalidOperationException("当前目录不是可更新的正式安装目录。");
        var marker = JsonSerializer.Deserialize<PackageMetadata>(File.ReadAllText(markerPath));
        if (marker?.ProductId != UpdateTrust.ProductId || !UpdateTrust.IsSupportedChannel(marker.Channel) ||
            expectedChannel is not null && marker.Channel != expectedChannel)
            throw new InvalidOperationException("安装目录的产品或更新通道不匹配。");
        return root;
    }

    public static async Task InstallAsync(string packagePath, string targetDirectory, UpdateInfo update,
        CancellationToken cancellationToken = default)
    {
        var target = EnsureInstallRoot(targetDirectory, update.Channel);
        await UpdatePackageValidator.ValidateAsync(packagePath, update, cancellationToken);
        var transaction = Path.Combine(target, TransactionDirectory, Guid.NewGuid().ToString("N"));
        var stage = Path.Combine(transaction, "stage");
        var backup = Path.Combine(transaction, "backup");
        Directory.CreateDirectory(stage);
        Directory.CreateDirectory(backup);
        var installed = new List<string>();
        var backups = new List<(string Target, string Backup)>();
        try
        {
            await ExtractAsync(packagePath, stage, cancellationToken);
            foreach (var source in Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(stage, source);
                var destination = ResolveInside(target, relative);
                var old = ResolveInside(backup, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (File.Exists(destination))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(old)!);
                    File.Move(destination, old);
                    backups.Add((destination, old));
                }
                File.Move(source, destination);
                installed.Add(destination);
            }
        }
        catch
        {
            foreach (var path in installed.AsEnumerable().Reverse()) TryDelete(path);
            foreach (var (destination, old) in backups.AsEnumerable().Reverse())
                if (File.Exists(old)) File.Move(old, destination, true);
            throw;
        }
        finally { TryDeleteDirectory(transaction); }
    }

    private static async Task ExtractAsync(string packagePath, string stage, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        foreach (var entry in archive.Entries)
        {
            var relative = UpdatePackageValidator.NormalizeRelativePath(entry.FullName);
            if (relative.Length == 0) continue;
            var destination = ResolveInside(stage, relative);
            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(destination); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = entry.Open();
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, true);
            await BoundedStream.CopyToAsync(input, output, UpdateManifestCodec.MaximumPackageSize, cancellationToken);
        }
    }

    private static string ResolveInside(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"路径超出允许目录：{relative}");
        return fullPath;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
