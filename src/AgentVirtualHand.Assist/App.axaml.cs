using AgentVirtualHand.Assist.ViewModels;
using AgentVirtualHand.Assist.Views;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace AgentVirtualHand.Assist;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var assist = new AssistViewModel();
            desktop.MainWindow = new AssistWindow { DataContext = assist };

            // Zamkniecie okna odcina wszystkich i zatrzymuje link (na watku roboczym, jak host).
            desktop.ShutdownRequested += (_, _) => assist.ShutdownBlocking();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
