using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;

namespace AmdHdrScreenshotFixer;

public partial class CalibrationWindow : Window
{
    private readonly ConfigStore configStore;
    private readonly FixerConfig config;
    private readonly ObservableCollection<PairRow> pairs = [];
    private CancellationTokenSource? cancellation;
    private CalibrationResult? result;

    public CalibrationWindow(ConfigStore configStore, FixerConfig config)
    {
        InitializeComponent();
        this.configStore = configStore;
        this.config = config;
        PairGrid.ItemsSource = pairs;
    }

    public bool Applied { get; private set; }

    private void AddPair_Click(object sender, RoutedEventArgs e)
    {
        var amdDialog = new OpenFileDialog { Title = "选择 AMD HDR 异常截图", Filter = "PNG 图片 (*.png)|*.png", CheckFileExists = true };
        if (amdDialog.ShowDialog(this) != true) return;
        var referenceDialog = new OpenFileDialog { Title = "选择对应的正常参考截图", Filter = "PNG 图片 (*.png)|*.png", CheckFileExists = true };
        if (referenceDialog.ShowDialog(this) != true) return;
        var row = new PairRow(amdDialog.FileName, referenceDialog.FileName);
        pairs.Add(row); PairGrid.SelectedItem = row; PairGrid.ScrollIntoView(row);
        StartButton.IsEnabled = pairs.Count >= 3;
        ResultText.Text = $"已添加 {pairs.Count} 组对比图";
    }

    private void RemovePair_Click(object sender, RoutedEventArgs e)
    {
        if (PairGrid.SelectedItem is PairRow row) pairs.Remove(row);
        StartButton.IsEnabled = pairs.Count >= 3;
    }

    private async void StartCalibration_Click(object sender, RoutedEventArgs e)
    {
        if (pairs.Count < 3) return;
        cancellation = new CancellationTokenSource();
        StartButton.IsEnabled = false; ApplyButton.IsEnabled = false; result = null;
        var progress = new Progress<CalibrationProgress>(value =>
        {
            CalibrationProgressBar.Value = value.Percent; ResultText.Text = value.Message;
        });
        try
        {
            var input = pairs.Select(item => new CalibrationPair(item.AmdPath, item.ReferencePath)).ToList();
            var baseline = configStore.LoadFactoryCalibration();
            result = await Task.Run(() => CalibrationEngine.Fit(input, baseline, progress, cancellation.Token));
            ScoreText.Text = result.Quality.Score.ToString("F1");
            MeanText.Text = result.Quality.MeanDeltaE.ToString("F2");
            P95Text.Text = result.Quality.P95DeltaE.ToString("F2");
            ResultText.Text = result.Quality.MeetsTarget
                ? $"达到目标，已生成 {result.GridSize}³ LUT。应用后可在主窗口继续微调。"
                : $"未达到目标，已保留验证误差最低的 {result.GridSize}³ LUT。建议增加更多不同场景。";
            await LoadResultPreviewAsync(input[^1], result.Calibration);
            ApplyButton.IsEnabled = true;
        }
        catch (OperationCanceledException) { ResultText.Text = "校准已取消"; }
        catch (Exception ex)
        {
            ResultText.Text = $"校准失败：{ex.Message}";
            MessageBox.Show(this, ex.Message, "校准失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            cancellation.Dispose(); cancellation = null; StartButton.IsEnabled = pairs.Count >= 3;
        }
    }

    private async Task LoadResultPreviewAsync(CalibrationPair pair, CalibrationData calibration)
    {
        var corrected = await Task.Run(() => ImageProcessor.LoadBase(pair.AmdPath, new FixerConfig(), calibration, 900));
        var reference = await Task.Run(() => ImageProcessor.LoadBase(pair.ReferencePath, new FixerConfig(), null, 900));
        CorrectedPreview.Source = ImageProcessor.Render(corrected, 1, 1, 1);
        ReferencePreview.Source = ImageProcessor.Render(reference, 1, 1, 1);
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (result is null) return;
        try
        {
            configStore.SaveCalibration(config, result.Calibration);
            Applied = true; DialogResult = true;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "保存校准失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void RestoreFactory_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "恢复内置模型会替换当前设备校准，是否继续？", "恢复内置模型",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            configStore.SaveCalibration(config, configStore.LoadFactoryCalibration());
            Applied = true; DialogResult = true;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "恢复失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        cancellation?.Cancel();
        base.OnClosing(e);
    }
}

internal sealed record PairRow(string AmdPath, string ReferencePath)
{
    public string AmdName => Path.GetFileName(AmdPath);
    public string ReferenceName => Path.GetFileName(ReferencePath);
}
