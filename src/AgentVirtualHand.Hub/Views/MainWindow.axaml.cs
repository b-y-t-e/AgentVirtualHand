using AgentVirtualHand.Hub.ViewModels;
using AgentVirtualHand.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
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
