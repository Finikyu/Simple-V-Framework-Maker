using System.Collections.ObjectModel;
using Svfm.Core.Project;
using Svfm.Core.VFramework;

namespace Svfm.App;

/// <summary>One character a head option is worn by.</summary>
public sealed class HeadWearRow(HeadVm h, PlayerBranch who) : Observable
{
    public PlayerBranch Who { get; } = who;
    public string Label { get; } = PlayerBranches.Label(who);
    public bool Wears { get => h.Wears(Who); set { h.SetWears(Who, value); Raise(); } }
    /// <summary>True for the only ticked wearer: it can't be unticked (issue #15).</summary>
    public bool Locked => PlayerBranches.IsLastWearer(Wears, h.WearerCount);
    public bool CanTick => !Locked;
    public string Note => Locked ? PlayerBranches.LastHeadWearerReason : "";
    public void Reload() { Raise(nameof(Wears)); Raise(nameof(Locked)); Raise(nameof(CanTick)); Raise(nameof(Note)); }
}

/// <summary>A character's face for a head option, as plain text.</summary>
public sealed record FaceRow(string Who, string Detail);

/// <summary>"Worn with" and "Who &amp; faces" for a head option.</summary>
public sealed class HeadVm : Observable
{
    private readonly Session _s;
    private readonly HeadOptionItem _h;
    private readonly ProjectItem _owner;
    /// <summary><paramref name="owner"/> is the item the edits keep: the head option, or the existing item whose model it is.</summary>
    public HeadVm(Session s, HeadOptionItem h, ProjectItem? owner = null)
    {
        _s = s; _h = h; _owner = owner ?? h;
        foreach (var k in Original.Keys.Union(_h.Spec.Branches.Keys).OrderBy(k => k)) Wearers.Add(new HeadWearRow(this, k));
        ReloadFaces();
    }

    private IReadOnlyDictionary<PlayerBranch, HeadBranch> Original => _s.Edits?.OriginalOf(_owner)?.Spec?.Heads ?? _h.Spec.Branches;
    public ObservableCollection<HeadWearRow> Wearers { get; } = [];
    public ObservableCollection<FaceRow> Faces { get; } = [];
    public bool OnlyOne => _h.Spec.Branches.Count < 2;
    public int WearerCount => _h.Spec.Branches.Count;
    public string SnakeNote => _h.Spec.AvatarWouldWearSnakeFace ? "This head is Snake's own face (bandana faces, accessory heads or a face mod). V Framework 1.7 has Avatar read Snake's branch, so the build gives Avatar a branch without it." : "";

    public string? NoAvatar => _h.Spec.NoAvatarReason;

    /// <summary>Issue #126: a second head option, this head with the game's bandana on top, for Snake (<see cref="BandanaCombo"/>).</summary>
    /// Issue #134: the head option of an existing V Framework mod takes one too (owner, Nov 21: "an Add bandana button").
    public bool CanBandanaCombo => _s.Game != null && _s.Project?.Items.Contains(_owner) == true && _h.Spec.Branches.ContainsKey(PlayerBranch.Snake)
        && BandanaCombo.HeadOf(_h.Id) == null && !GatedBandanas.IsGated(_h);
    public bool BandanaComboOn
    {
        get => _s.Project != null && BandanaCombo.Of(_s.Project, _h, _infiniteCopy) != null;
        set
        {
            if (value == BandanaComboOn || !CanBandanaCombo) return;
            _s.Checkpoint();
            BandanaError = "";
            try { if (value) BandanaCombo.Add(_s.Game!, _s.Project!, _h, _infiniteCopy); else BandanaCombo.Remove(_s.Project!, _h, _infiniteCopy); }
            catch (Exception e) when (e is InvalidOperationException or System.IO.InvalidDataException) { DevLog.Info("edit", "bandana combo: " + e.Message); BandanaError = "Could not add the bandana: " + e.Message; }
            _s.NotifyChanged();
            Raise(); Raise(nameof(BandanaOffOn));
        }
    }
    /// <summary>
    /// Owner, Nov 22: "Infinite bandana" next to "Add Bandana Copy" selects the copy that the buttons add and remove: the
    /// infinite copy (on) or the plain copy (off). Each copy is added and removed on its own.
    /// </summary>
    public bool InfiniteCopy
    {
        get => _infiniteCopy;
        set
        {
            if (value == _infiniteCopy) return;
            _infiniteCopy = value;
            Raise(); Raise(nameof(BandanaComboOn)); Raise(nameof(BandanaOffOn));
        }
    }
    private bool _infiniteCopy;
    /// <summary>True when the head can take the combo and has none yet: the "Add bandana" button shows.</summary>
    public bool BandanaOffOn => CanBandanaCombo && !BandanaComboOn;
    public Command AddBandana => _addBandana ??= new Command(() => BandanaComboOn = true);
    public Command RemoveBandana => _removeBandana ??= new Command(() => BandanaComboOn = false);
    private Command? _addBandana, _removeBandana;
    private string _bandanaError = "";
    public string BandanaError { get => _bandanaError; private set => Set(ref _bandanaError, value); }

    public bool Wears(PlayerBranch who) => _h.Spec.Branches.ContainsKey(who);
    public void SetWears(PlayerBranch who, bool on)
    {
        if (on == Wears(who)) return;
        if (!on && _h.Spec.Branches.Count < 2) { foreach (var w in Wearers) w.Reload(); return; }
        _s.Checkpoint($"{_owner.Id}:headwear:{who}");
        if (on) { if (Original.TryGetValue(who, out var b)) _h.Spec.Branches[who] = b; } else _h.Spec.Branches.Remove(who);
        _s.NotifyChanged();
        foreach (var w in Wearers) w.Reload();
        ReloadFaces(); Raise(nameof(OnlyOne));
    }

    public void Reload() { foreach (var w in Wearers) w.Reload(); ReloadFaces(); Raise(nameof(OnlyOne)); Raise(nameof(BandanaComboOn)); Raise(nameof(BandanaOffOn)); }

    private void ReloadFaces()
    {
        Faces.Clear();
        foreach (var (who, b) in _h.Spec.Branches.OrderBy(k => k.Key))
        {
            var parts = new List<string>();
            if (b.Fv2 != null) parts.Add(System.IO.Path.GetFileName(b.Fv2));
            foreach (var (stage, f) in b.FaceStages.OrderBy(k => k.Key))
                parts.Add($"{stage switch { 1 => "Normal", 2 => "Grown horn", 3 => "Demon", _ => "Stage " + stage }}: {System.IO.Path.GetFileName(f.fv2)}");
            if (b.EnemyFaceId != null) parts.Add("soldier face " + b.EnemyFaceId.Replace("TppEnemyFaceId.", ""));
            Faces.Add(new FaceRow(PlayerBranches.Label(who), parts.Count == 0 ? "head model from the mod" : string.Join("  ·  ", parts)));
        }
    }
}
