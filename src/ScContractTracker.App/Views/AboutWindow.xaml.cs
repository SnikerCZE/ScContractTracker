using System.Diagnostics;
using System.Windows;
using ScContractTracker.App.Services;
using ScContractTracker.App.ViewModels;

namespace ScContractTracker.App.Views;

public partial class AboutWindow : Window
{
    public const string SupportUrl = "https://ko-fi.com/snikercze";

    public AboutWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ScContractTracker.App.Services.Native.UseDarkTitleBar(this);
        VersionText.Text = Loc.Instance.Format("About.Version", UpdateService.CurrentVersion);
        RepoLink.Content = UpdateService.RepositoryUrl;
        DataLink.Content = AppPaths.DataDir;
        SupportLink.Content = SupportUrl;
    }

    private void Repo_Click(object sender, RoutedEventArgs e) => OpenUrl(UpdateService.RepositoryUrl);

    private void Support_Click(object sender, RoutedEventArgs e) => OpenUrl(SupportUrl);

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { AppPaths.LogError(ex); }
    }

    private void Data_Click(object sender, RoutedEventArgs e) => MainViewModel.OpenFolder(AppPaths.DataDir);
}
