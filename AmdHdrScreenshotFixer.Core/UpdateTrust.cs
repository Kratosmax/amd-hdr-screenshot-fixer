namespace AmdHdrScreenshotFixer.Core;

public static class UpdateTrust
{
    public const string ProductId = "AmdHdrScreenshotFixer.Windows.Desktop";
    public const string LiteChannel = "portable-framework-dependent";
    public const string FullChannel = "portable-self-contained";

    public static bool IsSupportedChannel(string channel) => channel is LiteChannel or FullChannel;

    public static string GetManifestFileName(string channel) => channel switch
    {
        LiteChannel => "update-lite.json",
        FullChannel => "update-full.json",
        _ => throw new InvalidDataException("不支持的更新通道。")
    };

    public const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAuLe3EgGC8C/mLEUIr8cC
        j/2uuQ0VmGh9ImIsdEUoDgqckr2A5IpvDwB9fsWFJEvqMQOyX2ob9zgcxuXw+eA8
        ZEX5h4IQslxg1vOpJvimZrep5ggMffZHA5VijjT+Olm/yIYM3FQVhL1Vtg2f4rSi
        uXfUbM9844fqRwYXBrzFAeR6ZipiNiCsY7ShujZCFUTSU60HmOBaylaKQUAh405t
        Faf20s1F0xxi3Urp+w4xO57u7ybQayCoQwbHZTrih0CNuqV1i3QU8vTg0UGgwyCo
        rjx+ylPeuV+E3io7lP7gA/kTgbV2UcQs3X49TJ6rNrMiYHV6Li7CeGF2mh9YgTj9
        pu9Yu4B+tUr0aCapo+pDwTjzv/SplgeqenzcdRNzpJV0kY2F28CZ1BhRYS00rKo3
        aUjtH7BEBxBpVwGCHXOw6Jb/cOKGH58udHVp/YcBUZpKpiOXRkJKODl7coud7qjp
        IeAWasurnQDY1rv3WCK6aam2tPLlx1gT1FWL3ze9jK0DAgMBAAE=
        -----END PUBLIC KEY-----
        """;
}
