using Avalonia;
using Avalonia.Controls;

namespace Svfm.App;

/// <summary>The top bar of every screen: the logo, the name, the mod or file the screen is about, and the screen's own buttons.</summary>
public sealed partial class HeaderBar : UserControl
{
    public static readonly StyledProperty<string?> CrumbProperty = AvaloniaProperty.Register<HeaderBar, string?>(nameof(Crumb));
    public static readonly StyledProperty<object?> ActionsProperty = AvaloniaProperty.Register<HeaderBar, object?>(nameof(Actions));
    public static readonly StyledProperty<System.Windows.Input.ICommand?> BackProperty = AvaloniaProperty.Register<HeaderBar, System.Windows.Input.ICommand?>(nameof(Back));
    public static readonly StyledProperty<string?> BackTipProperty = AvaloniaProperty.Register<HeaderBar, string?>(nameof(BackTip));
    public static readonly StyledProperty<int> StepProperty = AvaloniaProperty.Register<HeaderBar, int>(nameof(Step), -1);

    public HeaderBar()
    {
        InitializeComponent();
        CrumbProperty.Changed.AddClassHandler<HeaderBar>((h, _) => h.Refresh());
        ActionsProperty.Changed.AddClassHandler<HeaderBar>((h, _) => h.ActionsHost.Content = h.Actions);
        BackProperty.Changed.AddClassHandler<HeaderBar>((h, _) => { h.BackButton.Command = h.Back; h.BackButton.IsVisible = h.Back != null; });
        BackTipProperty.Changed.AddClassHandler<HeaderBar>((h, _) => ToolTip.SetTip(h.BackButton, h.BackTip));
        StepProperty.Changed.AddClassHandler<HeaderBar>((h, _) => h.Steps.Step = h.Step);
    }

    public string? Crumb { get => GetValue(CrumbProperty); set => SetValue(CrumbProperty, value); }
    public object? Actions { get => GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }
    /// <summary>Issue #122 (N2): the screen's Back command; the Back button shows only when it is set.</summary>
    public System.Windows.Input.ICommand? Back { get => GetValue(BackProperty); set => SetValue(BackProperty, value); }
    public string? BackTip { get => GetValue(BackTipProperty); set => SetValue(BackTipProperty, value); }
    /// <summary>The step of the screen in the step bar: 0 Open, 1 Found.</summary>
    public int Step { get => GetValue(StepProperty); set => SetValue(StepProperty, value); }

    private void Refresh()
    {
        CrumbText.Text = Crumb;
        Slash.IsVisible = !string.IsNullOrEmpty(Crumb);
    }
}
