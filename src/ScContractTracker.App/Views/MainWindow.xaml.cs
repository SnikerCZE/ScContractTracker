using System.Windows;
using System.Windows.Controls;
using ScContractTracker.App.ViewModels;

namespace ScContractTracker.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ScContractTracker.App.Services.Native.UseDarkTitleBar(this);
        _vm = new MainViewModel();
        DataContext = _vm;
        Loaded += (_, _) => _vm.Start(this);
        Closed += (_, _) => _vm.Dispose();
    }

    // Položky jazyků jsou v popupu podmenu, kde RelativeSource na okno nefunguje – obsluha v code-behind.
    private void AppLanguage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: LanguageOption opt })
            _vm.SetAppLanguageCommand.Execute(opt.Code);
    }

    private void GameLanguage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: LanguageOption opt })
            _vm.SetGameLanguageCommand.Execute(opt.Code);
    }
}
