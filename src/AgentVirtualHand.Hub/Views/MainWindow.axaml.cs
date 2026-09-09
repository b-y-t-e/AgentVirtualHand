using AgentVirtualHand.Hub.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace AgentVirtualHand.Hub.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

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
