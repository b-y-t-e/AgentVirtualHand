using AgentVirtualHand.Assist.ViewModels;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace AgentVirtualHand.Assist.Views;

public partial class AssistWindow : Window
{
    private readonly DispatcherTimer _resetButton;

    public AssistWindow()
    {
        AvaloniaXamlLoader.Load(this);

        // Po skopiowaniu przycisk pokazuje "Copied" i po chwili wraca do "Copy code".
        _resetButton = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _resetButton.Tick += (_, _) => { _resetButton.Stop(); SetButton("Copy code"); };
    }

    /// <summary>Kopiuje kod do schowka - z przycisku albo z klikniecia w sam kod.</summary>
    private async void OnCopyCode(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AssistViewModel vm || vm.Code.Length == 0) return;
        if (Clipboard is not { } clipboard) return;

        await clipboard.SetTextAsync(vm.Code);
        SetButton("Copied");
        _resetButton.Stop();
        _resetButton.Start();
    }

    private void SetButton(string text)
    {
        if (this.FindControl<Button>("CopyButton") is { } button) button.Content = text;
    }
}
