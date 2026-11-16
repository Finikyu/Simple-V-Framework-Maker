using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace Svfm.App;

public sealed partial class DeveloperView : UserControl
{
    public DeveloperView()
    {
        InitializeComponent();
        this.FindControl<Button>("CopyLog")!.Click += async (_, _) =>
        {
            if (DataContext is DeveloperVm vm && TopLevel.GetTopLevel(this)?.Clipboard is { } cb) { await cb.SetTextAsync(vm.AllText); vm.Note = ""; }
        };
        var box = this.FindControl<TextBox>("LogBox")!;
        box.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) box.CaretIndex = box.Text?.Length ?? 0; };
    }
}
