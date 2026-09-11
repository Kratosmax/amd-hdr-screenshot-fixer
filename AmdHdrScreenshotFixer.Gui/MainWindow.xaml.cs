using Microsoft.Win32;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows.Threading;
using System.IO;

namespace AmdHdrScreenshotFixer;

public partial class MainWindow : Window
{
    private readonly ConfigStore configStore = new();
    private readonly ScreenshotWatcher screenshotWatcher;
    private readonly DispatcherTimer previewTimer;
    private readonly UpdateClient updateClient = new();
    private FixerConfig config = new();
    private CalibrationData? calibration;
    private PixelFrame? correctedBase;
    private BitmapSource? originalPreview;
    private string? inputPath;
    private string? pendingWatchedPreviewPath;
    private long imageLoadVersion;
    private bool initialized;
    private bool updateCheckRunning;

    public MainWindow(string? initialPath = null, string? screenshotPath = null, string? qaExportPath = null,
        string? updatedFrom = null, string? settingsScreenshotPath = null,
        string? calibrationScreenshotPath = null)
    {
        InitializeComponent();
        screenshotWatcher = new ScreenshotWatcher(configStore);
        screenshotWatcher.StatusChanged += Watcher_StatusChanged;
        screenshotWatcher.ProcessingCompleted += Watcher_ProcessingCompleted;
        previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(45) };
        previewTimer.Tick += (_, _) => { previewTimer.Stop(); RefreshPreview(); };
        LoadSettings();
        initialized = true;
        UpdateGainLabels();
        Loaded += async (_, _) =>
        {
            if (updatedFrom is not null) OperationStatus.Text = $"已更新到 {UpdateClient.CurrentVersion.ToString(3)}";
            if (screenshotPath is null && qaExportPath is null && settingsScreenshotPath is null &&
                calibrationScreenshotPath is null && config.AutoUpdateEnabled)
                await CheckForUpdatesAsync(false);
        };
        if (settingsScreenshotPath is not null || calibrationScreenshotPath is not null)
            Loaded += async (_, _) => await CaptureAuxiliaryWindowsAsync(settingsScreenshotPath, calibrationScreenshotPath);
        if (initialPath is not null) Loaded += async (_, _) =>
        {
            await LoadImageAsync(initialPath);
            if (qaExportPath is not null)
            {
                var succeeded = await ExportImageAsync(qaExportPath);
                Application.Current.Shutdown(succeeded ? 0 : 1);
                return;
            }
            if (screenshotPath is not null)
            {
                try
                {
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                    WindowCapture.Save(this, screenshotPath);
                    Application.Current.Shutdown();
                }
                catch (Exception ex)
                {
                    File.WriteAllText(Path.GetFullPath(screenshotPath) + ".error.txt", ex.ToString());
                    Application.Current.Shutdown(1);
                }
            }
        };
        StateChanged += MainWindow_StateChanged;
        Closed += (_, _) => screenshotWatcher.Dispose();
    }

    private void LoadSettings()
    {
        try
        {
            config = configStore.Load();
            calibration = configStore.LoadCalibration(config);
            ApplySavedDefaults();
            if (!string.IsNullOrWhiteSpace(config.WatchPath)) WatchFolderButton.ToolTip = config.WatchPath;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "读取配置失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task LoadImageAsync(string path, bool applySavedDefaults = false, string? successStatus = null,
        bool showErrors = true)
    {
        var loadVersion = Interlocked.Increment(ref imageLoadVersion);
        try
        {
            OperationStatus.Text = "正在载入...";
            Mouse.OverrideCursor = Cursors.Wait;
            var baseFrame = await Task.Run(() => ImageProcessor.LoadBase(path, config, calibration, 1600));
            var originalFrame = await Task.Run(() => ImageProcessor.LoadBase(path, new FixerConfig(), null, 1600));
            if (loadVersion != Volatile.Read(ref imageLoadVersion)) return;

            correctedBase = baseFrame;
            originalPreview = ImageProcessor.Render(originalFrame, 1, 1, 1);
            inputPath = path;
            if (applySavedDefaults) ApplySavedDefaults();
            FileStatus.Text = path;
            EmptyState.Visibility = Visibility.Collapsed;
            ExportButton.IsEnabled = true;
            RefreshPreview();
            OperationStatus.Text = successStatus ?? $"{baseFrame.Width} x {baseFrame.Height} 预览";
        }
        catch (Exception ex)
        {
            if (loadVersion != Volatile.Read(ref imageLoadVersion)) return;
            if (showErrors)
                MessageBox.Show(this, ex.Message, "无法打开图片", MessageBoxButton.OK, MessageBoxImage.Error);
            OperationStatus.Text = "载入失败";
        }
        finally
        {
            if (loadVersion == Volatile.Read(ref imageLoadVersion)) Mouse.OverrideCursor = null;
        }
    }

    private void GainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => AdjustmentSlider_ValueChanged(sender, e);

    private void AdjustmentSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!initialized) return;
        UpdateGainLabels();
        previewTimer.Stop();
        previewTimer.Start();
    }

    private void UpdateGainLabels()
    {
        RedValue.Text = $"{RedSlider.Value:P0}";
        GreenValue.Text = $"{GreenSlider.Value:P0}";
        BlueValue.Text = $"{BlueSlider.Value:P0}";
        ExposureValue.Text = $"{ExposureSlider.Value:+0.00;-0.00;0.00} EV";
        ContrastValue.Text = $"{ContrastSlider.Value:P0}";
        SaturationValue.Text = $"{SaturationSlider.Value:P0}";
        BlackPointValue.Text = $"{BlackPointSlider.Value:P0}";
        WhitePointValue.Text = $"{WhitePointSlider.Value:P0}";
    }

    private void RefreshPreview()
    {
        if (OriginalMode.IsChecked == true)
        {
            PreviewImage.Source = originalPreview;
            return;
        }
        if (correctedBase is not null)
            PreviewImage.Source = ImageProcessor.Render(correctedBase, RedSlider.Value, GreenSlider.Value, BlueSlider.Value,
                ExposureSlider.Value, ContrastSlider.Value, SaturationSlider.Value,
                BlackPointSlider.Value, WhitePointSlider.Value);
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "PNG 图片 (*.png)|*.png", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) await LoadImageAsync(dialog.FileName);
    }

    private async Task<string> CheckForUpdatesAsync(bool userInitiated,
        UpdateNetworkSettings? networkSettings = null, Window? owner = null)
    {
        if (updateCheckRunning) return "已有更新检查正在进行";
        updateCheckRunning = true;
        if (userInitiated) OperationStatus.Text = "正在检查更新...";
        try
        {
            var network = networkSettings ?? config.UpdateNetwork;
            var update = await updateClient.CheckAsync(network);
            if (update is null)
            {
                if (userInitiated) OperationStatus.Text = $"已是最新版本 {UpdateClient.CurrentVersion.ToString(3)}";
                return OperationStatus.Text;
            }
            if (!userInitiated && config.SkippedVersion == update.Version.ToString(3)) return "已跳过此版本";
            var dialog = new UpdateWindow(update, updateClient.CanInstallInPlace, async progress =>
            {
                var prepared = await updateClient.DownloadAsync(update, progress, network);
                UpdateClient.LaunchUpdater(prepared);
                Application.Current.Shutdown();
            }, () =>
            {
                config.SkippedVersion = update.Version.ToString(3);
                configStore.SaveSkippedVersion(config.SkippedVersion);
            }) { Owner = owner ?? this };
            dialog.ShowDialog();
            return $"发现版本 {update.Version.ToString(3)}";
        }
        catch (Exception ex) when (!userInitiated)
        {
            System.Diagnostics.Debug.WriteLine($"Background update check failed: {ex.Message}");
            return "后台检查失败";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "检查更新失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            OperationStatus.Text = "检查更新失败";
            return $"检查失败：{ex.Message}";
        }
        finally
        {
            updateCheckRunning = false;
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsWindow? window = null;
        window = new SettingsWindow(config, configStore,
            network => CheckForUpdatesAsync(true, network, window)) { Owner = this };
        window.ShowDialog();
    }

    private async Task CaptureAuxiliaryWindowsAsync(string? settingsPath, string? calibrationPath)
    {
        try
        {
            if (settingsPath is not null)
            {
                var settings = new SettingsWindow(config, configStore, _ => Task.FromResult("QA")) { Owner = this };
                settings.Show();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                WindowCapture.Save(settings, settingsPath);
                settings.Close();
            }
            if (calibrationPath is not null)
            {
                var calibrationWindow = new CalibrationWindow(configStore, config) { Owner = this };
                calibrationWindow.Show();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                WindowCapture.Save(calibrationWindow, calibrationPath);
                calibrationWindow.Close();
            }
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            var failurePath = settingsPath ?? calibrationPath;
            if (failurePath is not null) File.WriteAllText(Path.GetFullPath(failurePath) + ".error.txt", ex.ToString());
            Application.Current.Shutdown(1);
        }
    }

    private async void CalibrationButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new CalibrationWindow(configStore, config) { Owner = this };
        if (window.ShowDialog() != true || !window.Applied) return;
        calibration = configStore.LoadCalibration(config);
        if (inputPath is not null) await LoadImageAsync(inputPath);
        OperationStatus.Text = "已应用设备校准，可继续微调并保存默认值";
    }

    private void WatchToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (!initialized) return;
        if (string.IsNullOrWhiteSpace(config.WatchPath) || !Directory.Exists(config.WatchPath))
        {
            if (!ChooseWatchFolder())
            {
                WatchToggle.IsChecked = false;
                return;
            }
        }

        try
        {
            screenshotWatcher.Start(config.WatchPath!);
            WatchStatus.Text = "监听中";
            WatchStatus.Foreground = (Brush)FindResource("AccentBrush");
            WatchStatus.ToolTip = config.WatchPath;
        }
        catch (Exception ex)
        {
            WatchToggle.IsChecked = false;
            MessageBox.Show(this, ex.Message, "无法启动监听", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void WatchToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        if (!initialized) return;
        screenshotWatcher.Stop();
        WatchStatus.Text = "监听关闭";
        WatchStatus.Foreground = (Brush)FindResource("MutedBrush");
        WatchStatus.ToolTip = config.WatchPath;
    }

    private void WatchFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var wasRunning = screenshotWatcher.IsRunning;
        if (!ChooseWatchFolder()) return;
        if (wasRunning)
        {
            screenshotWatcher.Start(config.WatchPath!);
            WatchStatus.ToolTip = config.WatchPath;
            OperationStatus.Text = "已切换监听目录";
        }
    }

    private bool ChooseWatchFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择截图监听目录",
            InitialDirectory = Directory.Exists(config.WatchPath) ? config.WatchPath : null
        };
        if (dialog.ShowDialog(this) != true) return false;
        configStore.SaveWatchPath(dialog.FolderName);
        config.WatchPath = dialog.FolderName;
        WatchFolderButton.ToolTip = dialog.FolderName;
        OperationStatus.Text = "已保存监听目录";
        return true;
    }

    private void Watcher_StatusChanged(string message, bool isError)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            OperationStatus.Text = message;
            OperationStatus.Foreground = isError ? System.Windows.Media.Brushes.Firebrick : (Brush)FindResource("MutedBrush");
        });
    }

    private void Watcher_ProcessingCompleted(WatcherProcessingResult result)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            pendingWatchedPreviewPath = result.SourcePath;
            if (WindowState != WindowState.Minimized) ShowLatestWatchedPreview();
        });
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized) ShowLatestWatchedPreview();
    }

    private void ShowLatestWatchedPreview()
    {
        var path = pendingWatchedPreviewPath;
        if (path is null) return;
        pendingWatchedPreviewPath = null;
        _ = LoadImageAsync(path, applySavedDefaults: true,
            successStatus: $"已处理并预览：{Path.GetFileName(path)}", showErrors: false);
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (inputPath is null) return;
        var directory = Path.GetDirectoryName(inputPath)!;
        var name = Path.GetFileNameWithoutExtension(inputPath) + config.Suffix + ".png";
        var dialog = new SaveFileDialog { Filter = "PNG 图片 (*.png)|*.png", InitialDirectory = directory, FileName = name, OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;

        await ExportImageAsync(dialog.FileName);
    }

    private async Task<bool> ExportImageAsync(string destinationPath)
    {
        if (inputPath is null) return false;
        try
        {
            var sourcePath = inputPath;
            var red = RedSlider.Value;
            var green = GreenSlider.Value;
            var blue = BlueSlider.Value;
            var exposure = ExposureSlider.Value;
            var postContrast = ContrastSlider.Value;
            var postSaturation = SaturationSlider.Value;
            var postBlackPoint = BlackPointSlider.Value;
            var postWhitePoint = WhitePointSlider.Value;
            ExportButton.IsEnabled = false;
            OperationStatus.Text = "正在导出...";
            Mouse.OverrideCursor = Cursors.Wait;
            await Task.Run(() => ImageProcessor.Export(sourcePath, destinationPath, config, calibration,
                red, green, blue, exposure, postContrast, postSaturation, postBlackPoint, postWhitePoint));
            OperationStatus.Text = "导出完成";
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Error);
            OperationStatus.Text = "导出失败";
            return false;
        }
        finally
        {
            Mouse.OverrideCursor = null;
            ExportButton.IsEnabled = true;
        }
    }

    private void SaveConfigButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            configStore.SaveAdjustments(RedSlider.Value, GreenSlider.Value, BlueSlider.Value,
                ExposureSlider.Value, ContrastSlider.Value, SaturationSlider.Value,
                BlackPointSlider.Value, WhitePointSlider.Value);
            config.RedGain = RedSlider.Value;
            config.GreenGain = GreenSlider.Value;
            config.BlueGain = BlueSlider.Value;
            config.Exposure = ExposureSlider.Value;
            config.PostContrast = ContrastSlider.Value;
            config.PostSaturation = SaturationSlider.Value;
            config.PostBlackPoint = BlackPointSlider.Value;
            config.PostWhitePoint = WhitePointSlider.Value;
            OperationStatus.Text = "已保存默认调整参数";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        ApplySavedDefaults();
        OperationStatus.Text = "已恢复保存的默认值";
    }

    private void ApplySavedDefaults()
    {
        RedSlider.Value = config.RedGain;
        GreenSlider.Value = config.GreenGain;
        BlueSlider.Value = config.BlueGain;
        ExposureSlider.Value = config.Exposure;
        ContrastSlider.Value = config.PostContrast;
        SaturationSlider.Value = config.PostSaturation;
        BlackPointSlider.Value = config.PostBlackPoint;
        WhitePointSlider.Value = config.PostWhitePoint;
    }

    private void PreviewMode_Checked(object sender, RoutedEventArgs e)
    {
        if (initialized) RefreshPreview();
    }

    private void Preview_DragEnter(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = HasSinglePng(e.Data) ? Visibility.Visible : Visibility.Collapsed;
        Preview_DragOver(sender, e);
    }

    private void Preview_DragLeave(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    private void Preview_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasSinglePng(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Preview_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (!HasSinglePng(e.Data)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        await LoadImageAsync(files[0]);
    }

    private static bool HasSinglePng(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop)) return false;
        var files = data.GetData(DataFormats.FileDrop) as string[];
        return files?.Length == 1 && Path.GetExtension(files[0]).Equals(".png", StringComparison.OrdinalIgnoreCase);
    }

}
