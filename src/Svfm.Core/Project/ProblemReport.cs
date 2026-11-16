using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Svfm.Core.Project;

/// <summary>
/// The Problem report (see GLOSSARY.md): one plain text file that a user sends to the developer. It has three parts, in
/// this order: a header (app version, commit, Windows version, date and time), the errors of the session, and the
/// session log. The user profile path is replaced with %USERPROFILE% in the text only; the log files do not change.
/// </summary>
public static class ProblemReport
{
    /// <summary>The issues page of the public repository.</summary>
    public const string IssuesUrl = "https://github.com/Finikyu/Simple-V-Framework-Maker/issues";
    /// <summary>The GitHub form for a problem; the user attaches the report to it.</summary>
    public const string ProblemUrl = IssuesUrl + "/new?template=problem.yml";
    /// <summary>The GitHub form for feedback.</summary>
    public const string FeedbackUrl = IssuesUrl + "/new?template=feedback.yml";

    /// <summary>Builds the report text.</summary>
    /// <param name="appVersion">The app version, for example 0.2.0.</param>
    /// <param name="commit">The commit of the build (empty when unknown).</param>
    /// <param name="windows">The Windows version.</param>
    /// <param name="at">When the report is made.</param>
    /// <param name="errors">The errors of this session from error.log (empty when there were no errors).</param>
    /// <param name="log">The session log lines.</param>
    /// <param name="userProfile">The user profile folder, replaced with %USERPROFILE% (empty: nothing is replaced).</param>
    public static string Text(string appVersion, string commit, string windows, DateTime at, string errors, IEnumerable<string> log, string userProfile)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Simple V Framework Maker: Problem report");
        sb.AppendLine("========================================");
        sb.AppendLine($"App version: {appVersion}");
        sb.AppendLine($"Commit: {(commit.Length > 0 ? commit : "(unknown)")}");
        sb.AppendLine($"Windows: {windows}");
        sb.AppendLine($"Date and time: {at.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}");
        sb.AppendLine($"Send this file with an issue: {ProblemUrl}");
        sb.AppendLine();
        sb.AppendLine("Errors of this session");
        sb.AppendLine("----------------------");
        sb.AppendLine(errors.Trim().Length > 0 ? errors.TrimEnd() : "No errors in this session.");
        sb.AppendLine();
        sb.AppendLine("Session log");
        sb.AppendLine("-----------");
        int lines = 0;
        foreach (var l in log) { sb.AppendLine(l); lines++; }
        if (lines == 0) sb.AppendLine("No session log."); // a crash at startup comes before the log starts
        string text = sb.ToString();
        string profile = userProfile.TrimEnd('\\', '/');
        if (profile.Length == 0) return text;
        return text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase)
                   .Replace(profile.Replace('\\', '/'), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Builds the report for this app run (the version and commit of <paramref name="app"/>, the OS of this PC), writes it into <paramref name="folder"/> and returns its path.</summary>
    /// <param name="app">The app assembly: its informational version is "version+commit".</param>
    /// <param name="folder">The folder of the report.</param>
    /// <param name="errors">The errors of this session (empty when there were none).</param>
    /// <param name="log">The session log lines.</param>
    /// <param name="now">When the report is made.</param>
    public static string WriteForApp(Assembly app, string folder, string errors, IEnumerable<string> log, DateTime now)
    {
        string info = app.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        string version = info.Split('+')[0], commit = info.Contains('+') ? info[(info.IndexOf('+') + 1)..] : "";
        string text = Text(version, commit, $"{RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}", now,
            errors, log, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        return Write(folder, text, now);
    }

    /// <summary>The text that was added to the error log after byte <paramref name="start"/> (its length when the session started).</summary>
    /// <param name="errorLog">The path of error.log.</param>
    /// <param name="start">The length of error.log, in bytes, when the session started.</param>
    /// <returns>Empty when there is no file. All of the file when it is now shorter than <paramref name="start"/> (it was made again).</returns>
    public static string ErrorsSince(string errorLog, long start)
    {
        if (!File.Exists(errorLog)) return "";
        using var f = new FileStream(errorLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (f.Length >= start) f.Position = start;
        using var r = new StreamReader(f, Encoding.UTF8);
        return r.ReadToEnd();
    }

    /// <summary>The file name: <c>Problem report YYYY-MM-DD HH-mm.txt</c>.</summary>
    public static string FileName(DateTime at) => $"Problem report {at.ToString("yyyy-MM-dd HH-mm", CultureInfo.InvariantCulture)}.txt";

    /// <summary>Writes the report into <paramref name="folder"/> and returns its path. If the name exists, a number is added: "(2)", "(3)" ...</summary>
    public static string Write(string folder, string text, DateTime at)
    {
        Directory.CreateDirectory(folder);
        string stem = Path.GetFileNameWithoutExtension(FileName(at));
        for (int n = 1; ; n++)
        {
            string path = Path.Combine(folder, n == 1 ? stem + ".txt" : $"{stem} ({n}).txt");
            try
            {
                using var f = new FileStream(path, FileMode.CreateNew, FileAccess.Write); // CreateNew: never overwrite an earlier report
                using var w = new StreamWriter(f, new UTF8Encoding(false));
                w.Write(text);
                return path;
            }
            catch (IOException) when (File.Exists(path)) { }
        }
    }
}
