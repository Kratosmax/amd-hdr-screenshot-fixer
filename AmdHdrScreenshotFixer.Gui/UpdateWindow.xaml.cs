using AmdHdrScreenshotFixer.Core;
using System.Diagnostics;
using System.Windows;

namespace AmdHdrScreenshotFixer;

public partial class UpdateWindow : Window
{
    private readonly UpdateInfo update;
    private readonly Func<IProgress<int>, Task> install;
    private readonly Action skip;
    private readonly bool canInstall;

    public UpdateWindow(UpdateInfo update, bool canInstall, Func<IProgress<int>, Task> install, Action skip)
    {
        InitializeComponent();
        this.update = update;
        this.install = install;
        this.skip = skip;
        this.canInstall = canInstall;
        VersionText.Text = $"{UpdateClient.CurrentVersion.ToString(3)}  ->  {update.Version.ToString(3)}  |  {update.Size / 1024d / 1024d:0.0} MB";
        ReleaseNotesText.Text = string.IsNullOrWhiteSpace(update.ReleaseNotes) ? "此版本没有附加更新说明。" : update.ReleaseNotes;
        if (!canInstall)
        {
            InstallButton.Content = "打开下载页";
            StatusText.Text = "当前是开发构建或旧式单文件目录，不能就地替换。";
        }
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (!canInstall)
        {
            Process.Start(new ProcessStartInfo(UpdateClient.ReleasePageUri.ToString()) { UseShellExecute = true });
            return;
        }
        SetBusy(true);
        DownloadProgress.Visibility = Visibility.Visible;
        StatusText.Text = "正在下载和验证...";
        try
        {
            var progress = new Progress<int>(value =>
            {
                DownloadProgress.Value = value;
                StatusText.Text = value < 100 ? $"正在下载和验证... {value}%" : "验证完成，正在重启...";
            });
            await install(progress);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"更新失败：{ex.Message}";
            SetBusy(false);
        }
    }

    private void SkipButton_Click(object sender, RoutedEventArgs e) { skip(); Close(); }
    private void LaterButton_Click(object sender, RoutedEventArgs e) => Close();

    private void SetBusy(bool busy)
    {
        InstallButton.IsEnabled = !busy;
        LaterButton.IsEnabled = !busy;
        SkipButton.IsEnabled = !busy;
    }
}
