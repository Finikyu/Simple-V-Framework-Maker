using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Svfm.Core.Formats;
using Svfm.Core.Project;

namespace Svfm.App;

/// <summary>One row of the Found screen.</summary>
public sealed class FactVm(FoundFact f)
{
    public string Label => f.Label.ToUpperInvariant();
    public string Title => f.Title;
    public string Detail => f.Detail;
    public bool HasTitle => f.Title.Length > 0;
    public bool HasDetail => f.Detail.Length > 0;
    public string? Mono => f.Mono;
    public bool HasMono => !string.IsNullOrEmpty(f.Mono);
    public IReadOnlyList<ChipVm> Chips { get; } = (f.Chips ?? []).Select(c => new ChipVm(c.text, c.state)).ToList();
    public bool HasChips => Chips.Count > 0;
    public string StatusText => f.Status switch { FactStatus.Certain => "Certain", FactStatus.Fixed => "Fixed automatically", FactStatus.NeedsYou => "Needs you", _ => "Left out" };
    public bool IsOk => f.Status == FactStatus.Certain;
    public bool IsFix => f.Status == FactStatus.Fixed;
    public bool IsBad => f.Status == FactStatus.NeedsYou;
}

/// <summary>One small label on the Found screen. <see cref="Ok"/> is "can wear it", <see cref="No"/> is greyed out
/// "can't", and <see cref="Note"/> is an amber aside about one that can.</summary>
public sealed record ChipVm(string Text, ChipState State)
{
    public bool Ok => State == ChipState.Ok;
    public bool No => State == ChipState.No;
    public bool Note => State == ChipState.Note;
}

/// <summary>One model of the mod in the Models found list; ticking it brings its item in.</summary>
public sealed class ModelVm : Observable
{
    private readonly FoundVm _owner;
    private bool _checked;
    public FoundModel Model { get; }
    // An existing mod keeps every item: its script is edited in place, so its rows have no tick to clear.
    public ModelVm(FoundVm owner, FoundModel m, bool canTick) { _owner = owner; Model = m; _checked = m.ItemId != null; CanTick = canTick && m.ItemId != null; }
    public string Title => Model.Title;
    public string File => Model.File;
    public bool HasFile => Model.File.Length > 0;
    public string Detail => Model.Detail;
    public bool CanTick { get; }
    public bool Checked { get => _checked; set { if (!CanTick || !Set(ref _checked, value)) return; _owner.TickChanged(); } }
    public bool Selected => ReferenceEquals(_owner.Selected, this);
    public void RaiseSelected() => Raise(nameof(Selected));
}

/// <summary>Found: what the tool made of the mod, read-only, and the models it will bring in.</summary>
public sealed class FoundVm : Observable
{
    private readonly MainVm _main;
    private readonly IntakeResult _res;
    private ModelVm? _selected;
    private Bitmap? _picture;

    public FoundVm(MainVm main, IntakeResult res, string fileName)
    {
        _main = main; _res = res; FileName = fileName;
        foreach (var f in res.Facts) Facts.Add(new FactVm(f));
        foreach (var m in res.Models) Models.Add(new ModelVm(this, m, res.Project?.Existing == null));
        Select(Models.FirstOrDefault());
        Continue = new Command(() => Go(), () => CanContinue);
        Back = new Command(() => _main.ShowOpen());
    }

    public string FileName { get; }
    public string Summary
    {
        get
        {
            var names = _res.Project?.Items.Select(i => i.Title).Distinct().Take(2).ToList() ?? [];
            string n = names.Count > 0 ? string.Join(", ", names) : _res.ModName;
            int files = _res.Classification.Files.Count;
            return $"{n} · {_res.ModAuthor} · {files} files · read-only summary, every choice can be changed on the next screen.";
        }
    }
    public string Message => _res.Project == null ? "Nothing in this mod can be ported yet. " + string.Join(" ", _res.Classification.Routes.Take(3)) : "";
    public bool HasMessage => Message.Length > 0;
    public ObservableCollection<FactVm> Facts { get; } = [];
    public ObservableCollection<ModelVm> Models { get; } = [];
    public bool HasModels => Models.Count > 0;
    public ModelVm? Selected { get => _selected; private set { Set(ref _selected, value); } }
    public Bitmap? Picture { get => _picture; private set { Set(ref _picture, value); Raise(nameof(HasPicture)); Raise(nameof(NoPicture)); } }
    public bool HasPicture => _picture != null;
    public bool NoPicture => _picture == null;
    public string PictureName => Selected?.Title ?? "";
    public string PictureNote => Selected == null ? "" : Selected.Model.ItemId != null ? "The model is drawn when the item is opened; this is its icon." : "Not a model this tool carries, so there is no preview. It is left out of the build.";

    public Command Continue { get; }
    public Command Back { get; }
    public Command PickModel => new(o => { if (o is ModelVm m) Select(m); });
    public OpenVm Open => _main.OpenScreen;
    public bool CanContinue => _res.Project != null && Models.Any(m => m.Checked && m.Model.ItemId != null);

    private void Select(ModelVm? m)
    {
        var old = _selected;
        Selected = m; old?.RaiseSelected(); m?.RaiseSelected();
        Picture = null;
        if (m?.Model.ItemId is { } id && _res.Icons.TryGetValue(id, out var img)) { using var ms = new MemoryStream(Png.Encode(img)); Picture = new Bitmap(ms); }
        Raise(nameof(PictureName)); Raise(nameof(PictureNote));
    }

    internal void TickChanged() => Continue.Raise();

    private void Go()
    {
        var drop = Models.Where(m => !m.Checked && m.Model.ItemId != null).Select(m => m.Model.ItemId!).ToHashSet();
        var p = _main.Session.Project;
        if (p != null) foreach (var item in p.Items.Where(i => drop.Contains(i.Id)).ToList()) p.Items.Remove(item);
        _main.ProjectOpened();
    }
}
