using System.Diagnostics;
using System.Windows;
using ScContractTracker.App.Services;
using ScContractTracker.App.ViewModels;

namespace ScContractTracker.App.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ScContractTracker.App.Services.Native.UseDarkTitleBar(this);
        VersionText.Text = Loc.Instance.Format("About.Version", UpdateService.CurrentVersion);
        RepoLink.Content = UpdateService.RepositoryUrl;
        DataLink.Content = AppPaths.DataDir;
    }

    private void Repo_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(UpdateService.RepositoryUrl) { UseShellExecute = true }); }
        catch (Exception ex) { AppPaths.LogError(ex); }
    }

    private void Data_Click(object sender, RoutedEventArgs e) => MainViewModel.OpenFolder(AppPaths.DataDir);
}
