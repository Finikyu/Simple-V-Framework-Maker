using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Svfm.App;

/// <summary>
/// Issue #122 (N1): the steps "Open > Found > Editor > Build" in the header. The current step is amber. A done step that
/// the app can go back to (Open from Found, Editor from Build) is a link; the other done steps are plain text.
/// </summary>
public sealed class StepBar : StackPanel
{
    public static readonly StyledProperty<int> StepProperty = AvaloniaProperty.Register<StepBar, int>(nameof(Step), -1);
    private static readonly string[] Names = ["Open", "Found", "Editor", "Build"];

    static StepBar() => StepProperty.Changed.AddClassHandler<StepBar>((s, _) => s.Rebuild());

    public StepBar() { Orientation = Orientation.Horizontal; Spacing = 6; VerticalAlignment = VerticalAlignment.Center; }

    public int Step { get => GetValue(StepProperty); set => SetValue(StepProperty, value); }

    private T Res<T>(string key) => (T)Application.Current!.FindResource(key)!;

    private void Rebuild()
    {
        Children.Clear();
        if (Step < 0) return;
        for (int i = 0; i < Names.Length; i++)
        {
            if (i > 0) Children.Add(Text("›", Res<IBrush>("FaintTextBrush")));
            Action? back = (Step, i) switch
            {
                (1, 0) => () => Main?.ShowOpen(),
                (3, 2) => () => Main?.ShowEditor(),
                _ => null,
            };
            if (back != null)
            {
                var b = new Button { Name = "Step" + Names[i], Content = Names[i], Classes = { "step" }, VerticalAlignment = VerticalAlignment.Center, MinHeight = 0 };
                ToolTip.SetTip(b, "Back to " + Names[i].ToLowerInvariant() + " (Esc)");
                b.Click += (_, _) => back();
                Children.Add(b);
            }
            else Children.Add(Text(Names[i], i == Step ? Res<IBrush>("AmberBrush") : i < Step ? Res<IBrush>("MutedBrush") : Res<IBrush>("FaintTextBrush")));
        }
    }

    private MainVm? Main => TopLevel.GetTopLevel(this)?.DataContext as MainVm;

    private TextBlock Text(string s, IBrush fg) => new()
    {
        Text = s.ToUpperInvariant(), Foreground = fg, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold,
        FontFamily = Res<FontFamily>("HeadFont"), LetterSpacing = Res<double>("HeadSpacing"),
    };
}
