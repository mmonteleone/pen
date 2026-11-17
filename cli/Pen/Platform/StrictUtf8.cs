using System.Text;

namespace Pen.Platform;

/// <summary>UTF-8 that throws on what it can't encode or decode, rather than putting U+FFFD in its place.</summary>
internal static class StrictUtf8
{
    /// <summary>The encoding, without a byte order mark.</summary>
    /// <remarks>
    /// Decoding bytes that aren't valid UTF-8 throws <see cref="DecoderFallbackException"/>, and encoding text that
    /// isn't valid UTF-16 throws <see cref="EncoderFallbackException"/>.
    /// </remarks>
    public static readonly UTF8Encoding Encoding =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Decodes <paramref name="bytes"/> as UTF-8, giving null if they aren't valid UTF-8, so no text names them
    /// exactly.
    /// </summary>
    public static string? DecodeOrNull(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return Encoding.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }
}
