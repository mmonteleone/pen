using System.Formats.Tar;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Pen.Platform;

/// <summary>
/// Reads the files embedded in the binary (compose.yaml, image/, proxy/, templates/, tools/),
/// named by their repo-relative paths.
/// </summary>
internal static class Embedded
{
    private static Assembly ResourceAssembly => typeof(Embedded).Assembly;

    /// <summary>Opens the embedded file <paramref name="name"/>.</summary>
    /// <exception cref="InvalidOperationException">No such file is embedded.</exception>
    public static Stream Open(string name) =>
        ResourceAssembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"missing embedded file {name}");

    /// <summary>Reads the embedded file <paramref name="name"/> as UTF-8 text.</summary>
    public static string ReadText(string name)
    {
        using var reader = new StreamReader(Open(name), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>Reads the embedded file <paramref name="name"/>'s bytes.</summary>
    public static byte[] ReadBytes(string name)
    {
        using var stream = Open(name);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>Lists the embedded names under a prefix such as "image/", sorted ordinally.</summary>
    public static List<string> ListUnder(string prefix) =>
        [.. ResourceAssembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)];

    /// <summary>Computes the image hash: SHA-256 over every embedded image/ file's path and content.</summary>
    /// <returns>The hash's first 16 hex digits.</returns>
    public static string ComputeImageHash()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var name in ListUnder("image/"))
        {
            var bytes = ReadBytes(name);
            hash.AppendData(Encoding.UTF8.GetBytes($"{name}\0{bytes.Length}\0"));
            hash.AppendData(bytes);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset())[..16];
    }

    /// <summary>Writes image/ as a tar build context (paths relative to image/).</summary>
    public static void WriteImageContext(Stream output)
    {
        using var tar = new TarWriter(output, TarEntryFormat.Ustar, leaveOpen: true);
        foreach (var name in ListUnder("image/"))
        {
            var bytes = ReadBytes(name);
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
