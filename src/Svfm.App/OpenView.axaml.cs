using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace Svfm.App;

public sealed partial class OpenView : UserControl
{
    public OpenView()
    {
        InitializeComponent();
        AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(this, true);
        Loaded += (_, _) => { if (this.FindControl<Grid>("DropZone") is { } z) z.PointerPressed += (_, _) => { if (DataContext is OpenVm vm && vm.NotBusy) vm.ChooseMod.Execute(null); }; };
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var file = e.DataTransfer.TryGetFiles()?.FirstOrDefault()?.TryGetLocalPath();
        if (file != null && DataContext is OpenVm vm) await vm.LoadAsync(file);
    }
}
