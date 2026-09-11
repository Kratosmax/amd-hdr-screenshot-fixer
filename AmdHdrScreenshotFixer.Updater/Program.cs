using System.Diagnostics;
using AmdHdrScreenshotFixer.Core;

return await UpdaterProgram.RunAsync(args);

internal static class UpdaterProgram
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AmdHdrScreenshotFixer", "update.log");

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = Parse(args);
            var package = Require(options, "package");
            var manifest = Require(options, "manifest");
            var target = Require(options, "target");
            var processId = int.Parse(Require(options, "pid"), System.Globalization.CultureInfo.InvariantCulture);
            var processStartTicks = long.Parse(Require(options, "process-start-ticks"),
                System.Globalization.CultureInfo.InvariantCulture);
            var restart = !options.TryGetValue("restart", out var restartValue) ||
                          !restartValue.Equals("false", StringComparison.OrdinalIgnoreCase);
            WriteLog("等待主程序退出。");
            await WaitForExitAsync(processId, processStartTicks, TimeSpan.FromSeconds(60));
            var channel = UpdateInstaller.GetInstalledChannel(target);
            var update = UpdateManifestCodec.ParseAndVerify(await File.ReadAllTextAsync(manifest), channel);
            await UpdateInstaller.InstallAsync(package, target, update);
            if (restart)
            {
                var executable = Path.Combine(UpdateInstaller.EnsureInstallRoot(target), "AmdHdrScreenshotFixer.exe");
                var start = new ProcessStartInfo(executable) { UseShellExecute = true, WorkingDirectory = target };
                start.ArgumentList.Add("--updated-from");
                start.ArgumentList.Add(update.Version.ToString(3));
                Process.Start(start);
            }
            WriteLog($"已安装并启动 {update.Version.ToString(3)}。");
            return 0;
        }
        catch (Exception ex)
        {
            WriteLog($"更新失败：{ex}");
            return 1;
        }
    }

    private static async Task WaitForExitAsync(int processId, long expectedStartTicks, TimeSpan timeout)
    {
        Process process;
        try { process = Process.GetProcessById(processId); }
        catch (ArgumentException) { return; }
        using (process)
        using (var cancellation = new CancellationTokenSource(timeout))
        {
            if (process.StartTime.ToUniversalTime().Ticks != expectedStartTicks)
                throw new InvalidOperationException("主进程身份已变化，拒绝继续更新。");
            try { await process.WaitForExitAsync(cancellation.Token); }
            catch (OperationCanceledException ex) { throw new TimeoutException("主程序未在允许时间内退出。", ex); }
        }
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("更新器参数格式无效。");
            result.Add(args[i][2..], args[i + 1]);
        }
        return result;
    }

    private static string Require(IReadOnlyDictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException($"缺少更新器参数 --{name}。");

    private static void WriteLog(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 256 * 1024)
                File.Move(LogPath, LogPath + ".old", true);
            File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}");
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
