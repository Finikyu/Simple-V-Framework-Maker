using System.Collections.ObjectModel;
using Svfm.Core.Knowledge;
using Svfm.Core.Project;
using Svfm.Core.VFramework;

namespace Svfm.App;

/// <summary>A game upgrade line in the pick list: its English text. The text ID is never shown.</summary>
public sealed record UpgradeLineChoice(string Id, string Text)
{
    public override string ToString() => Text;
}

/// <summary>
/// Issue #123: the upgrade lines of an item (langPowerUpInfo0..7), on the "Name, text &amp; icon" tab. The lines are
/// the text IDs in <see cref="DevelopRow.UpgradeLineIds"/>; an own line's texts are in the item's texts under
/// "&lt;prefix&gt;_up_N", a game line is an ability_ text of the game.
/// </summary>
public sealed class UpgradeLinesVm : Observable
{
    private readonly Session _s;
    private readonly ItemVm _owner;
    private readonly ProjectItem _item;

    public UpgradeLinesVm(Session s, ItemVm owner, ProjectItem item)
    {
        _s = s; _owner = owner; _item = item;
        AddCommand = new Command(() => Change(l => l.Append(NewOwn())), () => CanAdd);
        Reload();
    }

    public const string NoRowNote = "This item has no R&D row, so it has no grade and no upgrade lines.";
    public const string MaxNote = "The game shows at most 8 upgrade lines.";

    public ObservableCollection<UpgradeLineVm> Lines { get; } = [];
    public Command AddCommand { get; }
    public DevelopVm? Develop => _owner.Develop;
    public bool HasRow => ProjectEdits.DevelopOf(_item) != null;
    public string? Lock => !HasRow ? NoRowNote : _item is ExistingItem x ? ExistingMod.UpgradeLinesLockOf(x) : null;
    public bool Editable => Lock == null;
    public bool Locked => Lock != null;
    /// <summary>A head option has no grade (issue #25), and an item with no R&amp;D row shows a greyed box.</summary>
    public bool GradeShown => Develop?.GradeShown ?? true;
    public bool CanAdd => Editable && Lines.Count < DevelopRow.MaxUpgradeLines;
    public string AddTip => Lines.Count >= DevelopRow.MaxUpgradeLines ? MaxNote : "Add an upgrade line.";

    public static IReadOnlyList<UpgradeLineChoice> GameChoices(Session s) =>
        _choices is { Count: > 0 } c ? c : _choices = [.. GameKnowledge.VanillaUpgradeLines(s.Game).Select(x => new UpgradeLineChoice(x.Id, x.Text))];
    private static List<UpgradeLineChoice>? _choices;

    private IReadOnlyList<string> Ids => ProjectEdits.DevelopOf(_item)?.UpgradeLines ?? [];

    /// <summary>Puts a new own line's empty English text under the first free "&lt;prefix&gt;_up_N" and returns its ID.</summary>
    private string NewOwn() => ProjectEdits.NewLineText(_item.Text, _item.Prefix, new Dictionary<string, string> { ["eng"] = "" });

    /// <summary>Changes the line list as one undo step.</summary>
    internal void Change(Func<IEnumerable<string>, IEnumerable<string>> f, string field = "lines")
    {
        if (!Editable || ProjectEdits.DevelopOf(_item) is not { } d) return;
        _s.Checkpoint($"{_item.Id}:{field}");
        var before = d.UpgradeLines;
        var now = f(before).ToList();
        // The text of an own line that is taken out goes with it, so the lang files keep no orphan.
        foreach (var gone in before.Except(now)) if (IsOwn(gone)) _item.Text.Remove(gone);
        ProjectEdits.SetDevelop(_item, d.WithUpgradeLines(now));
        _s.NotifyChanged();
        Reload();
    }

    internal bool IsOwn(string id) => !id.StartsWith("ability_", StringComparison.Ordinal) && _item.Text.ContainsKey(id);

    internal void Move(UpgradeLineVm line, int by) => Change(l =>
    {
        var list = l.ToList();
        int to = line.Index + by;
        if (to < 0 || to >= list.Count) return list;
        (list[line.Index], list[to]) = (list[to], list[line.Index]);
        return list;
    }, "lines:move");

    internal void Remove(UpgradeLineVm line) => Change(l => l.Where((_, i) => i != line.Index));

    internal void SetKind(UpgradeLineVm line, bool own) => Change(l => l.Select((id, i) => i != line.Index ? id
        : own ? NewOwn() : GameChoices(_s).FirstOrDefault()?.Id ?? id));

    internal void SetGame(UpgradeLineVm line, string id) => Change(l => l.Select((x, i) => i == line.Index ? id : x), $"lines:{line.Index}");

    public void Reload()
    {
        Lines.Clear();
        var ids = Ids;
        for (int i = 0; i < ids.Count; i++) Lines.Add(new UpgradeLineVm(this, _owner, _s, i, ids[i], i == 0, i == ids.Count - 1, IsOwn(ids[i])));
        foreach (var n in new[] { nameof(HasRow), nameof(Lock), nameof(Editable), nameof(Locked), nameof(GradeShown), nameof(CanAdd), nameof(AddTip), nameof(Develop) }) Raise(n);
        AddCommand.Raise();
    }
}

/// <summary>One upgrade line: a game text picked by its English text, or the item's own text with a cell per language.</summary>
public sealed class UpgradeLineVm : Observable
{
    private readonly UpgradeLinesVm _list;
    private readonly Session _s;
    private string _filter = "";

    public UpgradeLineVm(UpgradeLinesVm list, ItemVm owner, Session s, int index, string id, bool first, bool last, bool own)
    {
        _list = list; _s = s; Index = index; Id = id; IsOwn = own;
        Own = own ? new ExtraTextVm(owner, $"Line {index + 1}", id) : null;
        UpCommand = new Command(() => _list.Move(this, -1), () => !first && _list.Editable);
        DownCommand = new Command(() => _list.Move(this, 1), () => !last && _list.Editable);
        RemoveCommand = new Command(() => _list.Remove(this), () => _list.Editable);
    }

    public int Index { get; }
    public string Number => $"{Index + 1}.";
    public string Id { get; }
    public bool IsOwn { get; }
    public bool IsGame => !IsOwn;
    public ExtraTextVm? Own { get; }
    public Command UpCommand { get; }
    public Command DownCommand { get; }
    public Command RemoveCommand { get; }

    public static IReadOnlyList<string> Kinds { get; } = ["Game text", "Own text"];
    public string Kind { get => IsOwn ? Kinds[1] : Kinds[0]; set { if (value != null && value != Kind) _list.SetKind(this, value == Kinds[1]); } }

    public string Filter { get => _filter; set { if (Set(ref _filter, value ?? "")) Raise(nameof(Choices)); } }
    public IReadOnlyList<UpgradeLineChoice> Choices
    {
        get
        {
            var all = UpgradeLinesVm.GameChoices(_s);
            var hit = _filter.Length == 0 ? all : all.Where(c => c.Text.Contains(_filter, StringComparison.OrdinalIgnoreCase)).ToList();
            // The picked line stays in the list, so the filter never clears the pick.
            return Choice is { } c0 && !hit.Contains(c0) ? [c0, .. hit] : hit;
        }
    }
    public UpgradeLineChoice? Choice
    {
        get => IsOwn ? null : UpgradeLinesVm.GameChoices(_s).FirstOrDefault(c => c.Id == Id) ?? new UpgradeLineChoice(Id, "A game text (the game is not open)");
        set { if (value != null && value.Id != Id) _list.SetGame(this, value.Id); }
    }
}
