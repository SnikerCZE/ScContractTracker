using System.Text.Json;

namespace ScContractTracker.Core.Localization;

/// <summary>
/// Texty rozhraní aplikace. Každý jazyk = Languages/app/&lt;kód&gt;.json s plochým slovníkem
/// { "_name": "Čeština", "Menu.File": "Soubor", ... }. Chybějící klíče se berou z angličtiny.
/// </summary>
public sealed class AppStrings
{
    private readonly Dictionary<string, Dictionary<string, string>> _languages = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _current = new();
    private Dictionary<string, string> _fallback = new();

    public string CurrentCode { get; private set; } = "en";

    public IReadOnlyList<(string Code, string Name)> Available =>
        _languages.Select(kv => (kv.Key, kv.Value.GetValueOrDefault("_name", kv.Key))).OrderBy(x => x.Item2).ToList();

    public static AppStrings LoadFrom(string folder)
    {
        var s = new AppStrings();
        if (Directory.Exists(folder))
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
            {
                try
                {
                    var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file),
                        new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                    if (dict is not null) s._languages[Path.GetFileNameWithoutExtension(file)] = dict;
                }
                catch (JsonException) { /* vadný soubor přeskočíme */ }
            }
        }
        s._fallback = s._languages.GetValueOrDefault("en") ?? new();
        s.SetLanguage("en");
        return s;
    }

    public void Add(string code, Dictionary<string, string> strings)
    {
        _languages[code] = strings;
        if (code.Equals("en", StringComparison.OrdinalIgnoreCase)) _fallback = strings;
    }

    public void SetLanguage(string code)
    {
        if (!_languages.TryGetValue(code, out var dict)) { code = "en"; dict = _fallback; }
        CurrentCode = code;
        _current = dict;
    }

    public string this[string key] =>
        _current.TryGetValue(key, out var v) ? v :
        _fallback.TryGetValue(key, out var f) ? f : key;

    public string Format(string key, params object[] args) => string.Format(this[key], args);
}
