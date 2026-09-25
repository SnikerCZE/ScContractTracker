using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ScContractTracker.App.Services;

/// <summary>Text velkými písmeny (nadpisy a tlačítka ve stylu mobiGlasu).</summary>
public sealed class UpperCaseConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s ? s.ToUpper(CultureInfo.CurrentCulture) : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}

/// <summary>Prázdný / null text → Collapsed (s parametrem "invert" naopak).</summary>
public sealed class EmptyToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var empty = value is null || (value is string s && string.IsNullOrWhiteSpace(s));
        if (parameter as string == "invert") empty = !empty;
        return empty ? Visibility.Collapsed : Visibility.Visible;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}

/// <summary>
/// Souhrn skupiny v historii (hlavička skupiny DataGridu): počet, dokončené, výdělek, aUEC/h.
/// Vstup: CollectionViewGroup.Items (+ ItemCount jen kvůli přepočtu při změně).
/// </summary>
public sealed class GroupSummaryConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length == 0 || values[0] is not System.Collections.IEnumerable items) return "";
        var models = items.OfType<ScContractTracker.App.ViewModels.ContractViewModel>().Select(v => v.Model).ToList();
        var s = ScContractTracker.Core.Stats.StatisticsService.Build("", models);
        var L = Loc.Instance;
        return L.Format("Group.Summary", s.Accepted, s.Completed,
            ScContractTracker.App.ViewModels.Fmt.Percent(s.SuccessRate),
            ScContractTracker.App.ViewModels.Fmt.Money(s.TotalReward),
            ScContractTracker.App.ViewModels.Fmt.Money(s.RewardPerHour),
            ScContractTracker.App.ViewModels.Fmt.ShortDuration(s.AverageDuration));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Šířka bez okrajů: ActualWidth − parametr (px). Detaily řádku DataGridu dostávají nekonečnou šířku,
/// takže text by se nezalamoval – šířku proto vážeme na šířku tabulky.
/// </summary>
public sealed class SubtractConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var width = value is double d && !double.IsNaN(d) && !double.IsInfinity(d) ? d : 0;
        var minus = double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : 0;
        return Math.Max(200, width - minus);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}

/// <summary>Předá DataContext do míst mimo vizuální strom (např. hlavičky sloupců DataGridu).</summary>
public sealed class BindingProxy : Freezable
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy), new UIPropertyMetadata(null));

    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new BindingProxy();
}
