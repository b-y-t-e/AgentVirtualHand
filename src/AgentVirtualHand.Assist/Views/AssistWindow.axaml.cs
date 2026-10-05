using AgentVirtualHand.Assist.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace AgentVirtualHand.Assist.Views;

public partial class AssistWindow : Window
{
    private readonly DispatcherTimer _hintReset;

    public AssistWindow()
    {
        AvaloniaXamlLoader.Load(this);

        // Po skopiowaniu pokazujemy "Copied" i po chwili wracamy do podpowiedzi.
        _hintReset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _hintReset.Tick += (_, _) => { _hintReset.Stop(); SetHint("Click the code to copy it"); };
    }

    /// <summary>Klik w kod kopiuje go do schowka - zadnego przycisku, a kod da sie przekazac dalej.</summary>
    private async void OnCopyCode(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not AssistViewModel vm || vm.Code.Length == 0) return;
        if (Clipboard is not { } clipboard) return;

        await clipboard.SetTextAsync(vm.Code);
        SetHint("Copied");
        _hintReset.Stop();
        _hintReset.Start();
    }

    private void SetHint(string text)
    {
        if (this.FindControl<TextBlock>("CopyHint") is { } hint) hint.Text = text;
    }
}
