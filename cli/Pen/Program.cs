using Pen.CommandLine;
using Pen.Commands;
using Pen.Configuration;
using Pen.Platform;
using Pen.Projects;
using Pen.Sessions;
using Pen.Tools;
using Pen.Trust;

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
    /// Only what no caller handled gets here; a session it interrupts is finished by
    /// the next run, as when Pen dies. Other exceptions are Pen's bugs and keep
    /// their stack traces.
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
    /// The exception's own text can't be shown: the AOT build (UseSystemResourceKeys)
    /// has it as a resource key.
    /// </remarks>
    internal static string DescribeFileFailure(IOException exception) =>
        $"a file operation failed ({exception.GetType().Name}): check that the disk has space " +
            "and the files under the project and ~/.config/pen are usable, then rerun";

    /// <summary>Runs the command <paramref name="args"/> ask for.</summary>
    /// <remarks>
    /// Each step needs more than the one before, and each command runs as soon as it has
    /// what it needs: help and listings need only the embedded recipes (no host, config or project),
    /// pen ps and pen clean --stale the host, pen upgrade, pen allow &lt;domain…&gt; and
    /// pen deny &lt;domain…&gt; the user config, the rest the project.
    /// Only a session, the pen allow and pen deny listings and pen trust load the project config.
    /// </remarks>
    private static int Run(string[] args)
    {
        var catalog = RecipeCatalog.Embedded;
        var command = CommandParser.Parse(args, StartDir.TryGet, catalog);
        switch (command.Kind)
        {
            case CommandKind.Builtin:
                CommandTree.InvokeBuiltin(command.ParseResult!, Console.Out);
                return 0;
            case CommandKind.Tools:
                Console.Out.Write(catalog.FormatListing());
                return 0;
            case CommandKind.Allow when command.Defaults:
                Console.Out.Write(AllowCommand.FormatDefaults());
                return 0;
        }

        var host = HostEnv.FromProcess();
        StartDir.Leave(host);
        switch (command.Kind)
        {
            case CommandKind.Ps:
                return new PsCommand(host).Run();
            case CommandKind.Clean when command.Stale:
                return new CleanStaleCommand(host, Console.In).Run();
        }

        Config.EnsureUserConfig(host, GitIdentity.ReadFromHost);
        var user = Config.Load(host.ConfigDir, host.Display(host.ConfigDir), ConfigKind.User, catalog);
        switch (command.Kind)
        {
            case CommandKind.Upgrade:
                return new UpgradeCommand(host, user, catalog).Run(command.NoCache);
            case CommandKind.Allow when command.Args.Count > 0:
                return new AllowCommand(host, catalog).Run(command.Args, command.Global);
            case CommandKind.Deny when command.Args.Count > 0:
                return new DenyCommand(host, catalog).Run(command.Args, command.Global);
        }

        var project = Project.Resolve(StartDir.Get(), host);
        return command.Kind switch
        {
            CommandKind.Run => RunSession(ProjectContext.Load(host, project, user, catalog), command, catalog),
            CommandKind.Allow => ListBlocked(ProjectContext.Load(host, project, user, catalog), catalog),
            CommandKind.Deny => ListDenied(ProjectContext.Load(host, project, user, catalog), catalog),
            CommandKind.Trust => RunTrust(ProjectContext.Load(host, project, user, catalog), command, catalog),
            CommandKind.Init => new InitCommand(host, project, catalog).Run(),
            CommandKind.Sign => RunSign(host, project, command),
            CommandKind.Clean => new CleanCommand(host, project, Console.In).Run(),
            _ => throw new InvalidOperationException($"unhandled command {command.Kind}"),
        };
    }

    /// <summary>Runs a session in the project's sandbox.</summary>
    /// <remarks>
    /// --docker turns Docker-in-Docker on for one session; docker = true in
    /// .pen/config.toml for every session. Nothing turns it off per session.
    /// --port adds to ports in .pen/config.toml.
    /// --tool adds to tools in both config files.
    /// </remarks>
    private static int RunSession(ProjectContext context, Command command, RecipeCatalog catalog)
    {
        var request = new SessionRequest(command.Args)
        {
            Docker = command.Docker || context.ProjectConfig.Docker == true,
            Ports = Port.Merge(context.ProjectConfig.Ports, command.Ports),
            FlagTools = command.Tools,
        };
        return new Session(context, Answers.FromProcess(), catalog).Run(request);
    }

    /// <summary>Runs pen allow without domains: lists the recently blocked ones.</summary>
    private static int ListBlocked(ProjectContext context, RecipeCatalog catalog) =>
        new AllowCommand(context.Host, catalog).ListBlocked(context);

    /// <summary>Runs pen deny without domains: lists what's denied.</summary>
    private static int ListDenied(ProjectContext context, RecipeCatalog catalog) =>
        new DenyCommand(context.Host, catalog).List(context);

    /// <summary>Runs pen trust.</summary>
    private static int RunTrust(ProjectContext context, Command command, RecipeCatalog catalog) =>
        new TrustCommand(context, Answers.FromProcess(), catalog).Run(command.Yes, command.List);

    /// <summary>Runs pen sign, against the base given or the default one.</summary>
    private static int RunSign(HostEnv host, Project project, Command command) =>
        new SignCommand(host, project).Run(command.Args.Count > 0 ? command.Args[0] : null);
}
