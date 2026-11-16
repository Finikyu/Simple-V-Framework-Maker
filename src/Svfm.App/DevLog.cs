using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Svfm.App;

/// <summary>
/// The app's session log: one fixed line format, kept in memory for the Developer tab and written to a file, so a test
/// run on any machine leaves the same kind of record.
/// <code>2026-10-28 11:02:03.123  INFO   open      Opened "mod.mgsv": 1 outfit(s) in 640 ms</code>
/// Columns: time, level (INFO, WARN, ERROR, CHECK), category, message. Lines are never wrapped; a multi-line message
/// (an exception) is written as continuation lines starting with four spaces and a bar.
/// </summary>
public static class DevLog
{
    public const string Format = "svfm-log/1";
    private static readonly object Gate = new();
    private static readonly List<string> Memory = [];
    private static StreamWriter? _file;
    private static DateTime _started = DateTime.Now;

    /// <summary>Raised on the thread that wrote the line.</summary>
    public static event Action<string>? Added;
    public static string? FilePath { get; private set; }
    public static string Folder => Path.Combine(Path.GetDirectoryName(Settings.FilePathPublic)!, "logs");

    /// <summary>The text of the last build or dry run (what the Developer screen shows as the build report).</summary>
    public static string LastBuildReport { get; set; } = "";

    public static IReadOnlyList<string> Lines { get { lock (Gate) return [.. Memory]; } }

    /// <summary>Starts a session: opens the file and writes the header block (app, system, display).</summary>
    public static void Start(string? folderOverride = null)
    {
        lock (Gate)
        {
            if (_file != null) return;
            _started = DateTime.Now;
            try
            {
                string dir = folderOverride ?? Folder;
                Directory.CreateDirectory(dir);
                FilePath = Path.Combine(dir, $"svfm-{_started:yyyyMMdd-HHmmss}.log");
                _file = new StreamWriter(new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
                foreach (var old in new DirectoryInfo(dir).GetFiles("svfm-*.log").OrderByDescending(f => f.LastWriteTimeUtc).Skip(20)) { try { old.Delete(); } catch (IOException) { } }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _file = null; FilePath = null; }
        }
        var asm = typeof(DevLog).Assembly;
        string built = BuiltStamp(asm);
        Raw($"# {Format}  Simple V Framework Maker {asm.GetName().Version}  built {built}");
        Raw($"# started {_started:yyyy-MM-dd HH:mm:ss}  zone {TimeZoneInfo.Local.BaseUtcOffset}");
        Raw($"# os {RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}  runtime {RuntimeInformation.FrameworkDescription}  cpu {Environment.ProcessorCount}");
        Raw($"# culture {CultureInfo.CurrentCulture.Name}  64bit {Environment.Is64BitProcess}  temp {Path.GetTempPath()}");
        Raw("# columns: time  level  category  message");
    }

    /// <summary>
    /// When this build was made, for the log header. <c>Assembly.Location</c> is empty in a single-file publish (the
    /// assemblies are embedded), so use <c>AppContext.BaseDirectory</c>, and the exe itself (<c>Environment.ProcessPath</c>) when no dll is beside it.
    /// </summary>
    private static string BuiltStamp(Assembly asm)
    {
        string dll = Path.Combine(AppContext.BaseDirectory, asm.GetName().Name + ".dll"); // IL3000: Assembly.Location is empty in a single-file app
        string? path = File.Exists(dll) ? dll
            : Environment.ProcessPath is { Length: > 0 } exe && File.Exists(exe) ? exe
            : null;
        return path != null ? File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "?";
    }

    private static void Raw(string line)
    {
        lock (Gate) { Memory.Add(line); if (Memory.Count > 5000) Memory.RemoveRange(0, 1000); try { _file?.WriteLine(line); } catch (IOException) { } }
        Added?.Invoke(line);
    }

    public static void Write(string level, string category, string message)
    {
        string[] parts = message.Replace("\r", "").Split('\n');
        string head = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {level,-5}  {category,-9} {parts[0]}";
        Raw(head);
        for (int i = 1; i < parts.Length; i++) Raw("    | " + parts[i]);
    }

    public static void Info(string category, string message) => Write("INFO", category, message);
    public static void Warn(string category, string message) => Write("WARN", category, message);
    public static void Error(string category, string message) => Write("ERROR", category, message);
    public static void Error(string category, Exception e) => Write("ERROR", category, e.ToString());
    public static void Check(string name, bool ok, string detail = "") => Write("CHECK", "selfcheck", $"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  " + detail : "")}");

    /// <summary>Times an action and logs it.</summary>
    public static T Time<T>(string category, string what, Func<T> f)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try { var r = f(); Info(category, $"{what} ({sw.ElapsedMilliseconds} ms)"); return r; }
        catch (Exception e) { Error(category, $"{what} failed after {sw.ElapsedMilliseconds} ms: {e}"); throw; }
    }
}
