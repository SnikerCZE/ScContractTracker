using System.Text.Json;
using System.Text.Json.Serialization;
using ScContractTracker.Core.Models;

namespace ScContractTracker.Core.Storage;

public interface IContractStore
{
    IReadOnlyList<Contract> Load();
    IReadOnlyList<RewardEntry> LoadRewards();
    void Save(IReadOnlyCollection<Contract> contracts, IReadOnlyCollection<RewardEntry> rewards);
}

/// <summary>
/// Ukládá historii kontraktů do jednoho JSON souboru (standardně %AppData%\ScContractTracker\contracts.json).
/// Zápis je atomický přes dočasný soubor, takže pád aplikace data nepoškodí.
/// </summary>
public sealed class JsonContractStore : IContractStore
{
    private readonly string _path;
    private readonly object _io = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public JsonContractStore(string path) => _path = path;

    public string Path => _path;

    public IReadOnlyList<Contract> Load() => Read()?.Contracts ?? new List<Contract>();

    public IReadOnlyList<RewardEntry> LoadRewards() => Read()?.Rewards ?? new List<RewardEntry>();

    private StoreDocument? Read()
    {
        lock (_io)
        {
            if (!File.Exists(_path)) return null;
            try
            {
                return JsonSerializer.Deserialize<StoreDocument>(File.ReadAllText(_path), Options);
            }
            catch (JsonException)
            {
                // Záloha poškozeného souboru, ať o data uživatel nepřijde úplně.
                File.Copy(_path, _path + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss"), overwrite: true);
                return null;
            }
        }
    }

    public void Save(IReadOnlyCollection<Contract> contracts, IReadOnlyCollection<RewardEntry> rewards)
    {
        lock (_io)
        {
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var doc = new StoreDocument
            {
                Version = 2,
                Contracts = contracts.OrderBy(c => c.AcceptedAt).ToList(),
                Rewards = rewards.OrderBy(r => r.Time).ToList()
            };
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(doc, Options));
            File.Move(tmp, _path, overwrite: true);
        }
    }

    private sealed class StoreDocument
    {
        public int Version { get; set; }
        public List<Contract> Contracts { get; set; } = new();
        public List<RewardEntry> Rewards { get; set; } = new();
    }
}
