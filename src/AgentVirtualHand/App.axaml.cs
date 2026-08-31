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

            // Zamknięcie okna musi odciąć dostęp - inaczej serwer żyje dalej w tle.
            desktop.ShutdownRequested += (_, _) => viewModel.ShutdownAsync().GetAwaiter().GetResult();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
