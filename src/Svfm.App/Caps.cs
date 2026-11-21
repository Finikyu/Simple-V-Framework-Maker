using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Svfm.App;

/// <summary>
/// Issue #122: Avalonia has no text-transform, so labels in the iDroid style (button labels, options, tab titles) go
/// through this converter to show in capitals. The bound text itself does not change.
/// </summary>
public static class Caps
{
    public static readonly FuncValueConverter<string?, string?> Upper = new(s => s?.ToUpperInvariant());
}

/// <summary>
/// The content template of a button or an option: a string shows in capitals in the heading font.
/// </summary>
public sealed class CapsLabel : IDataTemplate
{
    /// <summary>
    /// Gives each button with text content this template, at style priority so a template set in a view wins. Not a
    /// style setter: a ContentPresenter with a template gives control content the wrong DataContext.
    /// </summary>
    public static void HookButtons()
    {
        var caps = new CapsLabel();
        ContentControl.ContentProperty.Changed.AddClassHandler<Button>((b, e) =>
        {
            if (b is CheckBox or RadioButton) return; // check box text stays in sentence case, as in the mockup
            if (e.NewValue is string) b.SetValue(ContentControl.ContentTemplateProperty, caps, BindingPriority.Style);
            else if (b.ContentTemplate == caps) b.SetValue(ContentControl.ContentTemplateProperty, null, BindingPriority.Style);
        });
    }

    public bool Match(object? data) => true;

    public Control Build(object? param)
    {
        if (param is Control c) return c;
        if (param is not string text) return new TextBlock { Text = param?.ToString() };
        // A menu label ends in " ▾". The glyph is not in the heading font, and its fallback font has a taller line, which
        // put the label lower than a label without it (Undo). So the arrow is drawn as a shape, in the text colour.
        var menu = text.EndsWith(" ▾");
        var t = new TextBlock { Text = (menu ? text[..^2] : text).ToUpperInvariant(), FontWeight = FontWeight.SemiBold, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        if (Avalonia.Application.Current!.TryFindResource("HeadFont", out var f) && f is FontFamily ff) t.FontFamily = ff;
        if (Avalonia.Application.Current.TryFindResource("HeadSpacing", out var s) && s is double d) t.LetterSpacing = d;
        if (!menu) return t;
        var arrow = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse("M0,0 L8,0 L4,5 Z"), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        arrow.Bind(Avalonia.Controls.Shapes.Shape.FillProperty, t.GetObservable(TextBlock.ForegroundProperty));
        return new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6, Children = { t, arrow } };
    }
}
