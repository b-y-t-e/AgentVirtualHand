using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace AgentVirtualHand.Assist.Views;

public partial class AssistWindow : Window
{
    public AssistWindow() => AvaloniaXamlLoader.Load(this);
}
