using AgentVirtualHand.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace AgentVirtualHand.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private MainViewModel? Model => DataContext as MainViewModel;

    private async void OnToggleServer(object? sender, RoutedEventArgs e)
    {
        if (Model is null) return;
        await Model.ToggleServerAsync();
    }

    private void OnPair(object? sender, RoutedEventArgs e) => Model?.StartPairing();

    private void OnEndSession(object? sender, RoutedEventArgs e) => Model?.EndSession();

    private void OnExtend(object? sender, RoutedEventArgs e) => Model?.ExtendSession();

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (Model is null || Clipboard is null) return;

        await Clipboard.SetTextAsync(Model.ClipboardPayload);
        Model.NoteCopied();
    }

    private async void OnOpenFirewall(object? sender, RoutedEventArgs e)
    {
        if (Model is null) return;
        await Model.ConfigureFirewallAsync(open: true);
    }

    private async void OnCloseFirewall(object? sender, RoutedEventArgs e)
    {
        if (Model is null) return;
        await Model.ConfigureFirewallAsync(open: false);
    }
}
