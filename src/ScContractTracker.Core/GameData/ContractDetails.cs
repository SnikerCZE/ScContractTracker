namespace ScContractTracker.Core.GameData;

/// <summary>Detaily kontraktu z herních dat (Data.p4k).</summary>
public sealed record ContractDetails(
    string? Title,
    string? Description,
    string? Contractor,
    IReadOnlyList<string> Locations,
    IReadOnlyDictionary<string, string> Extra);

public interface IContractDetailsProvider
{
    /// <summary>Připraví data (otevření p4k, načtení lokalizace). Může trvat desítky sekund.</summary>
    Task InitializeAsync(CancellationToken ct = default);

    bool IsReady { get; }

    /// <param name="contractRecord">Hodnota "contract [..]" z Game.logu.</param>
    /// <param name="generatorName">Hodnota "generator name [..]" z Game.logu.</param>
    /// <param name="title">Název z notifikace – záloha pro vyhledání v lokalizaci.</param>
    ContractDetails? Lookup(string? contractRecord, string? generatorName, string? title);
}
