using System.Security.Cryptography;
using System.Text.Json;
using AmdHdrScreenshotFixer.Core;

try
{
    var options = Parse(args.Skip(1).ToArray());
    switch (args.FirstOrDefault())
    {
        case "metadata": await MetadataAsync(options); break;
        case "manifest": await ManifestAsync(options); break;
        case "verify": await VerifyAsync(options); break;
        default: throw new ArgumentException("用法：metadata|manifest|verify [参数]");
    }
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }

static async Task MetadataAsync(IReadOnlyDictionary<string, string> options)
{
    var metadata = new PackageMetadata { Version = VersionValue(options).ToString(3), Channel = Channel(options) };
    await File.WriteAllTextAsync(Required(options, "output"),
        JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
}

static async Task ManifestAsync(IReadOnlyDictionary<string, string> options)
{
    var version = VersionValue(options);
    var channel = Channel(options);
    var packagePath = Path.GetFullPath(Required(options, "package"));
    var notes = await File.ReadAllTextAsync(Required(options, "release-notes"));
    var file = new FileInfo(packagePath);
    await using var stream = file.OpenRead();
    var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream));
    var unsigned = new UpdateManifest
    {
        Version = version.ToString(3), Channel = channel, DownloadUrl = Required(options, "download-url"),
        Size = file.Length, Sha256 = hash, ReleaseNotes = notes
    };
    await UpdatePackageValidator.ValidateAsync(packagePath,
        new UpdateInfo(version, channel, new Uri(unsigned.DownloadUrl), file.Length, hash, notes, ""));
    var signed = new UpdateManifest
    {
        SchemaVersion = unsigned.SchemaVersion, ProductId = unsigned.ProductId, Version = unsigned.Version,
        Channel = unsigned.Channel, DownloadUrl = unsigned.DownloadUrl, Size = unsigned.Size,
        Sha256 = unsigned.Sha256, ReleaseNotes = unsigned.ReleaseNotes,
        Signature = UpdateManifestCodec.Sign(unsigned, await File.ReadAllTextAsync(Required(options, "private-key")))
    };
    await File.WriteAllTextAsync(Required(options, "output"), UpdateManifestCodec.Serialize(signed));
}

static async Task VerifyAsync(IReadOnlyDictionary<string, string> options)
{
    var update = UpdateManifestCodec.ParseAndVerify(await File.ReadAllTextAsync(Required(options, "manifest")), Channel(options));
    await UpdatePackageValidator.ValidateAsync(Required(options, "package"), update);
    Console.WriteLine($"VERIFIED {update.Version.ToString(3)} {update.Channel} {update.Sha256}");
}

static Version VersionValue(IReadOnlyDictionary<string, string> options) =>
    Version.TryParse(Required(options, "version"), out var value) && value.Build >= 0 && value.Revision < 0
        ? value : throw new ArgumentException("version 必须是三段数字版本。");

static string Channel(IReadOnlyDictionary<string, string> options)
{
    var value = Required(options, "channel");
    return UpdateTrust.IsSupportedChannel(value) ? value : throw new ArgumentException("channel 不受支持。");
}

static Dictionary<string, string> Parse(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length; i += 2)
    {
        if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("参数格式无效。");
        result.Add(args[i][2..], args[i + 1]);
    }
    return result;
}

static string Required(IReadOnlyDictionary<string, string> options, string name) =>
    options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value : throw new ArgumentException($"缺少参数 --{name}。");
