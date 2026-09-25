namespace ScContractTracker.Core.Models;

public enum RewardKind
{
    Blueprint,
    Item
}

/// <summary>
/// Jedna získaná odměna z Game.logu („Received Blueprint: …“ / „You've earned: …“).
/// Ukládá se samostatně, takže se neztratí, ani když ji nejde přiřadit ke kontraktu.
/// </summary>
public sealed class RewardEntry
{
    public RewardKind Kind { get; set; }
    public string Name { get; set; } = "";
    public DateTimeOffset Time { get; set; }
    /// <summary>Kontrakt, po jehož dokončení odměna přišla (null = nepřiřazeno).</summary>
    public string? ContractId { get; set; }
}
