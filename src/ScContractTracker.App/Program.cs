using ScContractTracker.App.Services;
using ScContractTracker.Core.GameData;
using Velopack;

namespace ScContractTracker.App;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Musí běžet jako první – Velopack při instalaci/aktualizaci spouští aplikaci s vlastními argumenty.
        VelopackApp.Build()
            .OnFirstRun(_ => InstallShortcuts.FirstRunAfterInstall = true)
            .OnBeforeUninstallFastCallback(_ => InstallShortcuts.RemoveAll())
            .Run();

        // Data.p4k používá kompresi Zstandard, kterou .NET neumí nativně.
        P4kArchive.ZstdFactory = s => new ZstdSharp.DecompressionStream(s);

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
