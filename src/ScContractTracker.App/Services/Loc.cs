using System.ComponentModel;
using System.Text.Json;
using System.Windows.Data;
using System.Windows.Markup;
using ScContractTracker.Core.Localization;

namespace ScContractTracker.App.Services;

/// <summary>
/// Lokalizace rozhraní s živým přepnutím jazyka. V XAML: Text="{svc:Tr Menu.File}".
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    private AppStrings _strings = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => _strings[key];

    public string Format(string key, params object[] args) => _strings.Format(key, args);

    public string CurrentCode => _strings.CurrentCode;

    public IReadOnlyList<(string Code, string Name)> Available => _strings.Available;

    public void Load(string code)
    {
        var s = new AppStrings();
        var options = new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        foreach (var dir in AppPaths.LanguageDirs("app"))
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file), options);
                    if (dict is not null) s.Add(Path.GetFileNameWithoutExtension(file), dict);
                }
                catch (Exception ex) { AppPaths.LogError(ex); }
            }
        }
        s.SetLanguage(code);
        _strings = s;
        Refresh();
    }

    public void SetLanguage(string code)
    {
        _strings.SetLanguage(code);
        Refresh();
    }

    private void Refresh()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentCode)));
        LanguageChanged?.Invoke();
    }

    public event Action? LanguageChanged;
}

/// <summary>Markup extension pro překlad: {svc:Tr Klíč}.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class Tr : MarkupExtension
{
    public Tr() { }
    public Tr(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay };
        return binding.ProvideValue(serviceProvider);
    }
}
