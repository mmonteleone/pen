using System.Formats.Tar;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Pen;

/// <summary>
/// Files embedded in the binary (compose.yaml, image/, proxy/, templates/, tools/),
/// named by their repo-relative paths.
/// </summary>
internal static class Embedded
{
    private static Assembly Asm => typeof(Embedded).Assembly;

    public static Stream Open(string name) =>
        Asm.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"missing embedded file {name}");

    public static string Text(string name)
    {
        using var r = new StreamReader(Open(name), Encoding.UTF8);
        return r.ReadToEnd();
    }

    public static byte[] Bytes(string name)
    {
        using var s = Open(name);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Embedded names under a prefix such as "image/", sorted ordinally.</summary>
    public static List<string> Under(string prefix) =>
        Asm.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();

    /// <summary>The image hash: SHA-256 over every embedded image/ file's path and content.</summary>
    public static string ImageHash()
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var name in Under("image/"))
        {
            var bytes = Bytes(name);
            h.AppendData(Encoding.UTF8.GetBytes($"{name}\0{bytes.Length}\0"));
            h.AppendData(bytes);
        }
        return Convert.ToHexStringLower(h.GetHashAndReset())[..16];
    }

    /// <summary>Writes image/ as a tar build context (paths relative to image/).</summary>
    public static void WriteImageContext(Stream output)
    {
        using var tar = new TarWriter(output, TarEntryFormat.Ustar, leaveOpen: true);
        foreach (var name in Under("image/"))
        {
            var bytes = Bytes(name);
            var entry = new UstarTarEntry(TarEntryType.RegularFile, name["image/".Length..])
            {
                DataStream = new MemoryStream(bytes),
                Mode = name.EndsWith(".sh", StringComparison.Ordinal)
                    ? (UnixFileMode)0b111_101_101
                    : (UnixFileMode)0b110_100_100,
                ModificationTime = DateTimeOffset.UnixEpoch,
            };
            tar.WriteEntry(entry);
        }
    }
}
