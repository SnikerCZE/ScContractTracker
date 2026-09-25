using System.Windows;
using Velopack.Windows;

// Třída Shortcuts je ve Velopacku označená jako zastaralá, protože zástupce běžně spravuje instalátor.
// My ale zástupce na ploše vytváříme až po souhlasu uživatele, takže ji potřebujeme.
#pragma warning disable CS0618

namespace ScContractTracker.App.Services;

/// <summary>
/// Zástupci po instalaci přes Velopack. Instalátor vytvoří jen zástupce v nabídce Start
/// (vpk pack --shortcuts StartMenuRoot); na ploše ho vytvoříme jen se souhlasem uživatele
/// při prvním spuštění po instalaci.
/// </summary>
public static class InstallShortcuts
{
    /// <summary>Nastaví hook Velopacku OnFirstRun – aplikace byla právě nainstalována.</summary>
    public static bool FirstRunAfterInstall { get; set; }

    /// <summary>Hook OnBeforeUninstallFastCallback – musí být rychlý a bez UI.</summary>
    public static void RemoveAll()
    {
        try { new Shortcuts().RemoveShortcutForThisExe(ShortcutLocation.Desktop | ShortcutLocation.StartMenuRoot); }
        catch (Exception ex) { AppPaths.LogError(ex); }
    }

    /// <summary>Po prvním spuštění zajistí zástupce v nabídce Start a zeptá se na zástupce na ploše.</summary>
    public static void OfferAfterInstall(Window owner)
    {
        if (!FirstRunAfterInstall) return;
        FirstRunAfterInstall = false;
        try
        {
            var shortcuts = new Shortcuts();
            shortcuts.CreateShortcutForThisExe(ShortcutLocation.StartMenuRoot);

            var L = Loc.Instance;
            var answer = MessageBox.Show(owner, L["Install.DesktopShortcutPrompt"], L["App.Title"],
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
            if (answer == MessageBoxResult.Yes)
                shortcuts.CreateShortcutForThisExe(ShortcutLocation.Desktop);
        }
        catch (Exception ex) { AppPaths.LogError(ex); }
    }
}
