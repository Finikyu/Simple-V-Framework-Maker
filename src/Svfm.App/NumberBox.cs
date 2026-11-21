using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Svfm.App;

/// <summary>
/// Issue #117: a number field that writes its value only when the user is done. A plain NumericUpDown sets
/// <see cref="NumericUpDown.Value"/> on each key, so each partial value made a checkpoint, refreshed the editor and clamped
/// while the user typed ("0.5" went through "0", which clamped to 0.1). Bind <see cref="Committed"/>, not Value.
/// Enter, Tab or a lost focus commits: the value clamps to the range, and text that is not a number puts back the old value.
/// Esc puts back the old value; a second Esc leaves the field. The spinner and the arrow keys commit at once.
/// </summary>
public sealed class NumberBox : NumericUpDown
{
    public static readonly StyledProperty<decimal?> CommittedProperty =
        AvaloniaProperty.Register<NumberBox, decimal?>(nameof(Committed), defaultBindingMode: BindingMode.TwoWay);

    private TextBox? _box;

    static NumberBox() => CommittedProperty.Changed.AddClassHandler<NumberBox>((n, _) => n.Value = n.Committed);

    protected override Type StyleKeyOverride => typeof(NumericUpDown);

    public decimal? Committed { get => GetValue(CommittedProperty); set => SetValue(CommittedProperty, value); }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (_box != null) _box.LostFocus -= OnBoxLostFocus;
        _box = e.NameScope.Find<TextBox>("PART_TextBox");
        if (_box == null) return;
        _box.LostFocus += OnBoxLostFocus;
        _box.AddHandler(KeyDownEvent, OnBoxKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnSpin(SpinEventArgs e)
    {
        base.OnSpin(e);
        Committed = Value;
    }

    private void OnBoxLostFocus(object? sender, RoutedEventArgs e) => Commit();

    private void OnBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
        else if (e.Key == Key.Escape)
        {
            if (_box!.Text != Shown(Committed)) Revert();
            else this.GetVisualAncestors().OfType<InputElement>().FirstOrDefault(a => a.Focusable && a is not (TextBox or NumericUpDown) && a.Focus());
            e.Handled = true;
        }
    }

    /// <summary>The text the field shows for a value, in the field's own format.</summary>
    private string Shown(decimal? v) => v?.ToString(FormatString, NumberFormat ?? NumberFormatInfo.CurrentInfo) ?? "";

    private void Commit()
    {
        if (_box == null) return;
        if (string.IsNullOrWhiteSpace(_box.Text) || !decimal.TryParse(_box.Text, ParsingNumberStyle, NumberFormat ?? NumberFormatInfo.CurrentInfo, out var v)) { Revert(); return; }
        v = Math.Clamp(v, Minimum, Maximum);
        Committed = v;
        Value = v;
        _box.Text = Shown(v);
    }

    private void Revert()
    {
        Value = Committed;
        _box!.Text = Shown(Committed);
        _box.CaretIndex = _box.Text.Length;
    }
}
