using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace ScContractTracker.App.Services;

public enum UpdateCheckResult { NotInstalled, UpToDate, Available, Failed }

/// <summary>
/// Automatické aktualizace z GitHub Releases přes Velopack. Release vytváří workflow
/// .github/workflows/release.yml po pushnutí tagu v*.
/// </summary>
public sealed class UpdateService
{
    private UpdateInfo? _pending;
    private UpdateManager? _manager;

    public static string RepositoryUrl =>
        typeof(UpdateService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "RepositoryUrl")?.Value
        ?? "https://github.com/SnikerCZE/ScContractTracker";

    public static string CurrentVersion =>
        typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0]
        ?? typeof(UpdateService).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    public string? AvailableVersion => _pending?.TargetFullRelease.Version.ToString();
    public string? LastError { get; private set; }

    public async Task<UpdateCheckResult> CheckAsync(bool includePrerelease)
    {
        try
        {
            _manager = new UpdateManager(new GithubSource(RepositoryUrl, null, includePrerelease));
            if (!_manager.IsInstalled) return UpdateCheckResult.NotInstalled;
            _pending = await _manager.CheckForUpdatesAsync();
            return _pending is null ? UpdateCheckResult.UpToDate : UpdateCheckResult.Available;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            AppPaths.LogError(ex);
            return UpdateCheckResult.Failed;
        }
    }

    public async Task DownloadAndRestartAsync(Action<int>? progress = null)
    {
        if (_manager is null || _pending is null) return;
        await _manager.DownloadUpdatesAsync(_pending, progress);
        _manager.ApplyUpdatesAndRestart(_pending.TargetFullRelease);
    }
}
