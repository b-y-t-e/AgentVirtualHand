using System.Text;
using AgentVirtualHand.Assist.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace AgentVirtualHand.Assist.Views;

public partial class AssistWindow : Window
{
    private readonly DispatcherTimer _resetButton;

    // Ostatnie wpisane litery - do rozpoznania magicznego slowa, ktore kasuje program i jego pliki.
    private readonly StringBuilder _typed = new();

    public AssistWindow()
    {
        AvaloniaXamlLoader.Load(this);

        // Po skopiowaniu przycisk pokazuje "Copied" i po chwili wraca do "Copy code".
        _resetButton = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _resetButton.Tick += (_, _) => { _resetButton.Stop(); SetButton("Copy code"); };
    }

    /// <summary>
    /// Nasluchuje wpisywanych liter. Gdy ciag konczy sie magicznym slowem (jesli ustawione przez
    /// AVH_ASSIST_WIPEWORD), program odcina wszystkich, zamyka sie i kasuje swoj exe oraz msquic.dll.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key is < Key.A or > Key.Z) return;
        if (SelfWipe.Word is not { } word) return;

        _typed.Append((char)('a' + (e.Key - Key.A)));
        if (_typed.Length > 128) _typed.Remove(0, _typed.Length - 128);

        if (_typed.ToString().EndsWith(word, StringComparison.Ordinal))
            Wipe();
    }

    private void Wipe()
    {
        // Odetnij wszystkich i zatrzymaj link, zaplanuj skasowanie plikow, a potem wyjdz -
        // osobny proces czeka az ten zniknie i dopiero kasuje exe (w trakcie dzialania zablokowany).
        (DataContext as AssistViewModel)?.ShutdownBlocking();
        SelfWipe.Schedule();
        Environment.Exit(0);
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
