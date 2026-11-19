using AgentVirtualHand.Assist.ViewModels;
using AgentVirtualHand.Services;
using AgentVirtualHand.Assist.Views;
using Avalonia;
using Avalonia.Controls;
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
            var updates = new AppUpdates("assist");
            var assist = new AssistViewModel();
            assist.AttachUpdates(updates);
            desktop.MainWindow = new AssistWindow
            {
                DataContext = assist,
                WindowState = Program.StartMinimized ? WindowState.Minimized : WindowState.Normal,
                ShowActivated = !Program.StartMinimized,
            };

            // Zamkniecie okna odcina wszystkich i zatrzymuje link (na watku roboczym, jak host).
            desktop.ShutdownRequested += (_, _) => assist.ShutdownBlocking();

            updates.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
