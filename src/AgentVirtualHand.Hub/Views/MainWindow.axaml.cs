using AgentVirtualHand.Hub.ViewModels;
using AgentVirtualHand.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace AgentVirtualHand.Hub.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Kazdy ruch myszy i klawisz odswieza licznik bezczynnosci blokady.
        AddHandler(KeyDownEvent, (_, _) => Model?.Lock.NoteActivity(), RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, (_, _) => Model?.Lock.NoteActivity(), RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, (_, _) => Model?.Lock.NoteActivity(), RoutingStrategies.Tunnel);

        AddHandler(KeyDownEvent, OnShortcut, RoutingStrategies.Tunnel);

        // Zaslona ma od razu kursor w polu hasla - inaczej trzeba w nie najpierw kliknac.
        Opened += (_, _) => FocusPassword();
        DataContextChanged += (_, _) =>
        {
            if (Model is null) return;
            Model.Lock.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(LockViewModel.IsLocked) && Model.Lock.IsLocked) FocusPassword();
            };
        };
    }

    private void FocusPassword() =>
        Dispatcher.UIThread.Post(() => this.FindControl<TextBox>("PasswordBox")?.Focus());

    private void OnLockSubmit(object? sender, RoutedEventArgs e) => Model?.Lock.Submit();

    private void OnToggleAutoLock(object? sender, RoutedEventArgs e) => Model?.Lock.ToggleAutoLock();

    private void OnLockKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        e.Handled = true;
        Model?.Lock.Submit();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private HubViewModel? Model => DataContext as HubViewModel;

    /// <summary>Przyciski siedzą w szablonie wiersza, więc maszynę bierzemy z jego kontekstu.</summary>
    private static ConnectionRow? RowOf(object? sender) => (sender as Control)?.DataContext as ConnectionRow;

    private async void OnAdd(object? sender, RoutedEventArgs e)
    {
        if (Model is null) return;
        await Model.AddAsync();
    }

    /// <summary>Skróty działają tylko przy odblokowanym oknie - zasłona ma swoje pole hasła.</summary>
    private void OnShortcut(object? sender, KeyEventArgs e)
    {
        if (Model is not { Lock.IsUnlocked: true } || e.KeyModifiers != KeyModifiers.Control) return;

        switch (e.Key)
        {
            case Key.F:
                e.Handled = true;
                FocusControl("SearchBox", selectAll: true);
                break;
            case Key.N:
                e.Handled = true;
                Model.IsAddOpen = true;
                FocusControl("NewNameBox", selectAll: false);
                break;
            case Key.L:
                e.Handled = true;
                Model.IsLogOpen = !Model.IsLogOpen;
                break;
        }
    }

    private void FocusControl(string name, bool selectAll) => Dispatcher.UIThread.Post(() =>
    {
        if (this.FindControl<TextBox>(name) is not { } box) return;
        box.Focus();
        if (selectAll) box.SelectAll();
    });

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || Model is null) return;
        e.Handled = true;
        Model.Query = "";
    }

    private void OnClearSearch(object? sender, RoutedEventArgs e)
    {
        if (Model is null) return;
        Model.Query = "";
        FocusControl("SearchBox", selectAll: false);
    }

    private void OnCancelPairing(object? sender, RoutedEventArgs e) => Model?.Pairing.Cancel();

    private void OnClearFilters(object? sender, RoutedEventArgs e) => Model?.ClearFilters();

    private void OnFilter(object? sender, RoutedEventArgs e)
    {
        if (Model is null || sender is not Button { DataContext: FilterOption option }) return;
        Model.Filter = option.Group;
    }

    private void OnToggleAdd(object? sender, RoutedEventArgs e)
    {
        if (Model is null) return;
        Model.IsAddOpen = !Model.IsAddOpen;
        if (Model.IsAddOpen) FocusControl("NewNameBox", selectAll: false);
    }

    private void OnToggleLog(object? sender, RoutedEventArgs e)
    {
        if (Model is not null) Model.IsLogOpen = !Model.IsLogOpen;
    }

    private async void OnAddKeyDown(object? sender, KeyEventArgs e)
    {
        if (Model is null) return;

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await Model.AddAsync();
        }
        else if (e.Key == Key.Escape && Model.HasConnections)
        {
            e.Handled = true;
            Model.IsAddOpen = false;
        }
    }

    private async void OnToggle(object? sender, RoutedEventArgs e)
    {
        if (Model is null || RowOf(sender) is not { } row) return;
        await Model.ToggleAsync(row);
    }

    private async void OnRemove(object? sender, RoutedEventArgs e)
    {
        if (Model is null || RowOf(sender) is not { } row) return;
        await Model.RemoveAsync(row);
    }

    private void OnRotateToken(object? sender, RoutedEventArgs e)
    {
        if (Model is null || RowOf(sender) is not { } row) return;
        Model.RotateToken(row);
    }

    private async void OnCopyPrompt(object? sender, RoutedEventArgs e)
    {
        if (Model is null || Clipboard is null || RowOf(sender) is not { } row) return;

        await Clipboard.SetTextAsync(Model.PromptFor(row));
        Model.NotePromptCopied(row);
    }
}
