using System.Reflection;

// pen drives Docker on macOS and Linux only.
[assembly: System.Runtime.Versioning.UnsupportedOSPlatform("windows")]

namespace Pen;

internal static class Program
{
    private static int Main(string[] args) => Guarded(() => Run(args));

    /// <summary>
    /// Runs <paramref name="run"/>, showing expected failures as one line. Only
    /// what no caller handled gets here; a session it interrupts is finished by
    /// the next run, as when pen dies. Other exceptions are pen's bugs and keep
    /// their stack traces.
    /// </summary>
    internal static int Guarded(Func<int> run)
    {
        try
        {
            return run();
        }
        catch (PenException e)
        {
            Out.Line(e.Message);
            return e.ExitCode;
        }
        catch (UnauthorizedAccessException e)
        {
            Out.Line(Out.PermissionDenied(e));
            return 1;
        }
        catch (IOException e)
        {
            Out.Line(FileFailure(e));
            return 1;
        }
    }

    /// <summary>
    /// The message for a file error nothing handled. The exception's own text
    /// can't be shown: the AOT build (UseSystemResourceKeys) has it as a resource key.
    /// </summary>
    internal static string FileFailure(IOException e) =>
        $"a file operation failed ({e.GetType().Name}): check that the disk has space and the files under the project and ~/.config/pen are usable, then rerun";

    private static int Run(string[] args)
    {
        var command = Cli.Parse(args);
        switch (command.Kind)
        {
            case CommandKind.Help:
                Console.Out.Write(command.Args.Count > 0 ? Cli.SubcommandHelp[command.Args[0]] + "\n" : Cli.Usage);
                return 0;
            case CommandKind.Allow when command.Defaults:
                Console.Out.Write(AllowCommand.Defaults());
                return 0;
            case CommandKind.Version:
                Console.Out.WriteLine("pen " + (typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?"));
                return 0;
        }

        var host = HostEnv.FromProcess();
        StartDir.Leave(host);
        if (command.Kind == CommandKind.Ps) return Ps.Run(host);
        if (command.Kind == CommandKind.Prune) return Prune.Run(host, Console.In);
        Config.EnsureUserConfig(host);
        var user = Config.Load(host.ConfigDir, host.Display(host.ConfigDir), ConfigKind.User);

        switch (command.Kind)
        {
            case CommandKind.Upgrade:
                Image.Upgrade(command.Clean);
                return 0;
            case CommandKind.Allow when command.Args.Count > 0:
                return AllowCommand.Add(host, command.Args, command.Global);
        }

        var project = Project.Resolve(StartDir.Get(), host);
        Config ProjectConfig() => Config.Load(project.PenDir, ".pen", ConfigKind.Project);
        return command.Kind switch
        {
            CommandKind.Run => RunSession(host, project, user, ProjectConfig(), command),
            CommandKind.Allow => AllowCommand.List(host, project, user, ProjectConfig()),
            CommandKind.Trust => Trust.Command(host, project, user, ProjectConfig(), command.Yes, Answers.FromProcess(), command.List),
            CommandKind.Sign => Sign.Run(host, project, command.Args.Count > 0 ? command.Args[0] : null),
            CommandKind.Reset => Reset.Run(host, project, Console.In),
            _ => throw new InvalidOperationException($"unhandled command {command.Kind}"),
        };
    }

    // --docker turns Docker-in-Docker on for one session; docker = true in
    // .pen/config.toml for every session. Nothing turns it off per session.
    // --port adds to ports in .pen/config.toml.
    private static int RunSession(HostEnv host, Project project, Config user, Config projectConfig, Command command) =>
        new Session(host, project, user, projectConfig).Run(command.Args, docker: command.Docker || projectConfig.Docker == true,
            ports: Port.Merge(projectConfig.Ports, command.Ports));
}
