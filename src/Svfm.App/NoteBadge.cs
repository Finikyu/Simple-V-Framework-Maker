using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering;

namespace Svfm.App;

/// <summary>
/// The "i" / "!" note badge (issue #122). Its style key is Border, so the <c>Border.info</c> styles in App.axaml give it
/// its colours, but it draws its own shape by note level: blue is an octagon, orange has a 45-degree cut at the top right,
/// red is a square. The click and hover area is the drawn shape. It is a Decorator, not a Border, because
/// <c>Border.Render</c> is sealed; it takes the Border properties that the styles set.
/// </summary>
public class NoteBadge : Decorator, ICustomHitTest
{
    public static readonly StyledProperty<IBrush?> BackgroundProperty = Border.BackgroundProperty.AddOwner<NoteBadge>();
    public static readonly StyledProperty<IBrush?> BorderBrushProperty = Border.BorderBrushProperty.AddOwner<NoteBadge>();
    public static readonly StyledProperty<Thickness> BorderThicknessProperty = Border.BorderThicknessProperty.AddOwner<NoteBadge>();
    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty = Border.CornerRadiusProperty.AddOwner<NoteBadge>();

    static NoteBadge() => AffectsRender<NoteBadge>(BackgroundProperty, BorderBrushProperty);

    public IBrush? Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public IBrush? BorderBrush { get => GetValue(BorderBrushProperty); set => SetValue(BorderBrushProperty, value); }

    protected override Type StyleKeyOverride => typeof(Border);

    public NoteBadge() => Classes.CollectionChanged += (_, _) => InvalidateVisual();

    private Geometry Shape()
    {
        double w = Bounds.Width, h = Bounds.Height, o = 0.5;
        Point[] p = Classes.Contains("bad")
            ? [new(o, o), new(w - o, o), new(w - o, h - o), new(o, h - o)]
            : Classes.Contains("warn")
                ? [new(o, o), new(w - 4, o), new(w - o, 4.5), new(w - o, h - o), new(o, h - o)]
                : [new(w * .1, o), new(w * .9, o), new(w - o, h * .2), new(w - o, h * .8), new(w * .9, h - o),
                   new(w * .1, h - o), new(o, h * .8), new(o, h * .2)];
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(p[0], true);
            foreach (var q in p.Skip(1)) c.LineTo(q);
            c.EndFigure(true);
        }
        return g;
    }

    public override void Render(DrawingContext context) =>
        context.DrawGeometry(Background, BorderBrush is { } b ? new Pen(b, 1) : null, Shape());

    public bool HitTest(Point point) => Shape().FillContains(point);
}
