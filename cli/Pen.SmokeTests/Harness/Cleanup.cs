using System.Runtime.ExceptionServices;

namespace Pen.SmokeTests.Harness;

/// <summary>Runs cleanup steps so that one that fails doesn't stop the rest, as || true did, keeping the first
/// error.</summary>
internal sealed class Cleanup
{
    /// <summary>Gets the first error a step threw, or null if none has.</summary>
    public Exception? FirstError { get; private set; }

    /// <summary>Runs a step, keeping its error if it's the first.</summary>
    /// <param name="step">The step.</param>
    public void Run(Action step)
    {
        try
        {
            step();
        }
        catch (Exception exception)
        {
            FirstError ??= exception;
        }
    }

    /// <summary>Throws the first error again, with its stack trace, if a step threw one.</summary>
    public void ThrowIfFailed()
    {
        if (FirstError != null)
            ExceptionDispatchInfo.Throw(FirstError);
    }
}
