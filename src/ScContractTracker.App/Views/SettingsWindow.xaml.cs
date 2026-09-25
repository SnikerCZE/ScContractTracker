using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ScContractTracker.App.Services;
using ScContractTracker.App.ViewModels;
using ScContractTracker.Core.Settings;

namespace ScContractTracker.App.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow(AppSettings settings,
        IReadOnlyList<(string Code, string Name)> appLanguages,
        IReadOnlyList<LanguageOption> gameLanguages)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ScContractTracker.App.Services.Native.UseDarkTitleBar(this);
        _settings = settings;

        GamePathBox.Text = settings.GamePath;
        AppLanguageBox.ItemsSource = appLanguages.Select(l => new LanguageOption(l.Code, l.Name)).ToList();
        AppLanguageBox.SelectedValue = settings.AppLanguage;
        GameLanguageBox.ItemsSource = gameLanguages;
        GameLanguageBox.SelectedValue = settings.GameLanguage;
        OcrBox.IsChecked = settings.OcrEnabled;
        OcrSecondsBox.Text = settings.OcrWatchSeconds.ToString(CultureInfo.InvariantCulture);
        ClipboardBox.IsChecked = settings.WatchClipboard;
        ScreenshotsBox.IsChecked = settings.WatchGameScreenshots;
        CaptureBox.SelectedItem = CaptureBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals(i.Tag as string, settings.CaptureMethod, StringComparison.OrdinalIgnoreCase))
            ?? CaptureBox.Items[0];
        UpdatesBox.IsChecked = settings.CheckUpdatesOnStartup;
        PrereleaseBox.IsChecked = settings.IncludePrereleases;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            InitialDirectory = Directory.Exists(GamePathBox.Text) ? GamePathBox.Text : null,
            Title = Loc.Instance["Settings.GamePath"]
        };
        if (dlg.ShowDialog(this) == true) GamePathBox.Text = dlg.FolderName;
    }

    private void Default_Click(object sender, RoutedEventArgs e) => GamePathBox.Text = AppSettings.DefaultGamePath;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var path = GamePathBox.Text.Trim().TrimEnd('\\', '/');
        if (!File.Exists(Path.Combine(path, "Game.log")) &&
            MessageBox.Show(this, Loc.Instance["Settings.PathInvalid"], Title,
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        _settings.GamePath = path;
        if (AppLanguageBox.SelectedValue is string app) _settings.AppLanguage = app;
        if (GameLanguageBox.SelectedValue is string game) _settings.GameLanguage = game;
        _settings.OcrEnabled = OcrBox.IsChecked == true;
        if (int.TryParse(OcrSecondsBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var secs))
            _settings.OcrWatchSeconds = Math.Clamp(secs, 5, 300);
        _settings.WatchClipboard = ClipboardBox.IsChecked == true;
        _settings.WatchGameScreenshots = ScreenshotsBox.IsChecked == true;
        if (CaptureBox.SelectedItem is ComboBoxItem { Tag: string method }) _settings.CaptureMethod = method;
        _settings.CheckUpdatesOnStartup = UpdatesBox.IsChecked == true;
        _settings.IncludePrereleases = PrereleaseBox.IsChecked == true;

        DialogResult = true;
    }
}
