namespace ScContractTracker.Core.Models;

public enum GameEventKind
{
    ContractAccepted,
    ContractCompleted,
    ContractFailed,
    ContractAbandoned,
    ContractWithdrawn,
    /// <summary>Propojení missionId ↔ generátor/záznam kontraktu v DataCore.</summary>
    ContractMetadata,
    ObjectiveUpdated,
    ObjectiveCompleted,
    RewardReceived,
    LocationChanged,
    QuantumDestination,
    ObjectiveCancelled,
    /// <summary>„Received Blueprint: …“ (bez MissionId – přiřadí se k právě dokončenému kontraktu).</summary>
    BlueprintReceived,
    /// <summary>„You've earned: …“ – předmět za kontrakt.</summary>
    ItemEarned,
    /// <summary>MissionAccept comms: organizace, zadavatel, typ mise.</summary>
    ContractGiver,
    /// <summary>„Log started on …“ – začátek nové herní relace (nový Game.log).</summary>
    SessionStarted,
    /// <summary>Řádné odhlášení / ukončení hry (Player requested disconnect, SystemQuit) – hra ukončí všechny přijaté kontrakty.</summary>
    SessionEnded
}

/// <summary>Událost rozpoznaná z jednoho řádku Game.logu.</summary>
public sealed record GameEvent(GameEventKind Kind, DateTimeOffset Time)
{
    public string? MissionId { get; init; }
    public string? Title { get; init; }
    public string? ObjectiveId { get; init; }
    public string? GeneratorName { get; init; }
    public string? ContractRecord { get; init; }
    public long? Amount { get; init; }
    public string? Location { get; init; }
    public string? ContractDefinitionId { get; init; }
    /// <summary>Souřadnice značky cíle (CreateMarker) a těleso, ke kterému se vztahují.</summary>
    public double[]? Position { get; init; }
    public string? ZoneHostId { get; init; }
    /// <summary>Štítky z názvu kontraktu: „300 Rep“, „BP“, „ACE“…</summary>
    public IReadOnlyList<string>? Tags { get; init; }
    public string? Organization { get; init; }
    public string? MissionGiver { get; init; }
    public string? MissionType { get; init; }
    public string RawLine { get; init; } = "";
}
