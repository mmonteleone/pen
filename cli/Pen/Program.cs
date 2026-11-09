using System.Reflection;

// pen drives Docker on macOS and Linux only.
[assembly: System.Runtime.Versioning.UnsupportedOSPlatform("windows")]

namespace Pen;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
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

            var project = Project.Resolve(Environment.CurrentDirectory, host);
            Config ProjectConfig() => Config.Load(project.PenDir, ".pen", ConfigKind.Project);
            return command.Kind switch
            {
                CommandKind.Run => RunSession(host, project, user, ProjectConfig(), command),
                CommandKind.Allow => AllowCommand.List(host, project, user, ProjectConfig()),
                CommandKind.Sign => Sign.Run(host, project, command.Args.Count > 0 ? command.Args[0] : null),
                CommandKind.Reset => Reset.Run(host, project, Console.In),
                _ => throw new InvalidOperationException($"unhandled command {command.Kind}"),
            };
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
    }

    // --docker turns Docker-in-Docker on for one session; docker = true in
    // .pen/config.toml for every session. Nothing turns it off per session.
    // --port adds to ports in .pen/config.toml.
    private static int RunSession(HostEnv host, Project project, Config user, Config projectConfig, Command command) =>
        new Session(host, project, user, projectConfig).Run(command.Args, docker: command.Docker || projectConfig.Docker == true,
            ports: Port.Merge(projectConfig.Ports, command.Ports));
}
