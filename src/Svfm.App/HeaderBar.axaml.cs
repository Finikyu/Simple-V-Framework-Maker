using Avalonia;
using Avalonia.Controls;

namespace Svfm.App;

/// <summary>The top bar of every screen: the logo, the name, the mod or file the screen is about, and the screen's own buttons.</summary>
public sealed partial class HeaderBar : UserControl
{
    public static readonly StyledProperty<string?> CrumbProperty = AvaloniaProperty.Register<HeaderBar, string?>(nameof(Crumb));
    public static readonly StyledProperty<object?> ActionsProperty = AvaloniaProperty.Register<HeaderBar, object?>(nameof(Actions));

    public HeaderBar()
    {
        InitializeComponent();
        CrumbProperty.Changed.AddClassHandler<HeaderBar>((h, _) => h.Refresh());
        ActionsProperty.Changed.AddClassHandler<HeaderBar>((h, _) => h.ActionsHost.Content = h.Actions);
    }

    public string? Crumb { get => GetValue(CrumbProperty); set => SetValue(CrumbProperty, value); }
    public object? Actions { get => GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }

    private void Refresh()
    {
        CrumbText.Text = Crumb;
        Slash.IsVisible = !string.IsNullOrEmpty(Crumb);
    }
}
