using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ScContractTracker.App.Services;

public enum OcrInstallStatus { Installed, Cancelled, Failed }

public sealed record OcrInstallResult(OcrInstallStatus Status, int ExitCode = 0, string? Error = null);

/// <summary>
/// Doinstaluje rozpoznávací modul Windows OCR pro daný jazyk (volitelná součást Windows
/// "Language.OCR~~~xx-YY~0.0.1.0", stahuje se z Windows Update, pár MB).
/// Přidání součásti Windows vyžaduje práva správce – Windows proto jednou zobrazí dotaz UAC.
/// Používá se DISM, který je v každých Windows a nezávisí na PowerShell execution policy.
/// </summary>
public static partial class OcrLanguageInstaller
{
    [GeneratedRegex(@"^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8}){0,2}$")]
    private static partial Regex LanguageTagRx();

    public static string CapabilityName(string languageTag) => $"Language.OCR~~~{languageTag}~0.0.1.0";

    public static async Task<OcrInstallResult> InstallAsync(string languageTag)
    {
        // Tag jde do příkazové řádky elevovaného procesu – přísná kontrola formátu.
        if (!LanguageTagRx().IsMatch(languageTag))
            return new OcrInstallResult(OcrInstallStatus.Failed, Error: "Neplatný jazykový tag: " + languageTag);

        var dism = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "dism.exe");
        var psi = new ProcessStartInfo(dism,
            $"/Online /Add-Capability /CapabilityName:{CapabilityName(languageTag)} /NoRestart /Quiet")
        {
            UseShellExecute = true,   // nutné pro Verb = runas
            Verb = "runas",           // spustit jako správce (dotaz UAC)
            WindowStyle = ProcessWindowStyle.Hidden
        };

        try
        {
            using var process = Process.Start(psi);
            if (process is null) return new OcrInstallResult(OcrInstallStatus.Failed, Error: "DISM se nespustil.");
            await process.WaitForExitAsync();
            // 0 = hotovo, 3010 = hotovo, doporučen restart (pro OCR není potřeba)
            return process.ExitCode is 0 or 3010
                ? new OcrInstallResult(OcrInstallStatus.Installed, process.ExitCode)
                : new OcrInstallResult(OcrInstallStatus.Failed, process.ExitCode);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED – uživatel dotaz UAC odmítl
            return new OcrInstallResult(OcrInstallStatus.Cancelled);
        }
        catch (Exception ex)
        {
            AppPaths.LogError(ex);
            return new OcrInstallResult(OcrInstallStatus.Failed, Error: ex.Message);
        }
    }
}
