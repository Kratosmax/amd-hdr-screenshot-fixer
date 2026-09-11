using System.Windows;
using System.IO;
using System.Linq;

namespace AmdHdrScreenshotFixer;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AmdHdrScreenshotFixer");
            Directory.CreateDirectory(logDirectory);
            File.WriteAllText(Path.Combine(logDirectory, "gui-error.log"), args.Exception.ToString());
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var initialPath = e.Args.FirstOrDefault(path =>
            File.Exists(path) && Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase));
        string? screenshotPath = null;
        var screenshotIndex = Array.IndexOf(e.Args, "--screenshot");
        if (screenshotIndex >= 0 && screenshotIndex + 1 < e.Args.Length)
            screenshotPath = e.Args[screenshotIndex + 1];
        var qaExportPath = GetOption(e.Args, "--qa-export");
        var settingsScreenshotPath = GetOption(e.Args, "--qa-settings-screenshot");
        var calibrationScreenshotPath = GetOption(e.Args, "--qa-calibration-screenshot");
        var updatedFrom = GetOption(e.Args, "--updated-from");
        var window = new MainWindow(initialPath, screenshotPath, qaExportPath, updatedFrom,
            settingsScreenshotPath, calibrationScreenshotPath);
        if (int.TryParse(GetOption(e.Args, "--width"), out var width)) window.Width = Math.Max(window.MinWidth, width);
        if (int.TryParse(GetOption(e.Args, "--height"), out var height)) window.Height = Math.Max(window.MinHeight, height);
        MainWindow = window;
        window.Show();
    }

    private static string? GetOption(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
