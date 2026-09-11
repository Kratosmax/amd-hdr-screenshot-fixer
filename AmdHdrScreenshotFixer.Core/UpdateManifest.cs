using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AmdHdrScreenshotFixer.Core;

public sealed class UpdateManifest
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; } = 1;
    [JsonPropertyName("productId")] public string ProductId { get; init; } = UpdateTrust.ProductId;
    [JsonPropertyName("version")] public string Version { get; init; } = string.Empty;
    [JsonPropertyName("channel")] public string Channel { get; init; } = string.Empty;
    [JsonPropertyName("downloadUrl")] public string DownloadUrl { get; init; } = string.Empty;
    [JsonPropertyName("size")] public long Size { get; init; }
    [JsonPropertyName("sha256")] public string Sha256 { get; init; } = string.Empty;
    [JsonPropertyName("releaseNotes")] public string ReleaseNotes { get; init; } = string.Empty;
    [JsonPropertyName("signature")] public string Signature { get; init; } = string.Empty;
}

public sealed record UpdateInfo(Version Version, string Channel, Uri DownloadUri, long Size,
    string Sha256, string ReleaseNotes, string RawManifest);

public static class UpdateManifestCodec
{
    public const long MaximumPackageSize = 250L * 1024 * 1024;
    public const int MaximumManifestSize = 64 * 1024;
    private const string ReleasePathPrefix = "/Kratosmax/amd-hdr-screenshot-fixer/releases/download/";
    private static readonly JsonSerializerOptions StrictJson = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static UpdateInfo ParseAndVerify(string json, string expectedChannel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        if (Encoding.UTF8.GetByteCount(json) > MaximumManifestSize)
            throw new InvalidDataException("更新清单超过允许大小。");

        var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, StrictJson)
            ?? throw new InvalidDataException("更新清单为空。");
        Validate(manifest, expectedChannel, true);
        byte[] signature;
        try { signature = Convert.FromBase64String(manifest.Signature); }
        catch (FormatException ex) { throw new InvalidDataException("更新清单签名格式无效。", ex); }

        using var rsa = RSA.Create();
        rsa.ImportFromPem(UpdateTrust.PublicKeyPem);
        if (!rsa.VerifyData(Encoding.UTF8.GetBytes(CanonicalPayload(manifest)), signature,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new CryptographicException("更新清单签名验证失败。");

        return new UpdateInfo(Version.Parse(manifest.Version), manifest.Channel, new Uri(manifest.DownloadUrl),
            manifest.Size, manifest.Sha256.ToUpperInvariant(), manifest.ReleaseNotes, json);
    }

    public static string Sign(UpdateManifest manifest, string privateKeyPem)
    {
        Validate(manifest, manifest.Channel, false);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        return Convert.ToBase64String(rsa.SignData(Encoding.UTF8.GetBytes(CanonicalPayload(manifest)),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    public static string Serialize(UpdateManifest manifest) =>
        JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });

    private static void Validate(UpdateManifest manifest, string expectedChannel, bool requireSignature)
    {
        if (manifest.SchemaVersion != 1 || manifest.ProductId != UpdateTrust.ProductId)
            throw new InvalidDataException("更新清单产品或格式不匹配。");
        if (!Version.TryParse(manifest.Version, out var version) || version.Build < 0 || version.Revision >= 0)
            throw new InvalidDataException("更新版本必须是三段数字版本。");
        if (manifest.Channel != expectedChannel || !UpdateTrust.IsSupportedChannel(manifest.Channel))
            throw new InvalidDataException("更新通道不匹配。");
        if (!Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith(ReleasePathPrefix, StringComparison.Ordinal))
            throw new InvalidDataException("更新地址不在允许的 GitHub Release 范围内。");
        if (manifest.Size <= 0 || manifest.Size > MaximumPackageSize)
            throw new InvalidDataException("更新包大小超出允许范围。");
        if (manifest.Sha256.Length != 64 || !manifest.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("更新包 SHA-256 格式无效。");
        if (Encoding.UTF8.GetByteCount(manifest.ReleaseNotes) > 16 * 1024)
            throw new InvalidDataException("更新说明超过允许大小。");
        if (requireSignature && string.IsNullOrWhiteSpace(manifest.Signature))
            throw new InvalidDataException("更新清单缺少签名。");
    }

    private static string CanonicalPayload(UpdateManifest manifest) => string.Join('\n',
        manifest.SchemaVersion.ToString(CultureInfo.InvariantCulture), manifest.ProductId, manifest.Version,
        manifest.Channel, manifest.DownloadUrl, manifest.Size.ToString(CultureInfo.InvariantCulture),
        manifest.Sha256.ToUpperInvariant());
}
