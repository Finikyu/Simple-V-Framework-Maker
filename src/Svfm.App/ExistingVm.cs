using System.Collections.ObjectModel;
using Svfm.Core.Project;
using Svfm.Core.VFramework;

namespace Svfm.App;

/// <summary>One language box of an extra text of an existing mod.</summary>
public sealed class TextCell(ExtraTextVm owner, string code, string display) : Observable
{
    public string Display { get; } = display;
    public string Code { get; } = code;
    public string Text { get => owner.Get(Code); set { owner.Set(Code, value); Raise(); Raise(nameof(Tag)); } }
    public string Tag => owner.Has(Code) ? "own text" : "uses English";
    public string TagBrush => owner.Has(Code) ? "#58B07F" : "#7E848C";
    public void RaiseAll() { Raise(nameof(Text)); Raise(nameof(Tag)); Raise(nameof(TagBrush)); }
}

/// <summary>A text of an existing mod beyond its name and description (the real name, a look's name…): English and the other languages.</summary>
public sealed class ExtraTextVm : Observable
{
    private readonly ItemVm _owner;
    public ExtraTextVm(ItemVm owner, string label, string id)
    {
        _owner = owner; Label = label; Id = id;
        foreach (var (_, suffix, display) in ModText.Languages.Where(l => l.package != "eng")) Cells.Add(new TextCell(this, ModText.Package(suffix), display));
    }
    public string Label { get; }
    public string Id { get; }
    public ObservableCollection<TextCell> Cells { get; } = [];
    public string English { get => Get("eng"); set { Set("eng", value); Raise(); } }
    public string Get(string code) => _owner.GetText(Id, code, false);
    public bool Has(string code) => _owner.Has(Id, code);
    public void Set(string code, string value) => _owner.SetText(Id, code, value);
    public void RaiseAll() { Raise(nameof(English)); foreach (var c in Cells) c.RaiseAll(); }
}

/// <summary>One setting of an existing mod's script (an ability, a part of the R&amp;D row).</summary>
public sealed class FieldVm(Session s, ExistingItem item, ExistingField f) : Observable
{
    public string Label => f.Label;
    public bool IsDash => f.Path[^1] == "dashAttack";
    public bool IsBool => f.Type is FieldType.Bool or FieldType.Flag && !IsDash;
    public bool IsNumber => f.Type == FieldType.Number;
    public bool IsText => f.Type == FieldType.Text;
    public bool Editable => f.Locked == null;
    public bool IsLocked => f.Locked != null;
    public string LockedNote => f.Locked == null ? "" : "Can't be changed here: " + f.Locked + ".";
    public string Note => f.Present ? "" : "not set in the script";
    public bool BoolValue { get => f.Value is true; set { Change(value); } }
    public IReadOnlyList<Choice> DashAttacks => Choice.DashAttacks;
    public Choice DashValue { get => Choice.DashOf(f.Value); set { if (value != null) Change(Choice.DashValue(value)); } }
    public double NumberValue { get => (double)f.Value; set { Change(Math.Round(value, 3)); } }
    public string TextValue { get => (string)f.Value; set { Change(value); } }
    public bool Changed => f.Changed;
    public string ChangedBrush => f.Changed ? "#FF8A3D" : "Transparent";

    private void Change(object value)
    {
        if (Equals(f.Value, value)) return;
        s.Checkpoint($"{item.Id}:{f.Group}:{f.Label}");
        f.Value = value;
        s.NotifyChanged();
        RaiseAll();
    }

    public void RaiseAll() { Raise(nameof(BoolValue)); Raise(nameof(DashValue)); Raise(nameof(NumberValue)); Raise(nameof(TextValue)); Raise(nameof(Changed)); Raise(nameof(ChangedBrush)); }
}

public sealed class FieldGroupVm(string title, IEnumerable<FieldVm> fields)
{
    public string Title { get; } = title;
    public ObservableCollection<FieldVm> Fields { get; } = new(fields);
}
