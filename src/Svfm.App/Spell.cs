using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Svfm.Core.Text;

namespace Svfm.App;

/// <summary>
/// Issue #135: the spell check of long text. <c>app:Spell.Check="True"</c> on a TextBox draws a red wavy line under each
/// misspelled word and puts corrections and "Add to dictionary" in its right-click menu. The language is the UK/US switch
/// in the top bar (<see cref="Settings.SpellLanguage"/>).
/// </summary>
public static class Spell
{
    public static readonly AttachedProperty<bool> CheckProperty = AvaloniaProperty.RegisterAttached<TextBox, bool>("Check", typeof(Spell));
    public static bool GetCheck(TextBox t) => t.GetValue(CheckProperty);
    public static void SetCheck(TextBox t, bool v) => t.SetValue(CheckProperty, v);

    private static Settings _settings = new();
    private static HashSet<string> _userWords = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The language or the added words changed: the boxes check their text again.</summary>
    public static event Action? Changed;

    static Spell() => CheckProperty.Changed.AddClassHandler<TextBox>((t, e) => { if (e.NewValue is true) Attach(t); });

    public static SpellLanguage Language => _settings.SpellLanguage == "US" ? SpellLanguage.US : SpellLanguage.UK;

    /// <summary>Reads the language and the added words, and loads the dictionary in the background.</summary>
    public static void Init(Settings settings)
    {
        _settings = settings;
        _userWords = new(settings.SpellWords, StringComparer.OrdinalIgnoreCase);
        var lang = Language;
        Task.Run(() => SpellChecker.For(lang));
    }

    public static void SetLanguage(SpellLanguage lang)
    {
        if (lang == Language) return;
        _settings.SpellLanguage = lang.ToString();
        _settings.Save();
        Changed?.Invoke();
    }

    private static void AddWord(string word)
    {
        if (!_userWords.Add(word)) return;
        _settings.SpellWords.Add(word);
        _settings.Save();
        Changed?.Invoke();
    }

    private static void Attach(TextBox box)
    {
        var lines = new SpellLines(box);
        AdornerLayer.SetAdorner(box, lines);
        box.TemplateApplied += (_, e) =>
        {
            lines.Presenter = e.NameScope.Find<TextPresenter>("PART_TextPresenter");
            lines.Viewer = e.NameScope.Find<ScrollViewer>("PART_ScrollViewer");
            if (lines.Viewer != null) lines.Viewer.ScrollChanged += (_, _) => lines.InvalidateVisual();
        };
        box.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) lines.Recheck();
            else if (e.Property == TextBox.CaretIndexProperty || e.Property == Visual.BoundsProperty || e.Property == InputElement.IsFocusedProperty) lines.InvalidateVisual();
        };
        box.AttachedToVisualTree += (_, _) => { Changed += lines.Recheck; lines.Recheck(); };
        box.DetachedFromVisualTree += (_, _) => Changed -= lines.Recheck;

        // The menu: corrections of the word under the pointer, then the usual Cut, Copy and Paste.
        int menuIndex = -1;
        box.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(box).Properties.IsRightButtonPressed && lines.Presenter?.TextLayout is { } layout)
                menuIndex = layout.HitTestPoint(e.GetPosition(lines.Presenter)).TextPosition;
        }, RoutingStrategies.Tunnel);
        var menu = new MenuFlyout();
        menu.Opening += (_, _) =>
        {
            int at = menuIndex >= 0 ? menuIndex : box.CaretIndex;
            menuIndex = -1;
            menu.Items.Clear();
            var bad = lines.Bad.FirstOrDefault(b => at >= b.Start && at <= b.Start + b.Length);
            if (bad.Length > 0 && box.Text is { } text && bad.Start + bad.Length <= text.Length)
            {
                string word = text.Substring(bad.Start, bad.Length);
                var fixes = SpellChecker.For(Language).Suggest(word);
                foreach (var fix in fixes)
                {
                    var item = new MenuItem { Header = fix, FontWeight = FontWeight.SemiBold };
                    item.Click += (_, _) =>
                    {
                        string now = box.Text ?? "";
                        if (bad.Start + bad.Length > now.Length || now.Substring(bad.Start, bad.Length) != word) return;
                        box.Text = now[..bad.Start] + fix + now[(bad.Start + bad.Length)..];
                        box.CaretIndex = bad.Start + fix.Length;
                    };
                    menu.Items.Add(item);
                }
                if (fixes.Count == 0) menu.Items.Add(new MenuItem { Header = "No suggestions", IsEnabled = false });
                var add = new MenuItem { Header = "Add to dictionary" };
                add.Click += (_, _) => AddWord(word);
                menu.Items.Add(add);
                menu.Items.Add(new Separator());
            }
            menu.Items.Add(Command("Cut", box.Cut, box.CanCut));
            menu.Items.Add(Command("Copy", box.Copy, box.CanCopy));
            menu.Items.Add(Command("Paste", box.Paste, box.CanPaste));
        };
        box.ContextFlyout = menu;
    }

    /// <summary>The misspelled words of a checked box, as start and length (the UI checks read it).</summary>
    public static IReadOnlyList<(int Start, int Length)> Misspelled(TextBox box) => AdornerLayer.GetAdorner(box) is SpellLines l ? l.Bad : [];

    private static MenuItem Command(string header, Action run, bool enabled)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => run();
        return item;
    }

    /// <summary>The red wavy lines of one TextBox, drawn in the adorner layer over it.</summary>
    private sealed class SpellLines : Control
    {
        private static readonly Pen Wave = new(new SolidColorBrush(Color.FromRgb(0xE0, 0x50, 0x50)), 1);
        private readonly TextBox _box;
        public TextPresenter? Presenter;
        public ScrollViewer? Viewer;
        public List<(int Start, int Length)> Bad { get; private set; } = [];

        public SpellLines(TextBox box) { _box = box; IsHitTestVisible = false; }

        public void Recheck()
        {
            Bad = SpellChecker.For(Language).Misspelled(_box.Text, _userWords);
            InvalidateVisual();
        }

        public override void Render(DrawingContext ctx)
        {
            if (Presenter?.TextLayout is not { } layout || Bad.Count == 0) return;
            if (Presenter.TranslatePoint(default, this) is not { } origin) return;
            // Only the visible part of the box: text scrolled out of view has no line.
            Rect clip = new(Bounds.Size);
            if (Viewer != null && Viewer.TranslatePoint(default, this) is { } v) clip = new Rect(v, Viewer.Bounds.Size);
            using var _ = ctx.PushClip(clip);
            foreach (var (start, length) in Bad)
            {
                // The word being typed has no line yet.
                if (_box.IsFocused && _box.CaretIndex == start + length) continue;
                foreach (var r in layout.HitTestTextRange(start, length))
                    DrawWave(ctx, origin.X + r.X, origin.X + r.Right, origin.Y + r.Bottom - 1.5);
            }
        }

        private static void DrawWave(DrawingContext ctx, double x0, double x1, double y)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(x0, y), false);
                bool up = true;
                for (double x = x0 + 2; x <= x1; x += 2, up = !up) c.LineTo(new Point(x, up ? y - 1.5 : y));
                c.EndFigure(false);
            }
            ctx.DrawGeometry(null, Wave, g);
        }
    }
}
