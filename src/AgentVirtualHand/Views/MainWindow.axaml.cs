using AgentVirtualHand.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace AgentVirtualHand.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        KeyDown += OnShortcut;
    }

    /// <summary>Skróty z paska na dole okna. Ctrl+D zamiast Ctrl+C - to drugie zabiera pole tekstowe.</summary>
    private async void OnShortcut(object? sender, KeyEventArgs e)
    {
        if (Model is null || e.KeyModifiers != KeyModifiers.Control) return;

        switch (e.Key)
        {
            case Key.S:
                e.Handled = true;
                await Model.ToggleServerAsync();
                break;
            case Key.P when Model.CanPair:
                e.Handled = true;
                Model.OpenAccess();
                break;
            case Key.D when Model.HasInvitation:
                e.Handled = true;
                OnCopy(sender, new RoutedEventArgs());
                break;
            case Key.N when Model.IsRunning:
                e.Handled = true;
                await Model.NewInvitationAsync();
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

    private void OnOpenAccess(object? sender, RoutedEventArgs e) => Model?.OpenAccess();

    private async void OnNewInvitation(object? sender, RoutedEventArgs e)
    {
        if (Model is null) return;
        await Model.NewInvitationAsync();
    }

    private async void OnResetPeer(object? sender, RoutedEventArgs e)
    {
        if (Model is null) return;
        await Model.ResetPeerAsync();
    }

    private void OnEndSession(object? sender, RoutedEventArgs e) => Model?.EndSession();

    private void OnExtend(object? sender, RoutedEventArgs e) => Model?.ExtendSession();

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (Model is null || Clipboard is null) return;

        await Clipboard.SetTextAsync(Model.ClipboardPayload);
        Model.NoteCopied();
    }

}

