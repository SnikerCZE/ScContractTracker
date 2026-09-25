using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace ScContractTracker.App.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<TValue>(ref TValue field, TValue value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<TValue>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public RelayCommand(Action execute) : this(_ => execute()) { }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);
}

public static class Fmt
{
    public static string Duration(TimeSpan? ts) =>
        ts is { } t ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : "–";

    public static string Money(double? v) =>
        v is { } x ? x.ToString("N0", CultureInfo.CurrentCulture) : "–";

    public static string Time(DateTimeOffset? t) =>
        t is { } x ? x.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : "–";

    /// <summary>„21:03“ dnes, jinak „18. 9. 21:03“ (podle jazyka Windows).</summary>
    public static string ShortTime(DateTimeOffset? t)
    {
        if (t is not { } x) return "–";
        var local = x.ToLocalTime();
        var time = local.ToString("t", CultureInfo.CurrentCulture);
        return local.Date == DateTime.Today ? time
            : local.ToString(CultureInfo.CurrentCulture.DateTimeFormat.MonthDayPattern, CultureInfo.CurrentCulture) + " " + time;
    }

    /// <summary>Krátká délka: „0:38“ (h:mm), pod minutu „0:00:42“.</summary>
    public static string ShortDuration(TimeSpan? ts) =>
        ts is not { } t ? "–" : t.TotalMinutes < 1 ? Duration(t) : $"{(int)t.TotalHours}:{t.Minutes:00}";

    public static string Percent(double? v) =>
        v is { } x ? x.ToString("P0", CultureInfo.CurrentCulture) : "–";
}
