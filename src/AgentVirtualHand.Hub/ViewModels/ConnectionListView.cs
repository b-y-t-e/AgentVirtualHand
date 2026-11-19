using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AgentVirtualHand.Hub.ViewModels;

/// <summary>Grupy na liście w kolejności wyświetlania: połączone na górze, wyłączone na dole.</summary>
public enum StatusGroup { Connected, Connecting, Off }

/// <summary>Nagłówek grupy w widoku "All" - osobny element listy, nie stan wiersza.</summary>
public sealed class GroupHeader(StatusGroup group) : INotifyPropertyChanged
{
    private int _count;

    public StatusGroup Group { get; } = group;
    public string Title { get; } = group.ToString().ToLowerInvariant();

    public int Count
    {
        get => _count;
        set
        {
            if (_count == value) return;
            _count = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Jeden przycisk filtra stanu z bieżącą licznością; Group == null to "All".</summary>
public sealed class FilterOption(StatusGroup? group) : INotifyPropertyChanged
{
    private int _count;
    private bool _isSelected;

    public StatusGroup? Group { get; } = group;
    public string Label => $"{Group?.ToString() ?? "All"}  {_count}";

    public static IReadOnlyList<FilterOption> All() =>
        [new(null), .. Enum.GetValues<StatusGroup>().Select(g => new FilterOption(g))];

    public bool Accepts(ConnectionRow row) => Group is null || row.Group == Group;

    public void Update(IReadOnlyCollection<ConnectionRow> rows, StatusGroup? selected)
    {
        Set(ref _count, rows.Count(Accepts), nameof(Label));
        Set(ref _isSelected, Group == selected, nameof(IsSelected));
    }

    public bool IsSelected => _isSelected;

    private void Set<T>(ref T field, T value, string property)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Widok listy maszyn: filtr, wyszukiwanie, sortowanie po grupie i nazwie, nagłówki grup.
/// Nie dotyka zapisanej listy. Kolekcja jest uzgadniana ruchami, a nie budowana od nowa -
/// inaczej co 2 s wiersze by się przerysowywały, a otwarte menu "⋯" by się zamykało.
/// </summary>
public sealed class ConnectionListView
{
    private readonly Dictionary<StatusGroup, GroupHeader> _headers =
        Enum.GetValues<StatusGroup>().ToDictionary(g => g, g => new GroupHeader(g));

    /// <summary>Wiersze (<see cref="ConnectionRow"/>) przeplecione nagłówkami (<see cref="GroupHeader"/>).</summary>
    public ObservableCollection<object> Items { get; } = [];

    public bool HasRows => Items.Count > 0;

    public void Apply(IEnumerable<ConnectionRow> rows, string query, StatusGroup? filter) =>
        Reconcile(Arrange(rows, query, filter));

    /// <summary>Docelowa kolejność elementów; parowanie widać zawsze, niezależnie od filtra.</summary>
    public IReadOnlyList<object> Arrange(IEnumerable<ConnectionRow> rows, string query, StatusGroup? filter)
    {
        var visible = rows
            .Where(row => row.IsPairing || ((filter is null || row.Group == filter) && row.Matches(query)))
            .OrderBy(row => row.Group)
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Nagłówki grup tylko w widoku "All" - przy filtrze powtarzałyby wybrany przycisk.
        return filter is null ? WithHeaders(visible) : visible.Cast<object>().ToList();
    }

    private List<object> WithHeaders(List<ConnectionRow> sorted)
    {
        var items = new List<object>();
        foreach (var group in sorted.GroupBy(row => row.Group))
        {
            var header = _headers[group.Key];
            header.Count = group.Count();
            items.Add(header);
            items.AddRange(group);
        }
        return items;
    }

    private void Reconcile(IReadOnlyList<object> target)
    {
        // Najpierw usuwamy to, co wypadło z widoku - inaczej każdy późniejszy element dostałby Move.
        for (var i = Items.Count - 1; i >= 0; i--)
            if (!target.Contains(Items[i])) Items.RemoveAt(i);

        for (var i = 0; i < target.Count; i++)
        {
            var current = Items.IndexOf(target[i]);
            if (current < 0) Items.Insert(i, target[i]);
            else if (current != i) Items.Move(current, i);
        }
    }
}
