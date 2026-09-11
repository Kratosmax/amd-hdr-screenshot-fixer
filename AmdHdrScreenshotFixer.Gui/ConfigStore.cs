using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO;

namespace AmdHdrScreenshotFixer;

public sealed class ConfigStore
{
    private readonly JsonSerializerOptions options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public ConfigStore(string? dataDirectory = null)
    {
        var useSystemStore = dataDirectory is null;
        DataDirectory = useSystemStore
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AmdHdrScreenshotFixer")
            : Path.GetFullPath(dataDirectory!);
        Directory.CreateDirectory(DataDirectory);
        ConfigPath = Path.Combine(DataDirectory, "config.json");
        if (useSystemStore)
        {
            SeedFile("config.json", "AmdHdrScreenshotFixer.Defaults.config.json");
            SeedFile("calibration.json", "AmdHdrScreenshotFixer.Defaults.calibration.json");
        }
    }

    public string DataDirectory { get; }
    public string ConfigPath { get; }

    public FixerConfig Load()
    {
        if (!File.Exists(ConfigPath)) return new FixerConfig();
        var config = JsonSerializer.Deserialize<FixerConfig>(File.ReadAllText(ConfigPath), options) ?? new FixerConfig();
        config.UpdateNetwork = (config.UpdateNetwork ?? UpdateNetworkSettings.Default).Normalize();
        return config;
    }

    public CalibrationData? LoadCalibration(FixerConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.CalibrationFile)) return null;
        var path = ResolveDataPath(config.CalibrationFile);
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<CalibrationData>(File.ReadAllText(path), options);
    }

    public CalibrationData LoadFactoryCalibration()
    {
        using var source = typeof(ConfigStore).Assembly.GetManifestResourceStream("AmdHdrScreenshotFixer.Defaults.calibration.json")
            ?? throw new InvalidOperationException("Missing embedded default calibration.");
        return JsonSerializer.Deserialize<CalibrationData>(source, options)
            ?? throw new InvalidDataException("默认校准数据无效。");
    }

    public void SaveCalibration(FixerConfig config, CalibrationData calibration)
    {
        var fileName = string.IsNullOrWhiteSpace(config.CalibrationFile) ? "calibration.json" : config.CalibrationFile;
        var path = ResolveDataPath(fileName);
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(calibration, options) + Environment.NewLine);
        File.Move(tempPath, path, true);
    }

    public void SaveAdjustments(double red, double green, double blue, double exposure, double postContrast,
        double postSaturation = 1.0, double postBlackPoint = 0.0, double postWhitePoint = 1.0)
    {
        var root = File.Exists(ConfigPath)
            ? JsonNode.Parse(File.ReadAllText(ConfigPath))?.AsObject() ?? new JsonObject()
            : new JsonObject();
        root["redGain"] = Math.Round(red, 3);
        root["greenGain"] = Math.Round(green, 3);
        root["blueGain"] = Math.Round(blue, 3);
        root["exposure"] = Math.Round(exposure, 3);
        root["postContrast"] = Math.Round(postContrast, 3);
        root["postSaturation"] = Math.Round(postSaturation, 3);
        root["postBlackPoint"] = Math.Round(postBlackPoint, 3);
        root["postWhitePoint"] = Math.Round(postWhitePoint, 3);
        File.WriteAllText(ConfigPath, root.ToJsonString(options) + Environment.NewLine);
    }

    public void SaveWatchPath(string watchPath)
    {
        var root = File.Exists(ConfigPath)
            ? JsonNode.Parse(File.ReadAllText(ConfigPath))?.AsObject() ?? new JsonObject()
            : new JsonObject();
        root["watchPath"] = Path.GetFullPath(watchPath);
        File.WriteAllText(ConfigPath, root.ToJsonString(options) + Environment.NewLine);
    }

    public void SaveSkippedVersion(string? version)
    {
        var root = File.Exists(ConfigPath)
            ? JsonNode.Parse(File.ReadAllText(ConfigPath))?.AsObject() ?? new JsonObject()
            : new JsonObject();
        root["skippedVersion"] = version;
        File.WriteAllText(ConfigPath, root.ToJsonString(options) + Environment.NewLine);
    }

    public void SaveApplicationSettings(bool startWithWindows, bool autoUpdateEnabled, UpdateNetworkSettings network)
    {
        var normalized = network.Normalize();
        var root = File.Exists(ConfigPath)
            ? JsonNode.Parse(File.ReadAllText(ConfigPath))?.AsObject() ?? new JsonObject()
            : new JsonObject();
        root["startWithWindows"] = startWithWindows;
        root["autoUpdateEnabled"] = autoUpdateEnabled;
        root["updateNetwork"] = JsonSerializer.SerializeToNode(normalized, options);
        var temporary = ConfigPath + ".tmp";
        File.WriteAllText(temporary, root.ToJsonString(options) + Environment.NewLine);
        File.Move(temporary, ConfigPath, true);
    }

    private void SeedFile(string fileName, string resourceName)
    {
        var destination = Path.Combine(DataDirectory, fileName);
        if (File.Exists(destination)) return;

        foreach (var candidate in new[]
        {
            Path.Combine(Environment.CurrentDirectory, fileName),
            Path.Combine(AppContext.BaseDirectory, fileName)
        }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(candidate) && !Path.GetFullPath(candidate).Equals(destination, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(candidate, destination);
                return;
            }
        }

        using var source = typeof(ConfigStore).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded default: {resourceName}");
        using var output = File.Create(destination);
        source.CopyTo(output);
    }

    private string ResolveDataPath(string fileName)
    {
        var path = Path.GetFullPath(Path.Combine(DataDirectory, fileName));
        var root = Path.GetFullPath(DataDirectory) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("配置文件路径必须位于应用数据目录内。");
        return path;
    }
}
