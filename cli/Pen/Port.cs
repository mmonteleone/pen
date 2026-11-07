namespace Pen;

/// <summary>
/// A port to forward from the host's 127.0.0.1 to the same port in the
/// sandbox. <see cref="FromFlag"/>: asked for with pen --port, rather than
/// only by ports in .pen/config.toml; a clash on the host is then an error
/// instead of a warning.
/// </summary>
internal readonly record struct Port(int Number, bool FromFlag)
{
    public static bool IsValid(long n) => n is >= 1 and <= 65535;

    /// <summary>
    /// The config's ports, then the flag's, without duplicates (lists append).
    /// A port in both counts as from the flag.
    /// </summary>
    public static List<Port> Merge(IEnumerable<int> config, IEnumerable<int> flag)
    {
        var fromFlag = flag.ToHashSet();
        var merged = new List<Port>();
        foreach (var n in config.Concat(flag))
        {
            if (!merged.Exists(p => p.Number == n)) merged.Add(new(n, fromFlag.Contains(n)));
        }
        return merged;
    }
}
