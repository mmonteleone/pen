using Pen.Platform;

namespace Pen.Tests;

public class NormalizationTests
{
    private const string MacOSOnly = "only macOS file systems take NFC and NFD spellings for one name";

    [Fact]
    public void TheTestsRunWithInvariantGlobalization_AsPenDoes()
    {
        // In invariant mode string.Normalize returns its input unchanged; the tests must see what pen sees.
        Assert.True(AppContext.TryGetSwitch("System.Globalization.Invariant", out var isInvariant) && isInvariant);
        Assert.Equal("cafe\u0301", "cafe\u0301".Normalize());
    }

    [Fact]
    public void AnAsciiName_IsReturnedItself()
    {
        var name = "apps/web/.env";
        Assert.Same(name, Native.NormalizeFormC(name));
        Assert.Same("", Native.NormalizeFormC(""));
    }

    [Fact]
    public void AnNfdName_ComesBackInFormC_OnMacOS()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), MacOSOnly);
        Assert.Equal("caf\u00e9/CAF\u00c9.env", Native.NormalizeFormC("cafe\u0301/CAFE\u0301.env"));
        // Hangul jamo compose algorithmically.
        Assert.Equal("\ud55c", Native.NormalizeFormC("\u1112\u1161\u11ab"));
        // Text in form C already comes back the same.
        Assert.Equal("caf\u00e9 \ud55c", Native.NormalizeFormC("caf\u00e9 \ud55c"));
        // A character outside the BMP (a surrogate pair) comes back whole.
        Assert.Equal("\U0001F600caf\u00e9", Native.NormalizeFormC("\U0001F600cafe\u0301"));
    }

    [Fact]
    public void ALongName_ComesBackWhole_OnMacOS()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), MacOSOnly);
        var nfd = string.Concat(Enumerable.Repeat("e\u0301/", 5000));

        Assert.Equal(string.Concat(Enumerable.Repeat("\u00e9/", 5000)), Native.NormalizeFormC(nfd));
    }

    [Fact]
    public void NfcAndNfdSpellings_AreKeptApart_OnLinux()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux file systems tell NFC and NFD spellings apart; macOS ones don't");
        var name = "cafe\u0301.env";

        Assert.Same(name, Native.NormalizeFormC(name));
    }
}
