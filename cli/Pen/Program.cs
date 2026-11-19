using Pen.CommandLine;
using Pen.Commands;
using Pen.Configuration;
using Pen.Platform;
using Pen.Projects;
using Pen.Tools;

// Pen drives Docker on macOS and Linux only.
[assembly: System.Runtime.Versioning.UnsupportedOSPlatform("windows")]

namespace Pen;

/// <summary>Pen's entry point: parses the command line and runs the command it asks for.</summary>
internal static class Program
{
    private static int Main(string[] args) => RunGuarded(() => Run(args));

    /// <summary>Runs <paramref name="run"/>, showing expected failures as one line.</summary>
    /// <returns>The exit code <paramref name="run"/> returned, or the failure's.</returns>
    /// <remarks>
    /// Only what no caller handled gets here; a session it interrupts is finished by the next run, as when Pen dies.
    /// Other exceptions are Pen's bugs and keep their stack traces.
    /// </remarks>
    internal static int RunGuarded(Func<int> run)
    {
        try
        {
            return run();
        }
        catch (PenException exception)
        {
            Out.WriteLine(exception.Message);
            return exception.ExitCode;
        }
        catch (UnauthorizedAccessException exception)
        {
            Out.WriteLine(Out.DescribePermissionDenied(exception));
            return 1;
        }
        catch (IOException exception)
        {
            Out.WriteLine(DescribeFileFailure(exception));
            return 1;
        }
    }

    /// <summary>Describes a file error that nothing handled, for the user.</summary>
    /// <remarks>
    /// The exception's own text can't be shown: the AOT build (UseSystemResourceKeys) has it as a resource key.
    /// </remarks>
    internal static string DescribeFileFailure(IOException exception) =>
        $"a file operation failed ({exception.GetType().Name}): check that the disk has space " +
            "and the files under the project and ~/.config/pen are usable, then rerun";

    /// <summary>Runs the command <paramref name="args"/> ask for.</summary>
    /// <remarks>
    /// Each step needs more than the one before, and each command runs as soon as it has what it needs: help and
    /// listings need only the embedded recipes (no host, config or project), pen ps and pen clean --stale the host,
    /// pen upgrade, pen allow &lt;domain…&gt; and pen deny &lt;domain…&gt; the user config, the rest the project.
    /// Of the project commands, only a session, the pen allow and pen deny listings and pen trust load the project
    /// config; pen upgrade, pen allow &lt;domain…&gt; and pen deny &lt;domain…&gt; read it tolerantly or only to
    /// apply to running sessions.
    /// </remarks>
    private static int Run(string[] args)
    {
        var catalog = RecipeCatalog.Embedded;
        var clock = TimeProvider.System;
        var invocation = CommandParser.Parse(args, HostEnv.TryReadWorkingDirectory, catalog);
        switch (invocation)
        {
            case BuiltinInvocation builtin:
                CommandTree.InvokeBuiltin(builtin.ParseResult, Console.Out);
                return 0;
            case ToolsInvocation:
                Console.Out.Write(catalog.FormatListing());
                return 0;
            case ListDefaultDomainsInvocation:
                Console.Out.Write(AllowCommand.FormatDefaults());
                return 0;
        }

        var host = HostEnv.FromProcess();
        var startDir = new StartDir(host.LeaveWorkingDirectory());
        switch (invocation)
        {
            case PsInvocation:
                return new PsCommand(host, clock).Run();
            case CleanStaleInvocation:
                return new CleanStaleCommand(host, ReadAnswersFromStdin()).Run();
        }

        UserConfigInitializer.EnsureExists(host, GitIdentity.ReadFromHost);
        var user = ConfigLoader.LoadUser(host.ConfigDir, host.Display(host.ConfigDir), catalog);
        switch (invocation)
        {
            case UpgradeInvocation upgrade:
                return new UpgradeCommand(host, user, startDir, catalog, clock).Run(upgrade.NoCache);
            case AllowDomainsInvocation allow:
                return new AllowCommand(host, startDir, catalog, clock).Run(allow.Domains, allow.Global);
            case DenyDomainsInvocation deny:
                return new DenyCommand(host, startDir, catalog, clock).Run(deny.Domains, deny.Global);
        }

        var project = Project.Resolve(startDir.Get(), host);
        return invocation switch
        {
            RunInvocation run => RunSession(ProjectContext.Load(host, project, user, catalog), run, catalog, clock),
            ListBlockedInvocation =>
                ListBlocked(ProjectContext.Load(host, project, user, catalog), startDir, catalog, clock),
            ListDeniedInvocation =>
                ListDenied(ProjectContext.Load(host, project, user, catalog), startDir, catalog, clock),
            TrustInvocation trust =>
                RunTrust(ProjectContext.Load(host, project, user, catalog), trust.Mode, catalog, clock),
            InitInvocation => new InitCommand(host, project, catalog).Run(),
            SignInvocation sign => new SignCommand(host, project, clock).Run(sign.Base),
            CleanInvocation => new CleanCommand(host, project, ReadAnswersFromStdin(), clock).Run(),
            _ => throw new InvalidOperationException($"unhandled invocation {invocation}"),
        };
    }

    /// <summary>Runs a session in the project's sandbox.</summary>
    private static int RunSession(
        ProjectContext context, RunInvocation run, RecipeCatalog catalog, TimeProvider clock) =>
        new RunCommand(context, ReadAnswersFromStdin(), catalog, clock).Run(run);

    /// <summary>Gets this process's stdin, and whether it's a terminal.</summary>
    private static Answers ReadAnswersFromStdin() => new(Console.In, Native.IsATty(0));

    /// <summary>Runs pen allow without domains: lists the recently blocked ones.</summary>
    private static int ListBlocked(
        ProjectContext context, StartDir startDir, RecipeCatalog catalog, TimeProvider clock) =>
        new AllowCommand(context.Host, startDir, catalog, clock).ListBlocked(context);

    /// <summary>Runs pen deny without domains: lists what's denied.</summary>
    private static int ListDenied(
        ProjectContext context, StartDir startDir, RecipeCatalog catalog, TimeProvider clock) =>
        new DenyCommand(context.Host, startDir, catalog, clock).List(context);

    private static int RunTrust(ProjectContext context, TrustMode mode, RecipeCatalog catalog, TimeProvider clock) =>
        new TrustCommand(context, ReadAnswersFromStdin(), catalog, clock).Run(mode);
}
