namespace Pen.SmokeTests.Harness;

/// <summary>Assertions on a command's output that smoke.sh's checks shared, each failing with the output.</summary>
internal static class SmokeAssert
{
    /// <summary>Asserts that the combined output has <paramref name="text"/> in it (contains).</summary>
    /// <param name="result">The command's result.</param>
    /// <param name="text">The text.</param>
    public static void Contains(CommandResult result, string text) =>
        Assert.True(result.Output.Contains(text, StringComparison.Ordinal), $"no \"{text}\"; {result.Describe()}");

    /// <summary>Asserts that the combined output has every one of <paramref name="texts"/> in it (has_all).</summary>
    /// <param name="result">The command's result.</param>
    /// <param name="texts">The texts.</param>
    public static void ContainsAll(CommandResult result, params string[] texts)
    {
        var missing = texts.Where(text => !result.Output.Contains(text, StringComparison.Ordinal)).ToList();
        Assert.True(missing.Count == 0, $"no \"{string.Join("\", \"", missing)}\"; {result.Describe()}");
    }

    /// <summary>Asserts that stderr alone has <paramref name="text"/> in it, as smoke.sh's holding captured
    /// it.</summary>
    /// <param name="result">The command's result.</param>
    /// <param name="text">The text.</param>
    public static void StderrContains(CommandResult result, string text) =>
        Assert.True(
            result.Stderr.Contains(text, StringComparison.Ordinal), $"no \"{text}\" on stderr; {result.Describe()}");

    /// <summary>Asserts that the combined output is all there and doesn't have <paramref name="text"/> in it (not
    /// contains).</summary>
    /// <param name="result">The command's result.</param>
    /// <param name="text">The text.</param>
    public static void Lacks(CommandResult result, string text) =>
        Assert.True(
            result.OutputComplete && !result.Output.Contains(text, StringComparison.Ordinal),
            $"\"{text}\" is there, or the output was cut short; {result.Describe()}");

    /// <summary>Asserts that a probe session's output has exactly this line (has).</summary>
    /// <param name="probes">The probe session's result.</param>
    /// <param name="line">The whole line, e.g. "fail: hook".</param>
    public static void HasLine(ProbeResult probes, string line) =>
        Assert.True(probes.HasLine(line), $"no line \"{line}\"; {probes.Describe()}");

    /// <summary>Asserts that pen refused: a non-zero exit with the message in the output (refuse).</summary>
    /// <param name="result">Pen's result.</param>
    /// <param name="message">A part of the expected message.</param>
    public static void Refused(CommandResult result, string message) =>
        Assert.True(result.IsRefusal(message), $"expected a refusal saying \"{message}\"; {result.Describe()}");

    /// <summary>Asserts that a command exited zero.</summary>
    /// <param name="result">The command's result.</param>
    public static void Succeeded(CommandResult result) => Assert.True(result.ExitCode == 0, result.Describe());
}
