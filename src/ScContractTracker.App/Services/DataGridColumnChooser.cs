using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;

namespace ScContractTracker.App.Services;

/// <summary>
/// Zobrazit / skrýt sloupce DataGridu: pravé tlačítko na hlavičce sloupce otevře nabídku se všemi sloupci.
/// Použití v XAML:  <c>svc:DataGridColumnChooser.GridKey="history"</c> na DataGridu
/// a <c>svc:DataGridColumnChooser.Key="title"</c> na každém sloupci, který jde skrýt.
/// Skryté sloupce se ukládají do nastavení přes <see cref="Store"/>.
/// </summary>
public static class DataGridColumnChooser
{
    /// <summary>Úložiště skrytých sloupců (nastaví MainViewModel): klíč tabulky → klíče skrytých sloupců.</summary>
    public static Func<string, ICollection<string>>? Store { get; set; }
    /// <summary>Úložiště šířek sloupců (klíč tabulky → klíč sloupce → px).</summary>
    public static Func<string, IDictionary<string, double>>? WidthStore { get; set; }
    /// <summary>Úložiště pořadí sloupců (klíč tabulky → klíče zleva doprava).</summary>
    public static Func<string, IList<string>>? OrderStore { get; set; }
    /// <summary>Zavolá se po změně (uložení nastavení).</summary>
    public static Action? Changed { get; set; }

    // Tažení úchytem mění šířku mnohokrát za vteřinu → ukládáme s malým zpožděním.
    private static readonly DispatcherTimer SaveTimer = CreateSaveTimer();
    private static DispatcherTimer CreateSaveTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        t.Tick += (_, _) => { t.Stop(); Changed?.Invoke(); };
        return t;
    }
    private static void SaveLater() { SaveTimer.Stop(); SaveTimer.Start(); }

    public static readonly DependencyProperty KeyProperty =
        DependencyProperty.RegisterAttached("Key", typeof(string), typeof(DataGridColumnChooser), new PropertyMetadata(null));
    public static string? GetKey(DependencyObject o) => (string?)o.GetValue(KeyProperty);
    public static void SetKey(DependencyObject o, string? v) => o.SetValue(KeyProperty, v);

    public static readonly DependencyProperty GridKeyProperty =
        DependencyProperty.RegisterAttached("GridKey", typeof(string), typeof(DataGridColumnChooser),
            new PropertyMetadata(null, OnGridKeyChanged));
    public static string? GetGridKey(DependencyObject o) => (string?)o.GetValue(GridKeyProperty);
    public static void SetGridKey(DependencyObject o, string? v) => o.SetValue(GridKeyProperty, v);

    private static void OnGridKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid) return;
        grid.Loaded -= OnLoaded;
        grid.Loaded += OnLoaded;
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        var grid = (DataGrid)sender;
        var gridKey = GetGridKey(grid);
        if (gridKey is null || grid.Tag as string == "columns-ready") return;
        grid.Tag = "columns-ready";

        var hidden = Store?.Invoke(gridKey);
        foreach (var col in grid.Columns)
            if (GetKey(col) is { } k && hidden?.Contains(k) == true)
                col.Visibility = Visibility.Collapsed;

        // Uložené šířky
        var widths = WidthStore?.Invoke(gridKey);
        foreach (var col in grid.Columns)
            if (GetKey(col) is { } k && widths is not null && widths.TryGetValue(k, out var w) && w >= 20)
                col.Width = new DataGridLength(w);

        // Uložené pořadí (sloupce bez klíče – šipka, ✎, ✕ – zůstávají na svém místě).
        // DisplayIndex se nastavuje vzestupně 0..n-1, jinak se DataGrid dostane do nekonzistentního stavu
        // (ArgumentOutOfRange v ColumnFromDisplayIndex při každém překreslení).
        if (OrderStore?.Invoke(gridKey) is { Count: > 0 } order)
        {
            try
            {
                var cols = grid.Columns.OrderBy(c => c.DisplayIndex).ToList();
                var keyed = new Queue<DataGridColumn>(cols.Where(c => GetKey(c) is not null)
                    .OrderBy(c => { var i = order.IndexOf(GetKey(c)!); return i < 0 ? int.MaxValue : i; })
                    .ThenBy(c => c.DisplayIndex));
                var desired = cols.Select(c => GetKey(c) is null ? c : keyed.Dequeue()).ToList();
                for (var i = 0; i < desired.Count; i++)
                    if (desired[i].DisplayIndex != i) desired[i].DisplayIndex = i;
            }
            catch (Exception ex)
            {
                AppPaths.LogError(ex);
                order.Clear(); // poškozené uložené pořadí zahodíme
                Changed?.Invoke();
            }
        }

        // Sledování ruční změny šířky a přetažení sloupce
        var widthDesc = DependencyPropertyDescriptor.FromProperty(DataGridColumn.WidthProperty, typeof(DataGridColumn));
        foreach (var col in grid.Columns)
        {
            if (GetKey(col) is not { } key) continue;
            widthDesc.AddValueChanged(col, (_, _) =>
            {
                if (WidthStore?.Invoke(gridKey) is not { } store) return;
                var px = col.Width.IsAbsolute ? col.Width.Value : col.ActualWidth;
                if (px < 20 || double.IsNaN(px)) return;
                store[key] = Math.Round(px);
                SaveLater();
            });
        }
        grid.ColumnReordered += (_, _) =>
        {
            if (OrderStore?.Invoke(gridKey) is not { } store) return;
            store.Clear();
            foreach (var col in grid.Columns.OrderBy(x => x.DisplayIndex))
                if (GetKey(col) is { } k) store.Add(k);
            SaveLater();
        };

        var menu = new ContextMenu();
        menu.Opened += (_, _) => Rebuild(menu, grid, gridKey);

        // Nabídka jen na hlavičkách sloupců (ne na řádcích)
        var baseStyle = grid.TryFindResource(typeof(DataGridColumnHeader)) as Style;
        var style = new Style(typeof(DataGridColumnHeader), baseStyle);
        style.Setters.Add(new Setter(FrameworkElement.ContextMenuProperty, menu));
        style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, Loc.Instance["Columns.Hint"]));
        grid.ColumnHeaderStyle = style;
    }

    private static void Rebuild(ContextMenu menu, DataGrid grid, string gridKey)
    {
        menu.Items.Clear();
        menu.Items.Add(new MenuItem { Header = Loc.Instance["Columns.Title"], IsEnabled = false });
        menu.Items.Add(new Separator());
        foreach (var col in grid.Columns)
        {
            if (GetKey(col) is not { } key) continue;
            var item = new MenuItem
            {
                IsCheckable = true,
                IsChecked = col.Visibility == Visibility.Visible,
                StaysOpenOnClick = true
            };
            item.SetBinding(HeaderedItemsControl.HeaderProperty, new Binding(nameof(DataGridColumn.Header)) { Source = col });
            item.Click += (_, _) =>
            {
                // aspoň jeden sloupec musí zůstat
                if (!item.IsChecked && grid.Columns.Count(c => c.Visibility == Visibility.Visible && GetKey(c) is not null) <= 1)
                {
                    item.IsChecked = true;
                    return;
                }
                col.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                Save(grid, gridKey);
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var all = new MenuItem { Header = Loc.Instance["Columns.ShowAll"] };
        all.Click += (_, _) =>
        {
            foreach (var col in grid.Columns) col.Visibility = Visibility.Visible;
            Save(grid, gridKey);
        };
        menu.Items.Add(all);

        var reset = new MenuItem { Header = Loc.Instance["Columns.ResetLayout"] };
        reset.Click += (_, _) =>
        {
            WidthStore?.Invoke(gridKey).Clear();
            OrderStore?.Invoke(gridKey).Clear();
            Changed?.Invoke();
            MessageBox.Show(Loc.Instance["Columns.ResetDone"], Loc.Instance["App.Title"]);
        };
        menu.Items.Add(reset);
    }

    private static void Save(DataGrid grid, string gridKey)
    {
        if (Store?.Invoke(gridKey) is not { } hidden) return;
        hidden.Clear();
        foreach (var col in grid.Columns)
            if (GetKey(col) is { } k && col.Visibility != Visibility.Visible)
                hidden.Add(k);
        Changed?.Invoke();
    }
}
