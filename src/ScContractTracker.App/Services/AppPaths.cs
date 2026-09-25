namespace ScContractTracker.App.Services;

public static class AppPaths
{
    /// <summary>%AppData%\ScContractTracker – nastavení, historie a vlastní jazyky (přežijí aktualizaci).</summary>
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScContractTracker");

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string ContractsFile => Path.Combine(DataDir, "contracts.json");
    public static string ErrorLog => Path.Combine(DataDir, "error.log");

    public static string AssetsDir => Path.Combine(AppContext.BaseDirectory, "Assets");

    /// <summary>Uživatelská kopie má přednost – lze ji upravit bez nové verze aplikace.</summary>
    public static string LocationsFile
    {
        get
        {
            var user = Path.Combine(DataDir, "locations.json");
            return File.Exists(user) ? user : Path.Combine(AssetsDir, "locations.json");
        }
    }

    public static IEnumerable<string> LanguageDirs(string kind)
    {
        yield return Path.Combine(AssetsDir, "Languages", kind);
        yield return Path.Combine(DataDir, "Languages", kind);
    }

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(Path.Combine(DataDir, "Languages", "app"));
        Directory.CreateDirectory(Path.Combine(DataDir, "Languages", "game"));
    }

    public static void LogError(Exception ex)
    {
        try
        {
            File.AppendAllText(ErrorLog, $"[{DateTime.Now:O}] {ex}\n\n");
        }
        catch { /* logování nesmí shodit aplikaci */ }
    }
}
