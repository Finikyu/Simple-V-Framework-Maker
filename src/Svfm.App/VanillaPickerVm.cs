namespace Svfm.App;

/// <summary>
/// Issue #109: a picker for a vanilla R&amp;D row, with a search box and one collapsible section per kind
/// (<see cref="VanillaRowChoice.KindLabel"/>). The section of the item's own kind is open first. A search opens the
/// sections with a match and hides the others; an empty search puts back the first state. Used by the R&amp;D card
/// "Copy R&amp;D stats from a vanilla item" and by "Follows a vanilla development".
/// </summary>
public sealed class VanillaPickerVm : Observable
{
    private readonly Func<VanillaRowChoice?> _get;
    private readonly Action<VanillaRowChoice> _set;

    /// <param name="ownKinds">The item's kind codes, its own first (DevelopVm.OwnKinds).</param>
    public VanillaPickerVm(string[] ownKinds, Func<VanillaRowChoice?> get, Action<VanillaRowChoice> set)
    {
        _get = get; _set = set;
        var ownLabels = ownKinds.Select(k => VanillaRowChoice.LabelOf(k)).Distinct().ToList();
        var openLabel = ownLabels.FirstOrDefault();
        Groups = DevelopVm.VanillaRows.GroupBy(r => r.KindLabel)
            .OrderBy(g => ownLabels.IndexOf(g.Key) is var i && i >= 0 ? i : ownLabels.Count).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new VanillaGroupVm(this, g.Key, g.Key == openLabel, g.OrderBy(r => r.Label, StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();
    }

    public IReadOnlyList<VanillaGroupVm> Groups { get; }
    /// <summary>The first row in the list order: the default for a new follow.</summary>
    public VanillaRowChoice? First => Groups.SelectMany(g => g.All).FirstOrDefault();

    private string _search = "";
    public string Search
    {
        get => _search;
        set
        {
            if (!Set(ref _search, value ?? "")) return;
            var q = _search.Trim();
            foreach (var g in Groups) g.Filter(q);
        }
    }

    public VanillaRowChoice? Selected
    {
        get => _get();
        set { if (value != null && value != _get()) _set(value); RaiseSelected(); }
    }
    /// <summary>Tells the sections that the pick changed (also after an undo).</summary>
    public void RaiseSelected() { Raise(nameof(Selected)); foreach (var g in Groups) g.RaiseSelected(); }
}

/// <summary>One kind section of a <see cref="VanillaPickerVm"/>: its header with the row count, and its rows.</summary>
public sealed class VanillaGroupVm(VanillaPickerVm picker, string kind, bool own, List<VanillaRowChoice> all) : Observable
{
    public string Kind { get; } = kind;
    public bool Own { get; } = own;
    public IReadOnlyList<VanillaRowChoice> All { get; } = all;
    public IReadOnlyList<VanillaRowChoice> Rows { get; private set; } = all;
    public string Header => $"{Kind} ({Rows.Count})";
    private bool _open = own;
    public bool IsOpen { get => _open; set => Set(ref _open, value); }
    public bool IsShown => Rows.Count > 0;

    internal void Filter(string q)
    {
        Rows = q.Length == 0 ? All : All.Where(r => r.Label.Contains(q, StringComparison.OrdinalIgnoreCase) || Kind.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Id.ToString() == q).ToList();
        IsOpen = q.Length == 0 ? Own : Rows.Count > 0;
        Raise(nameof(Rows)); Raise(nameof(Header)); Raise(nameof(IsShown)); RaiseSelected();
    }

    /// <summary>The picked row when it is in this section, else null (each section is its own list box).</summary>
    public VanillaRowChoice? Selected
    {
        get => picker.Selected is { } s && Rows.Contains(s) ? s : null;
        set { if (value != null) picker.Selected = value; }
    }
    internal void RaiseSelected() => Raise(nameof(Selected));
}
