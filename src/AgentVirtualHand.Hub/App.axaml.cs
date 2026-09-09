using AgentVirtualHand.Hub.ViewModels;
using AgentVirtualHand.Hub.Views;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace AgentVirtualHand.Hub;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new HubViewModel();
            desktop.MainWindow = new MainWindow { DataContext = viewModel };

            // Zamknięcie okna musi zamknąć wszystkie lokalne porty - inaczej zostawialibyśmy
            // otwarte wejścia do cudzych maszyn. Czekanie idzie wątkiem roboczym, żeby nie
            // zakleszczyć dyspozytora UI na asynchronicznym zatrzymaniu serwerów.
            desktop.ShutdownRequested += (_, _) => viewModel.ShutdownBlocking();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
