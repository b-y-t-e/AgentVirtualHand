using AgentVirtualHand.ViewModels;
using AgentVirtualHand.Views;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace AgentVirtualHand;

public partial class App : Application
{
    /// <summary>Tryb "assist": minimalny ekran, kod od razu, dostep do konca dzialania aplikacji.</summary>
    public static bool AssistMode { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (AssistMode)
            {
                var assist = new AssistViewModel();
                desktop.MainWindow = new AssistWindow { DataContext = assist };
                desktop.ShutdownRequested += (_, _) => assist.ShutdownBlocking();
            }
            else
            {
                var viewModel = new MainViewModel();
                desktop.MainWindow = new MainWindow { DataContext = viewModel };

                // Zamknięcie okna musi odciąć dostęp. Blokowanie wątku UI na async zatrzymaniu
                // serwera zakleszcza się (kontynuacje wracają na dyspozytora UI), dlatego czekanie
                // odbywa się na wątku roboczym - patrz MainViewModel.ShutdownBlocking.
                desktop.ShutdownRequested += (_, _) => viewModel.ShutdownBlocking();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
