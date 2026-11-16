using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.LogicalTree;

namespace Svfm.App;

public sealed partial class EditorView : UserControl
{
    private Point? _last;
    private bool _panning;

    public EditorView()
    {
        InitializeComponent();
        // Issue #62: Ctrl or Shift picks several Sortie rows; Clone and Remove act on all of them.
        if (this.FindControl<ListBox>("SortieList") is { } sortie)
            sortie.SelectionChanged += (_, _) =>
            {
                if (sortie.DataContext is SortieVm vm) vm.Chosen = [.. sortie.SelectedItems!.OfType<SortieRow>()];
            };
        // The owner asked for the preview to be driven by the mouse: "3D preview should be able to be controlled by the
        // mouse, so mouse wheel to zoom in, click and hold to spin". Both boxes get the same handlers, because a weapon
        // is drawn in the short box and an outfit in the tall one.
        //
        // A right-drag (or Shift + left-drag) slides the model instead of turning it, which is how the owner asked to
        // reach the far end of a long model: "We also need a way to drag the model in the 3D viewer so we can look at
        // different parts of the model."
        foreach (var name in new[] { "StillBox", "WeaponBox" })
            if (this.FindControl<Control>(name) is { } box)
            {
                box.PointerPressed += (s, e) =>
                {
                    if (Preview is not { } p) return;
                    var pt = e.GetCurrentPoint(box);
                    bool right = pt.Properties.IsRightButtonPressed;
                    bool shiftLeft = pt.Properties.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                    if (!right && !shiftLeft && !pt.Properties.IsLeftButtonPressed) return;
                    _panning = right || shiftLeft;
                    _last = e.GetPosition(box);
                    if (!_panning) p.BeginDrag();
                    e.Pointer.Capture(box); box.Focus();
                };
                box.PointerMoved += (s, e) =>
                {
                    if (_last is not { } l || Preview is not { } p) return;
                    var now = e.GetPosition(box);
                    // The box size goes with the drag so a full-width drag is always half a turn, whatever the box is.
                    if (_panning) p.DragPanBy(now.X - l.X, now.Y - l.Y);
                    else p.DragBy(now.X - l.X, now.Y - l.Y, box.Bounds.Width, box.Bounds.Height);
                    _last = now;
                };
                box.PointerReleased += (s, e) =>
                {
                    if (_last == null) return;
                    _last = null;
                    if (!_panning) Preview?.EndDrag();
                    _panning = false;
                    e.Pointer.Capture(null);
                };
                box.PointerWheelChanged += (s, e) =>
                {
                    if (Preview is not { } p) return;
                    p.ZoomBy(e.Delta.Y > 0 ? 1 : -1);
                    e.Handled = true;
                };
                box.KeyDown += (s, e) =>
                {
                    if (Preview is not { } p) return;
                    if (e.Key == Key.Left) { p.Yaw -= 15; e.Handled = true; }
                    else if (e.Key == Key.Right) { p.Yaw += 15; e.Handled = true; }
                    else if (e.Key == Key.Up) { p.Pitch += 10; e.Handled = true; }
                    else if (e.Key == Key.Down) { p.Pitch -= 10; e.Handled = true; }
                    else if (e.Key == Key.Home) { p.Yaw = 0; p.Pitch = 0; e.Handled = true; }
                };
            }
    }

    private PreviewVm? Preview => (DataContext as EditorVm)?.Preview;

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Preview?.FitWidth(e.NewSize.Width);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Bounds.Width > 0) Preview?.FitWidth(Bounds.Width);
    }

    // A click on a row of an item drop-down picks the item and closes the list. A right-click does not tap, so the list
    // stays open for the row's Duplicate and Remove menu.
    private void Row_Tapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control c && c.FindLogicalAncestorOfType<Popup>() is { } popup) popup.IsOpen = false;
    }

    // The right-click menu of a row acts on that row, so select it first (the menu's commands act on the selected item).
    private void Row_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is Control { DataContext: ItemVm row } && DataContext is EditorVm ed) ed.Selected = row;
    }

    // ---- Dragging sortie slots into order ----
    // The owner: "Sortie slot is completely wrong, misses the drag features". The Up/Down buttons stay, but a row can now
    // be dragged onto another row to take its place, which is how the mock-up worked.
    private LookRow? _dragLook;

    private void LookRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: LookRow row } && e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
            _dragLook = row;
    }

    private void LookRow_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragLook == null || sender is not Control { DataContext: LookRow row } c) return;
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) { _dragLook = null; return; }
        if (ReferenceEquals(row, _dragLook)) return;
        // Dropped on another row: move the dragged one to that row's place.
        if (DataContext is EditorVm { Selected.Outfit: { } outfit })
        {
            outfit.MoveLookTo(_dragLook.Index, row.Index);
            _dragLook = null;
        }
    }

    private void LookRow_PointerReleased(object? sender, PointerReleasedEventArgs e) => _dragLook = null;
}
