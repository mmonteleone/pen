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
                    Console.Out.Write(Cli.Usage);
                    return 0;
                case CommandKind.Version:
                    Console.Out.WriteLine("pen " + (typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?"));
                    return 0;
            }

            var host = HostEnv.FromProcess();
            Config.EnsureUserConfig(host);
            var user = Config.Load(host.UserConfigPath, host.Display(host.UserConfigPath), ConfigKind.User);

            switch (command.Kind)
            {
                case CommandKind.Upgrade:
                    Image.Upgrade(command.Clean);
                    return 0;
                case CommandKind.Allow when command.Args.Count > 0:
                    return AllowCommand.Add(host, command.Args, command.Global);
            }

            var project = Project.Resolve(Environment.CurrentDirectory, host);
            Config ProjectConfig() => Config.Load(project.ProjectConfigPath, ".pen/config", ConfigKind.Project);
            return command.Kind switch
            {
                CommandKind.Run => new Session(host, project, user, ProjectConfig()).Run(command.Args),
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
    }
}
