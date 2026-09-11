using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace AmdHdrScreenshotFixer;

public partial class SettingsWindow : Window
{
    private readonly FixerConfig config;
    private readonly ConfigStore configStore;
    private readonly Func<UpdateNetworkSettings, Task<string>> checkForUpdates;
    private readonly ObservableCollection<ProxyEditorRow> proxies = [];

    public SettingsWindow(FixerConfig config, ConfigStore configStore,
        Func<UpdateNetworkSettings, Task<string>> checkForUpdates)
    {
        InitializeComponent();
        this.config = config;
        this.configStore = configStore;
        this.checkForUpdates = checkForUpdates;
        StartWithWindowsBox.IsChecked = StartupRegistrationService.IsEnabled();
        AutoUpdateBox.IsChecked = config.AutoUpdateEnabled;
        VersionText.Text = $"当前版本 {UpdateClient.CurrentVersion.ToString(3)}";
        var network = (config.UpdateNetwork ?? UpdateNetworkSettings.Default).Normalize();
        foreach (var proxy in network.GithubProxies ?? []) proxies.Add(new ProxyEditorRow(proxy));
        ProxyGrid.ItemsSource = proxies;
        HttpProxyBox.Text = network.HttpProxy ?? string.Empty;
    }

    private void AddProxy_Click(object sender, RoutedEventArgs e)
    {
        var row = new ProxyEditorRow(new GithubProxySetting("https://", 5));
        proxies.Add(row); ProxyGrid.SelectedItem = row; ProxyGrid.ScrollIntoView(row);
    }

    private void RemoveProxy_Click(object sender, RoutedEventArgs e)
    {
        if (ProxyGrid.SelectedItem is not ProxyEditorRow row) return;
        if (row.IsDirect) { ErrorText.Text = "GitHub 直连不可删除，可将优先级设为 0。"; return; }
        proxies.Remove(row); ErrorText.Text = string.Empty;
    }

    private void ProxyGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        if (e.Row.Item is ProxyEditorRow { IsDirect: true } && e.Column.DisplayIndex == 0) e.Cancel = true;
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildNetworkSettings(out var network)) return;
        CheckUpdateButton.IsEnabled = false; UpdateStatusText.Text = "正在检查...";
        try { UpdateStatusText.Text = await checkForUpdates(network); }
        catch (Exception ex) { UpdateStatusText.Text = $"检查失败：{ex.Message}"; }
        finally { CheckUpdateButton.IsEnabled = true; }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildNetworkSettings(out var network)) return;
        var requestedStartup = StartWithWindowsBox.IsChecked == true;
        var previousStartup = StartupRegistrationService.IsEnabled();
        try
        {
            StartupRegistrationService.SetEnabled(requestedStartup);
            configStore.SaveApplicationSettings(requestedStartup, AutoUpdateBox.IsChecked == true, network);
            config.StartWithWindows = requestedStartup;
            config.AutoUpdateEnabled = AutoUpdateBox.IsChecked == true;
            config.UpdateNetwork = network;
            ErrorText.Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush");
            ErrorText.Text = "设置已保存";
        }
        catch (Exception ex)
        {
            try { StartupRegistrationService.SetEnabled(previousStartup); } catch { }
            ErrorText.Foreground = System.Windows.Media.Brushes.Firebrick;
            ErrorText.Text = $"保存失败：{ex.Message}";
        }
    }

    private bool TryBuildNetworkSettings(out UpdateNetworkSettings settings)
    {
        ErrorText.Foreground = System.Windows.Media.Brushes.Firebrick;
        ProxyGrid.CommitEdit(DataGridEditingUnit.Cell, true); ProxyGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var values = new List<GithubProxySetting>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in proxies)
        {
            if (row.IsDirect) { values.Add(new GithubProxySetting(string.Empty, row.Priority, true)); continue; }
            if (!UpdateNetworkSettings.TryNormalizeGithubProxy(row.Address, out var baseUrl))
            {
                ErrorText.Text = $"GitHub 前缀线路无效：{row.Address}"; settings = UpdateNetworkSettings.Default; return false;
            }
            if (!seen.Add(baseUrl)) { ErrorText.Text = $"GitHub 前缀线路重复：{baseUrl}"; settings = UpdateNetworkSettings.Default; return false; }
            values.Add(new GithubProxySetting(baseUrl, row.Priority));
        }
        if (!UpdateNetworkSettings.TryNormalizeHttpProxy(HttpProxyBox.Text, out var httpProxy))
        {
            ErrorText.Text = "HTTP 网络代理应为 http://主机:端口，且不能包含账号、路径或查询参数。";
            settings = UpdateNetworkSettings.Default; return false;
        }
        if (values.All(item => item.Priority == 0))
        {
            ErrorText.Text = "至少启用一条 GitHub 访问线路。"; settings = UpdateNetworkSettings.Default; return false;
        }
        ErrorText.Text = string.Empty; settings = new UpdateNetworkSettings(values, httpProxy).Normalize(); return true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

internal sealed class ProxyEditorRow
{
    public ProxyEditorRow(GithubProxySetting setting)
    {
        IsDirect = setting.IsDirect;
        Address = setting.IsDirect ? "GitHub 直连（不拼接前缀）" : setting.BaseUrl;
        Priority = setting.Priority;
    }
    public string Address { get; set; }
    public int Priority { get; set; }
    public bool IsDirect { get; }
}
