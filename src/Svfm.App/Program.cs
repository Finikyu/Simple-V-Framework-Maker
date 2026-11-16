using Avalonia;

namespace Svfm.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try { return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        catch (Exception e)
        {
            // A crash before the window can show an error: write the Problem report next to the exe and end, with no message box.
            try { Svfm.Core.Project.ProblemReport.WriteForApp(typeof(Program).Assembly, AppContext.BaseDirectory, e.ToString(), DevLog.Lines, DateTime.Now); } catch { } // nothing else can be done here
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().WithPlexFonts().LogToTrace();
}

public static class FontSetup
{
    /// <summary>IBM Plex Sans and Mono, bundled under Assets/Fonts; use them as "fonts:Plex#IBM Plex Sans" / "fonts:Plex#IBM Plex Mono".</summary>
    public static Avalonia.AppBuilder WithPlexFonts(this Avalonia.AppBuilder b) =>
        b.ConfigureFonts(fm => fm.AddFontCollection(new Avalonia.Media.Fonts.EmbeddedFontCollection(new Uri("fonts:Plex"), new Uri("avares://SimpleVFrameworkMaker/Assets/Fonts"))));
}
