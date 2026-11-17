namespace Pen.Platform;

/// <summary>Formats the first few items of a list for a message, then how many more there are.</summary>
/// <remarks>Each formats only the items it lists, each once and in order, before returning.</remarks>
internal static class CappedList
{
    /// <summary>
    /// Formats the first <paramref name="max"/> of <paramref name="items"/>, one per line, then how many more there
    /// are.
    /// </summary>
    /// <returns>One line per item listed, without indent, then "… and N more" if any weren't.</returns>
    public static List<string> FormatLines<T>(IReadOnlyCollection<T> items, int max, Func<T, string> format)
    {
        var lines = items.Take(max).Select(format).ToList();
        if (items.Count > max)
            lines.Add($"… and {items.Count - max} more");
        return lines;
    }

    /// <summary>
    /// Joins the first <paramref name="max"/> of <paramref name="items"/> with commas, then " and N more" if any
    /// weren't.
    /// </summary>
    public static string Join<T>(IReadOnlyCollection<T> items, int max, Func<T, string> format)
    {
        var listed = string.Join(", ", items.Take(max).Select(format));
        return items.Count > max ? $"{listed} and {items.Count - max} more" : listed;
    }

    /// <summary>
    /// Joins the first <paramref name="max"/> of <paramref name="items"/> with commas, then " and N more" if any
    /// weren't.
    /// </summary>
    public static string Join(IReadOnlyCollection<string> items, int max) => Join(items, max, item => item);
}
