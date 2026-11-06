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
                case CommandKind.Run:
                    var project = Project.Resolve(Environment.CurrentDirectory, host);
                    var projectConfig = Config.Load(project.ProjectConfigPath, ".pen/config", ConfigKind.Project);
                    return new Session(host, project, user, projectConfig).Run(command.Args);
                default:
                    throw new PenException($"pen {command.Kind.ToString().ToLowerInvariant()} isn't implemented yet");
            }
        }
        catch (PenException e)
        {
            Out.Line(e.Message);
            return e.ExitCode;
        }
    }
}
