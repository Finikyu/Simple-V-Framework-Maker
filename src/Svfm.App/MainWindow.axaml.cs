using Svfm.Core.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;

namespace Svfm.App;

public sealed partial class MainWindow : Window, IDialogs
{
    private readonly MainVm _vm;

    public MainWindow() : this(new Session(Settings.Load())) { }

    public MainWindow(Session session)
    {
        Spell.Init(session.Settings);
        InitializeComponent();
        Title = $"Simple V Framework Maker v{typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}"; // #74: the version is in the title
        DataContext = _vm = new MainVm(session, this);
        Focusable = true; // #117: Esc in a number field moves the focus here, so the window shortcuts work again
        KeyDown += OnKeyDown;
        var search = this.FindControl<TextBox>("FieldSearchBox")!;
        search.AttachedToVisualTree += (_, _) => search.Focus();
        search.KeyDown += (_, e) => { if (e.Key == Key.Enter) { _vm.Editor?.RunFieldSearch(); e.Handled = true; } };
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) => { e.Handled = true; _vm.ReportError(e.Exception); };
        Closing += OnClosing;
        // Issue #135: File > Spelling, UK or US English.
        var uk = this.FindControl<MenuItem>("SpellUkItem")!; var us = this.FindControl<MenuItem>("SpellUsItem")!;
        void ShowSpelling() { uk.IsChecked = Spell.Language == SpellLanguage.UK; us.IsChecked = !uk.IsChecked; }
        ShowSpelling();
        uk.Click += (_, _) => { Spell.SetLanguage(SpellLanguage.UK); ShowSpelling(); };
        us.Click += (_, _) => { Spell.SetLanguage(SpellLanguage.US); ShowSpelling(); };
    }

    /// <summary>Asks before the window closes with unsaved changes (a close from code, such as the one after "yes", goes through).</summary>
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (e.IsProgrammatic || !_vm.Session.Dirty) return;
        e.Cancel = true;
        if (await Confirm("Close Simple V Framework Maker?", "This project has unsaved changes. Closing leaves them behind.", "Close", "Keep working"))
            Close();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        // Issue #117: an editable field keeps its keys (digits, Ctrl+Z, Esc), also when the focus is on a part inside it.
        bool typing = FocusManager?.GetFocusedElement() is Visual f && f.GetSelfAndVisualAncestors().Any(a => a is TextBox or NumericUpDown or ComboBox { IsEditable: true });
        if (e.Key == Key.F12) { _vm.ToggleDev.Execute(null); e.Handled = true; }
        else if (ctrl && e.Key == Key.F && _vm.CanSearch && this.FindControl<Button>("SearchButton") is { } search) { search.Flyout!.ShowAt(search); e.Handled = true; }
        else if (ctrl && e.Key == Key.Z && !typing) { _vm.UndoCommand.Execute(null); e.Handled = true; }
        else if (ctrl && e.Key == Key.S) { _vm.SaveCommand.Execute(null); e.Handled = true; }
        else if (ctrl && e.Key == Key.O) { _vm.OpenProjectCommand.Execute(null); e.Handled = true; }
        else if (ctrl && e.Key == Key.OemComma) { _vm.OpenSettingsCommand.Execute(null); e.Handled = true; }
        else if (!ctrl && !typing && e.Key is >= Key.D1 and <= Key.D9 && _vm.Editor is { } edn && _vm.Screen == edn) { edn.GoTab(e.Key - Key.D0); e.Handled = true; }
        else if (!typing && e.Key == Key.Escape && _vm.Screen is BuildVm) { _vm.ShowEditor(); e.Handled = true; }
        else if (!typing && e.Key == Key.Escape && _vm.Screen is FoundVm found) { found.Back.Execute(null); e.Handled = true; }
        else if (!typing && e.Key == Key.Escape && _vm.Editor is { Confirming: true } ed) { ed.Confirming = false; e.Handled = true; }
    }

    public async Task<string?> PickFile(string title, params (string name, string[] patterns)[] types)
    {
        var r = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title, AllowMultiple = false,
            FileTypeFilter = types.Select(t => new FilePickerFileType(t.name) { Patterns = t.patterns }).ToList(),
        });
        return r.Count > 0 ? r[0].TryGetLocalPath() : null;
    }

    public async Task<string?> SaveFile(string title, string suggestedName, string extension)
    {
        var r = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title, SuggestedFileName = suggestedName, DefaultExtension = extension.TrimStart('.'),
            FileTypeChoices = [new FilePickerFileType(extension) { Patterns = ["*" + extension] }],
        });
        return r?.TryGetLocalPath();
    }

    public async Task<string?> PickFolder(string title)
    {
        var r = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return r.Count > 0 ? r[0].TryGetLocalPath() : null;
    }

    /// <summary>
    /// The settings window: the game folder and the mod manager's mods folder, changeable at any time. The owner asked for
    /// this ("We need a way to change the porters settings at any time, like the mod manager location or TPP location. It
    /// shouldn't assume the original location on first launch for the managers but it should take an educated guess on
    /// TPP.exe"). The guess lives in <see cref="Settings.GuessGameFolder"/> and only ever fills an empty field.
    /// </summary>
    public async Task ShowSettings()
    {
        var s = _vm.Session.Settings;
        var gameBox = new TextBox { Text = s.GameFolder, PlaceholderText = "The folder with mgsvtpp.exe and the master folder" };
        var modsBox = new TextBox { Text = s.ModsFolder, PlaceholderText = "Diamond Mod Manager's mods folder (optional)" };
        var gameNote = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brushes.Gray };
        var modsNote = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brushes.Gray };

        void RefreshNotes()
        {
            gameNote.Text = gameBox.Text is { Length: > 0 } g
                ? Directory.Exists(Path.Combine(g, "master")) ? "Game folder set." : "There's no master folder here. Pick the folder that holds mgsvtpp.exe."
                : "Not chosen yet. The tool reads the game's files to tell new files from vanilla ones.";
            modsNote.Text = modsBox.Text is { Length: > 0 } m
                ? Directory.Exists(m) ? "Mods folder set. The clash check uses it." : "That folder doesn't exist."
                : "Not set. The clash check is skipped without it; everything else works.";
        }
        RefreshNotes();
        gameBox.TextChanged += (_, _) => RefreshNotes();
        modsBox.TextChanged += (_, _) => RefreshNotes();

        Button Browse(TextBox box, string title) => new Button { Content = "Browse…" };
        var gameBrowse = Browse(gameBox, "Choose the game folder");
        var modsBrowse = Browse(modsBox, "Choose the mod manager's mods folder");
        gameBrowse.Click += async (_, _) => { if (await PickFolder("Choose the game folder") is { } f) gameBox.Text = f; };
        modsBrowse.Click += async (_, _) => { if (await PickFolder("Choose the mod manager's mods folder") is { } f) modsBox.Text = f; };

        var guess = new Button { Content = "Find the game" };
        guess.Click += (_, _) =>
        {
            if (Settings.GuessGameFolder() is { } found) gameBox.Text = found;
            else gameNote.Text = "Couldn't find MGSV in the usual Steam folders. Choose it by hand.";
        };

        var save = new Button { Content = "Save", IsDefault = true, Classes = { "primary" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var window = new Window
        {
            // Resizable with a floor, and the content scrolls: a fixed, non-resizable dialog is what made text go out of
            // the box when the owner's display scaled it (item 41).
            Title = "Settings", Width = 620, MinWidth = 460, MinHeight = 320, SizeToContent = SizeToContent.Height,
            CanResize = true, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = new StackPanel
                {
                Margin = new Thickness(20), Spacing = 14,
                Children =
                {
                    new TextBlock { Text = "GAME FOLDER", FontWeight = FontWeight.SemiBold },
                    new DockPanel { Children = { gameBrowse, gameBox } },
                    gameNote,
                    guess,
                    new TextBlock { Text = "MOD MANAGER MODS FOLDER", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) },
                    new DockPanel { Children = { modsBrowse, modsBox } },
                    modsNote,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new Thickness(0, 8, 0, 0),
                        Children = { cancel, save },
                    },
                },
                },
            },
        };
        DockPanel.SetDock(gameBrowse, Dock.Right);
        DockPanel.SetDock(modsBrowse, Dock.Right);
        gameBrowse.Margin = new Thickness(8, 0, 0, 0);
        modsBrowse.Margin = new Thickness(8, 0, 0, 0);
        save.Click += (_, _) =>
        {
            // The setter lets go of the game only when the folder changed (#99).
            _vm.OpenScreen.GameFolder = gameBox.Text ?? "";
            _vm.Session.Settings.ModsFolder = modsBox.Text ?? "";
            _vm.Session.Settings.Save();
            _vm.OpenScreen.RefreshSettings();
            window.Close();
        };
        cancel.Click += (_, _) => window.Close();
        await window.ShowDialog(this);
    }

    /// <summary>A small yes/no window, used before anything throws away unsaved work.</summary>
    public async Task<bool> Confirm(string title, string message, string yes, string no)
    {
        var yesButton = new Button { Content = yes, IsDefault = true };
        var noButton = new Button { Content = no, IsCancel = true };
        var window = new Window
        {
            // Resizable with a floor, and the message scrolls: a long message in a fixed 460px box was one of the places
            // text went out of the box (item 41).
            Title = title, Width = 460, MinWidth = 360, MinHeight = 180, SizeToContent = SizeToContent.Height,
            CanResize = true, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = new StackPanel
                {
                Margin = new Thickness(20), Spacing = 16,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { noButton, yesButton },
                    },
                },
                },
            },
        };
        var answer = false;
        yesButton.Click += (_, _) => { answer = true; window.Close(); };
        noButton.Click += (_, _) => window.Close();
        await window.ShowDialog(this);
        return answer;
    }
}
