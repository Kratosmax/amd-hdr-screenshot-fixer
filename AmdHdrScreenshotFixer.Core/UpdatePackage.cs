using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AmdHdrScreenshotFixer.Core;

public sealed class PackageMetadata
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; } = 1;
    [JsonPropertyName("productId")] public string ProductId { get; init; } = UpdateTrust.ProductId;
    [JsonPropertyName("version")] public string Version { get; init; } = string.Empty;
    [JsonPropertyName("channel")] public string Channel { get; init; } = string.Empty;
}

public static class UpdatePackageValidator
{
    private const int MaximumEntries = 4096;
    private const long MaximumExpandedSize = 600L * 1024 * 1024;
    private const int MaximumMetadataSize = 16 * 1024;
    private const string TransactionDirectory = ".amd-hdr-screenshot-fixer-update";

    public static async Task ValidateAsync(string packagePath, UpdateInfo update,
        CancellationToken cancellationToken = default)
    {
        var file = new FileInfo(packagePath);
        if (!file.Exists || file.Length != update.Size) throw new InvalidDataException("更新包大小与清单不一致。");
        await using (var stream = file.OpenRead())
        {
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            if (!hash.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException("更新包 SHA-256 校验失败。");
        }

        using var archive = ZipFile.OpenRead(packagePath);
        if (archive.Entries.Count is 0 or > MaximumEntries) throw new InvalidDataException("更新包文件数量无效。");
        long expanded = 0;
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = NormalizeRelativePath(entry.FullName);
            expanded = checked(expanded + entry.Length);
            if (expanded > MaximumExpandedSize) throw new InvalidDataException("更新包解压后大小超出允许范围。");
            if (!string.IsNullOrEmpty(entry.Name) && !files.Add(path))
                throw new InvalidDataException($"更新包包含重复路径：{path}");
        }

        foreach (var required in new[] { "AmdHdrScreenshotFixer.exe", "AmdHdrScreenshotFixer.Updater.exe",
                     "amd-hdr-screenshot-fixer-install.json", "amd-hdr-screenshot-fixer-package.json" })
            if (!files.Contains(required)) throw new InvalidDataException($"更新包缺少必需文件：{required}");
        if (update.Channel == UpdateTrust.LiteChannel)
            foreach (var required in new[] { "AmdHdrScreenshotFixer.dll", "AmdHdrScreenshotFixer.Updater.dll",
                         "AmdHdrScreenshotFixer.Updater.deps.json", "AmdHdrScreenshotFixer.Updater.runtimeconfig.json",
                         "AmdHdrScreenshotFixer.Core.dll" })
                if (!files.Contains(required)) throw new InvalidDataException($"Lite 更新包缺少必需文件：{required}");

        var metadata = await ReadMetadataAsync(archive, cancellationToken);
        if (metadata.SchemaVersion != 1 || metadata.ProductId != UpdateTrust.ProductId ||
            metadata.Channel != update.Channel || !Version.TryParse(metadata.Version, out var packageVersion) ||
            packageVersion != update.Version)
            throw new InvalidDataException("更新包元数据与清单不匹配。");
    }

    public static string NormalizeRelativePath(string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName) || entryName.IndexOf('\0') >= 0)
            throw new InvalidDataException("更新包包含空路径。");
        var normalized = entryName.Replace('\\', '/').TrimEnd('/');
        if (normalized.Length == 0) return string.Empty;
        var segments = normalized.Split('/');
        if (normalized.StartsWith('/') || normalized.Contains(':') ||
            segments.Any(segment => segment is "" or "." or "..") ||
            segments[0].Equals(TransactionDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"更新包包含不安全路径：{entryName}");
        return normalized;
    }

    private static async Task<PackageMetadata> ReadMetadataAsync(ZipArchive archive, CancellationToken cancellationToken)
    {
        var entry = archive.GetEntry("amd-hdr-screenshot-fixer-package.json")
            ?? throw new InvalidDataException("更新包缺少元数据。");
        if (entry.Length <= 0 || entry.Length > MaximumMetadataSize) throw new InvalidDataException("包元数据大小无效。");
        await using var input = entry.Open();
        using var memory = new MemoryStream((int)entry.Length);
        await BoundedStream.CopyToAsync(input, memory, MaximumMetadataSize, cancellationToken);
        memory.Position = 0;
        return await JsonSerializer.DeserializeAsync<PackageMetadata>(memory, cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("包元数据为空。");
    }
}
