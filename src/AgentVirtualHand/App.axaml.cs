using AgentVirtualHand.ViewModels;
using AgentVirtualHand.Views;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace AgentVirtualHand;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainViewModel();
            desktop.MainWindow = new MainWindow { DataContext = viewModel };

            // Zamknięcie okna musi odciąć dostęp. Blokowanie wątku UI na async zatrzymaniu
            // serwera zakleszcza się (kontynuacje wracają na dyspozytora UI), dlatego czekanie
            // odbywa się na wątku roboczym - patrz MainViewModel.ShutdownBlocking.
            desktop.ShutdownRequested += (_, _) => viewModel.ShutdownBlocking();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
