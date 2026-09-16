using System.Diagnostics;
using System.IO;
using System.Windows.Documents;
using System.Windows.Navigation;
using WpfOpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace CycleArc.UI;

public partial class AboutWindow : Window
{
    private readonly Action<string>? _installVersion;
    private readonly Action? _showUpdates;

    public AboutWindow(string version, string dataSource, Action<string>? installVersion = null, Action? showUpdates = null)
    {
        _installVersion = installVersion;
        _showUpdates = showUpdates;
        InitializeComponent();
        Title = UiText.AboutTitle;
        SubtitleText.Text = UiText.T("Codex and Claude account usage monitor", "Codex·Claude 계정 사용량 모니터");
        GitHubLink.Inlines.Clear();
        GitHubLink.Inlines.Add(new Run(UiText.GitHubRepository));
        VersionText.Text = UiText.VersionPrefix + version;
        SourceText.Text = UiText.DataSourceStatus + dataSource;
        InstallVersionButton.Content = UiText.T("Install another version…", "다른 버전 설치…");
        InstallVersionButton.Visibility = installVersion is null ? Visibility.Collapsed : Visibility.Visible;
        InstallVersionButton.IsEnabled = installVersion is not null;
        UpdatesButton.Content = UiText.T("Check for updates…", "업데이트 확인…");
        UpdatesButton.Visibility = showUpdates is null ? Visibility.Collapsed : Visibility.Visible;
        CloseButton.Content = UiText.Close;
    }

    private void OnUpdates(object sender, RoutedEventArgs e) => _showUpdates?.Invoke();

    private void OnLink(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void OnInstallVersion(object sender, RoutedEventArgs e)
    {
        if (_installVersion is null) return;

        var dialog = new WpfOpenFileDialog
        {
            Title = UiText.T("Select CycleArc.exe", "CycleArc.exe 선택"),
            Filter = "CycleArc.exe|CycleArc.exe",
            CheckFileExists = true,
            CheckPathExists = true,
            FileName = "CycleArc.exe",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
        {
            _installVersion(Path.GetFullPath(dialog.FileName));
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
