using System.Runtime.CompilerServices;
using Xunit.v3;

namespace Pen.SmokeTests.Harness;

/// <summary>Marks a slow check: a fact with the trait Category=Slow, skipped unless SMOKE_SLOW=1.</summary>
/// <remarks>--filter-trait "Category=Slow" selects only the slow checks.</remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class SlowFactAttribute : FactAttribute, ITraitAttribute
{
    /// <summary>The trait's name.</summary>
    public const string Name = "Category";

    /// <summary>The trait's value.</summary>
    public const string Value = "Slow";

    /// <summary>Initializes a new instance of the <see cref="SlowFactAttribute"/> class.</summary>
    /// <param name="sourceFilePath">The test's source file, filled in by the compiler.</param>
    /// <param name="sourceLineNumber">The test's line, filled in by the compiler.</param>
    public SlowFactAttribute(
        [CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = "SMOKE_SLOW=1 to run it";
        SkipType = typeof(SmokeEnvironment);
        SkipUnless = nameof(SmokeEnvironment.IsSlow);
    }

    /// <summary>Gets the trait.</summary>
    /// <returns>Category=Slow.</returns>
    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits() => [new(Name, Value)];
}
