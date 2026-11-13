using System.Reflection;
using Pen.CommandLine;
using Pen.Commands;
using Pen.Configuration;
using Pen.Images;
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
        catch (PenException e)
        {
            Out.WriteLine(e.Message);
            return e.ExitCode;
        }
        catch (UnauthorizedAccessException e)
        {
            Out.WriteLine(Out.DescribePermissionDenied(e));
            return 1;
        }
        catch (IOException e)
        {
            Out.WriteLine(DescribeFileFailure(e));
            return 1;
        }
    }

    /// <summary>Describes a file error that nothing handled, for the user.</summary>
    /// <remarks>
    /// The exception's own text can't be shown: the AOT build (UseSystemResourceKeys)
    /// has it as a resource key.
    /// </remarks>
    internal static string DescribeFileFailure(IOException e) =>
        $"a file operation failed ({e.GetType().Name}): check that the disk has space " +
            "and the files under the project and ~/.config/pen are usable, then rerun";

    /// <summary>Runs the command <paramref name="args"/> ask for.</summary>
    /// <remarks>
    /// Each step needs more than the one before, and each command runs as soon as it has
    /// what it needs: help and listings need nothing, pen ps and pen prune the host,
    /// pen upgrade and pen allow &lt;domain…&gt; the user config, the rest the project.
    /// Only a session, pen allow (listing) and pen trust load the project config.
    /// </remarks>
    private static int Run(string[] args)
    {
        var command = CommandParser.Parse(args);
        switch (command.Kind)
        {
            case CommandKind.Help:
                return WriteHelp(command);
            case CommandKind.ToolList:
                Console.Out.Write(RecipeCatalog.Embedded.Listing());
                return 0;
            case CommandKind.Allow when command.Defaults:
                Console.Out.Write(AllowCommand.Defaults());
                return 0;
            case CommandKind.Version:
                return WriteVersion();
        }

        var host = HostEnv.FromProcess();
        StartDir.Leave(host);
        switch (command.Kind)
        {
            case CommandKind.Ps:
                return Ps.Run(host);
            case CommandKind.Prune:
                return Prune.Run(host, Console.In);
        }

        Config.EnsureUserConfig(host, GitIdentity.ReadFromHost);
        var user = Config.Load(host.ConfigDir, host.Display(host.ConfigDir), ConfigKind.User);
        switch (command.Kind)
        {
            case CommandKind.Upgrade:
                Image.Upgrade(host, user, command.Clean);
                return 0;
            case CommandKind.Allow when command.Args.Count > 0:
                return AllowCommand.Add(host, command.Args, command.Global);
        }

        var project = Project.Resolve(StartDir.Get(), host);
        return command.Kind switch
        {
            CommandKind.Run => RunSession(ProjectContext.Load(host, project, user), command),
            CommandKind.Allow => ListBlocked(ProjectContext.Load(host, project, user)),
            CommandKind.Trust => RunTrust(ProjectContext.Load(host, project, user), command),
            CommandKind.Sign => RunSign(host, project, command),
            CommandKind.Reset => Reset.Run(host, project, Console.In),
            _ => throw new InvalidOperationException($"unhandled command {command.Kind}"),
        };
    }

    /// <summary>Writes pen --help, or a subcommand's help, to stdout.</summary>
    private static int WriteHelp(Command command)
    {
        var text = command.Args.Count > 0 ? HelpText.Subcommands[command.Args[0]] + "\n" : HelpText.Usage;
        Console.Out.Write(text);
        return 0;
    }

    /// <summary>Writes pen --version to stdout.</summary>
    private static int WriteVersion()
    {
        var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        Console.Out.WriteLine("pen " + (version ?? "?"));
        return 0;
    }

    /// <summary>Runs a session in the project's sandbox.</summary>
    /// <remarks>
    /// --docker turns Docker-in-Docker on for one session; docker = true in
    /// .pen/config.toml for every session. Nothing turns it off per session.
    /// --port adds to ports in .pen/config.toml.
    /// --tool adds to tools in both config files.
    /// </remarks>
    private static int RunSession(ProjectContext context, Command command) =>
        new Session(context.Host, context.Project, context.User, context.ProjectConfig).Run(command.Args,
            docker: command.Docker || context.ProjectConfig.Docker == true,
            ports: Port.Merge(context.ProjectConfig.Ports, command.Ports), flagTools: command.Tools);

    /// <summary>Runs pen allow without domains: lists the recently blocked ones.</summary>
    private static int ListBlocked(ProjectContext context) =>
        AllowCommand.List(context.Host, context.Project, context.User, context.ProjectConfig);

    /// <summary>Runs pen trust.</summary>
    private static int RunTrust(ProjectContext context, Command command) =>
        TrustGate.Command(context.Host, context.Project, context.User, context.ProjectConfig, command.Yes,
            Answers.FromProcess(), command.List);

    /// <summary>Runs pen sign, against the base given or the default one.</summary>
    private static int RunSign(HostEnv host, Project project, Command command) =>
        Sign.Run(host, project, command.Args.Count > 0 ? command.Args[0] : null);
}
