namespace Svfm.Core.Formats;

/// <summary>
/// The readers index straight into the file's bytes, so a cut-off or damaged file throws out-of-range errors. Callers
/// skip a damaged file by catching <see cref="InvalidDataException"/>; the readers turn those errors into one.
/// </summary>
internal static class Damaged
{
    public static bool Is(Exception e) => e is ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException;

    public static InvalidDataException Error(string kind, Exception e) => new($"The {kind} file is damaged or cut off.", e);
}
