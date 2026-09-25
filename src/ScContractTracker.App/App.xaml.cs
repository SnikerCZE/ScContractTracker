using System.Windows;
using System.Windows.Threading;
using ScContractTracker.App.Services;
using ScContractTracker.App.Views;

namespace ScContractTracker.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) AppPaths.LogError(ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppPaths.LogError(args.Exception);
            args.SetObserved();
        };

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private static bool _showingError;
    private static string? _lastErrorMessage;
    private static DateTime _lastErrorShown;

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppPaths.LogError(e.Exception);
        e.Handled = true;
        // Stejná chyba se může opakovat při každém překreslení → hlásíme ji jen jednou (max. jednou za minutu),
        // a nikdy víc oken najednou.
        var msg = e.Exception.Message;
        if (_showingError || (msg == _lastErrorMessage && DateTime.UtcNow - _lastErrorShown < TimeSpan.FromMinutes(1))) return;
        _showingError = true;
        _lastErrorMessage = msg;
        try
        {
            MessageBox.Show(msg + "\n\n" + AppPaths.ErrorLog, "SC Contract Tracker", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _lastErrorShown = DateTime.UtcNow;
            _showingError = false;
        }
    }
}
