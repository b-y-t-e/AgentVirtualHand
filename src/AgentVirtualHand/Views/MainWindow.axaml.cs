using AgentVirtualHand.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace AgentVirtualHand.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        KeyDown += OnShortcut;

        // Kazdy ruch myszy i klawisz odswieza licznik bezczynnosci blokady.
        AddHandler(KeyDownEvent, (_, _) => Model?.Lock.NoteActivity(), RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, (_, _) => Model?.Lock.NoteActivity(), RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, (_, _) => Model?.Lock.NoteActivity(), RoutingStrategies.Tunnel);

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

    /// <summary>Skróty z paska na dole okna. Ctrl+D zamiast Ctrl+C - to drugie zabiera pole tekstowe.</summary>
    private async void OnShortcut(object? sender, KeyEventArgs e)
    {
        // Zablokowane okno nie reaguje na skroty - inaczej dalyby dostep do zaslonietej tresci.
        if (Model is null || Model.Lock.IsLocked || e.KeyModifiers != KeyModifiers.Control) return;

        switch (e.Key)
        {
            case Key.S:
                e.Handled = true;
                await Model.ToggleServerAsync();
                break;
            case Key.P when Model.IsRunning:
                e.Handled = true;
                await Model.InviteAsync();
                break;
            case Key.D when Model.HasInvitation:
                e.Handled = true;
                OnCopy(sender, new RoutedEventArgs());
                break;

        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private MainViewModel? Model => DataContext as MainViewModel;

    private async void OnToggleServer(object? sender, RoutedEventArgs e)
    {
        if (Model is null) return;
        await Model.ToggleServerAsync();
    }

    /// <summary>Przyciski siedza w szablonie wiersza, wiec maszyne bierzemy z jego kontekstu.</summary>
    private static MachineRow? RowOf(object? sender) => (sender as Control)?.DataContext as MachineRow;

    private void OnAdmitMachine(object? sender, RoutedEventArgs e)
    {
        if (Model is null || RowOf(sender) is not { } machine) return;
        Model.AdmitMachine(machine);
    }

    private void OnExtendAccess(object? sender, RoutedEventArgs e)
    {
        if (Model is null || RowOf(sender) is not { } machine) return;
        Model.ExtendAccess(machine);
    }

    private void OnRevokeAccess(object? sender, RoutedEventArgs e)
    {
        if (Model is null || RowOf(sender) is not { } machine) return;
        Model.RevokeAccess(machine);
    }

    private async void OnDeleteMachine(object? sender, RoutedEventArgs e)
    {
        if (Model is null || RowOf(sender) is not { } machine) return;
        await Model.DeleteMachineAsync(machine);
    }

    private void OnPickDuration(object? sender, RoutedEventArgs e)
    {
        if (Model is null || (sender as Control)?.Tag is not { } tag) return;
        if (int.TryParse(tag.ToString(), out var minutes)) Model.DurationMinutes = minutes;
    }

    private async void OnInvite(object? sender, RoutedEventArgs e)
    {
        if (Model is null) return;
        await Model.InviteAsync();
    }

    private void OnRevokeEveryone(object? sender, RoutedEventArgs e) => Model?.RevokeEveryone();

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (Model is null || Clipboard is null) return;

        await Clipboard.SetTextAsync(Model.ClipboardPayload);
        Model.NoteCopied();
    }

}

