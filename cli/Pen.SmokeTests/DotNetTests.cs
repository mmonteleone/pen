using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Pen.SmokeTests;

/// <summary>The dotnet tool: SDKs by version, PowerShell, NuGet through the proxy into the project's cache volume,
/// a build shared with the host, and a web app on a forwarded port.</summary>
/// <remarks>Login checks (log in to claude and codex, still logged in after pen upgrade --no-cache, logged out in
/// another project) are manual.</remarks>
public sealed class DotNetTests(DotNetTests.Fixture fixture) : IClassFixture<DotNetTests.Fixture>
{
    [Fact(DisplayName = "without the tool there's no dotnet or pwsh, and NuGet is blocked")]
    public void WithoutTheToolThereIsNoDotNet()
    {
        var lines = fixture.WithoutTool.StdoutLines;

        Assert.True(
            lines[0] == "none" && FieldOf(lines.ElementAtOrDefault(1), 1) == "403", fixture.WithoutTool.Describe());
    }

    [Fact(DisplayName = "dotnet:8 alone: pwsh is the recipe's (/opt/microsoft/powershell/7) and runs")]
    public void DotNet8HasTheRecipesPwsh()
    {
        var lines = fixture.DotNet8.StdoutLines;

        Assert.True(
            lines[0] == "/opt/microsoft/powershell/7/pwsh"
                && Regex.IsMatch(lines.ElementAtOrDefault(1) ?? "", "^[0-9]+$"),
            fixture.DotNet8.Describe());
    }

    [Fact(DisplayName = "...and dotnet lists only SDK 8")]
    public void DotNet8ListsOnlySdk8() =>
        Assert.True(FormatMajors(fixture.DotNet8.StdoutLines.Skip(2)) == "8 ", fixture.DotNet8.Describe());

    [Fact(DisplayName = "with the tool, NuGet is reachable")]
    public void WithTheToolNuGetIsReachable() =>
        Assert.True(
            string.Join('\n', fixture.NuGet.StdoutLines.Select(line => FieldOf(line, 0))) == "200",
            fixture.NuGet.Describe());

    [Fact(DisplayName = "dotnet alone lists the SDK of the default version, 10")]
    public void DefaultVersionIs10() =>
        Assert.True(FormatMajors(fixture.DefaultSdks.StdoutLines) == "10 ", fixture.DefaultSdks.Describe());

    [Fact(DisplayName = "dotnet:8 with dotnet:10 lists both SDKs")]
    public void TwoVersionsListBothSdks() =>
        Assert.True(FormatMajors(fixture.BothSdks.StdoutLines) == "8 10 ", fixture.BothSdks.Describe());

    [Fact(DisplayName = "...with the ASP.NET and container settings")]
    public void HasTheAspNetSettings()
    {
        string[] settings =
        [
            "ASPNETCORE_HTTP_PORTS=8080", "DOTNET_RUNNING_IN_CONTAINER=true",
            "DOTNET_GENERATE_ASPNET_CERTIFICATE=false", "DOTNET_USE_POLLING_FILE_WATCHER=true",
            "NUGET_XMLDOC_MODE=skip", "POWERSHELL_DISTRIBUTION_CHANNEL=", "DOTNET_NOLOGO=1",
            "DOTNET_CLI_TELEMETRY_OPTOUT=1",
        ];

        Assert.DoesNotContain(settings, setting => !fixture.Env.Stdout.Contains(setting, StringComparison.Ordinal));
    }

    [Fact(DisplayName = "dotnet new console, a NuGet restore and dotnet run work")]
    public void ConsoleAppRuns() =>
        Assert.True(
            fixture.ConsoleApp.Output.Contains("Hello, World!", StringComparison.Ordinal),
            fixture.ConsoleApp.Describe());

    [Fact(DisplayName = "...with nothing blocked")]
    public void ConsoleAppHasNothingBlocked() =>
        Assert.True(
            fixture.ConsoleApp.OutputComplete
                && !fixture.ConsoleApp.Output.Contains("blocked:", StringComparison.Ordinal),
            fixture.ConsoleApp.Describe());

    [Fact(DisplayName = "a second restore finds the package in the cache volume (no download)")]
    public void SecondRestoreDownloadsNothing() =>
        Assert.True(
            fixture.SecondRestore.OutputComplete
                && !Regex.IsMatch(fixture.SecondRestore.Output, "GET https://[^ ]*humanizer", RegexOptions.IgnoreCase),
            fixture.SecondRestore.Describe());

    [Fact(DisplayName = "NUGET_PACKAGES is the host's path, backed by the project's volume")]
    public void NuGetPackagesIsTheHostsPath() =>
        Assert.Equal(
            $"{SmokeEnvironment.NuGetPackages} humanizer.core ",
            string.Concat(fixture.NuGetPackages.StdoutLines.Take(2).Select(line => line + " ")));

    [Fact(DisplayName = "host build and run after the sandbox's build")]
    public void HostBuildsAfterTheSandbox()
    {
        var hostBuild = RequireHostDotNet().HostBuild;

        Assert.True(hostBuild.ExitCode == 0, hostBuild.Describe());
    }

    [Fact(DisplayName = "...then the sandbox builds again")]
    public void SandboxBuildsAfterTheHost()
    {
        var sandboxBuild = RequireHostDotNet().SandboxBuild;

        Assert.True(
            sandboxBuild.Output.Contains("Hello, World!", StringComparison.Ordinal), sandboxBuild.Describe());
    }

    [Fact(DisplayName = "a new web app builds and serves on the forwarded port 8080")]
    public void WebAppServesOnTheForwardedPort()
    {
        Assert.SkipWhen(fixture.WebApp == null, "port 8080 is in use on this machine: skipped the web app check");

        Assert.True(fixture.WebApp.Served == "Hello World!", fixture.WebApp.Session.Describe());
    }

    /// <summary>Gets the first field of a line split on spaces, as cut -d' ' -f does (fields count from 0).</summary>
    private static string FieldOf(string? line, int field) => line?.Split(' ').ElementAtOrDefault(field) ?? "";

    /// <summary>Formats the major versions of dotnet --list-sdks lines as the session's majors are: each line's first
    /// word up to its first dot, sorted numerically, once each, each followed by a space.</summary>
    private static string FormatMajors(IEnumerable<string> lines)
    {
        var majors = lines
            .Select(line => line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "")
            .Select(word => word.Split('.')[0])
            .Distinct()
            .OrderBy(major => int.TryParse(major, out var number) ? number : 0)
            .ThenBy(major => major, StringComparer.Ordinal);
        return string.Concat(majors.Select(major => major + " "));
    }

    private HostRoundTrip RequireHostDotNet()
    {
        Assert.SkipWhen(fixture.RoundTrip == null, "no .NET SDK on the host: skipped the host/sandbox round trip");
        return fixture.RoundTrip;
    }

    /// <summary>A build of the sandbox's app on the host, then in the sandbox again.</summary>
    /// <param name="HostBuild">dotnet build and run on the host.</param>
    /// <param name="SandboxBuild">dotnet build and run in a session afterwards.</param>
    internal sealed record HostRoundTrip(CommandResult HostBuild, CommandResult SandboxBuild);

    /// <summary>A web app session forwarding port 8080, and what the host got from it.</summary>
    /// <param name="Session">The session.</param>
    /// <param name="Served">The body curl got from 127.0.0.1:8080 on the host.</param>
    internal sealed record WebAppResult(CommandResult Session, string Served);

    /// <summary>Runs sessions with and without the dotnet tool at several versions, builds and restores a console
    /// app, builds it on the host when there's a .NET SDK, and serves a web app on port 8080 when it's free.</summary>
    public sealed class Fixture : SmokeFixture
    {
        private const int WebPort = 8080;

        private const string NuGetFunction = """
            nuget() { curl -s -o /dev/null -w "%{http_code} %{http_connect}" -I https://api.nuget.org/v3/index.json; }
            """;

        internal CommandResult WithoutTool { get; private set; } = null!;

        internal CommandResult DotNet8 { get; private set; } = null!;

        internal CommandResult NuGet { get; private set; } = null!;

        internal CommandResult DefaultSdks { get; private set; } = null!;

        internal CommandResult BothSdks { get; private set; } = null!;

        internal CommandResult Env { get; private set; } = null!;

        internal CommandResult ConsoleApp { get; private set; } = null!;

        internal CommandResult SecondRestore { get; private set; } = null!;

        internal CommandResult NuGetPackages { get; private set; } = null!;

        /// <summary>Gets the host round trip, or null if there's no dotnet on the host.</summary>
        internal HostRoundTrip? RoundTrip { get; private set; }

        /// <summary>Gets the web app's result, or null if port 8080 was in use on the host.</summary>
        internal WebAppResult? WebApp { get; private set; }

        protected override void Prepare()
        {
            // Without tools in the user's config: each session names its own.
            Home.MakeToolFree();
            WithoutTool = Cli.Run("run", "bash", "-c", $"command -v dotnet pwsh || echo none; {NuGetFunction}; nuget");
            DotNet8 = Cli.Run("--tool", "dotnet:8", "run", "bash", "-c", """
                readlink -f $(command -v pwsh); pwsh -NoProfile -Command '$PSVersionTable.PSVersion.Major'; dotnet --list-sdks
                """);
            NuGet = RunWithDotNet($"{NuGetFunction}; nuget");
            DefaultSdks = RunWithDotNet("dotnet --list-sdks");
            BothSdks = RunWithDotNet("dotnet --list-sdks", "--tool", "dotnet:8");
            Env = RunWithDotNet("env");

            ConsoleApp = RunWithDotNet("""
                mkdir -p app && cd app && dotnet new console -o . --force >/dev/null && dotnet add package Humanizer.Core >/dev/null && dotnet run
                """);
            SecondRestore = RunWithDotNet("cd app && rm -rf obj bin && dotnet restore -v d");
            NuGetPackages = RunWithDotNet("echo $NUGET_PACKAGES; ls $NUGET_PACKAGES");
            if (SmokeEnvironment.FindOnPath("dotnet") != null)
                RoundTrip = RunHostRoundTrip();
            Files.DeleteTree(Path.Combine(Project.Dir, "app"));

            if (!IsPortOpen(WebPort))
                WebApp = RunWebApp();
        }

        /// <summary>Runs a session with the dotnet tool (dotnet:10 unless the options add another).</summary>
        private CommandResult RunWithDotNet(string script, params string[] options) =>
            Cli.Run(["--tool", "dotnet", .. options, "run", "bash", "-c", script]);

        private HostRoundTrip RunHostRoundTrip()
        {
            // With the real HOME, as the user would build.
            var host = PenCli.CreateForRealHome(Path.Combine(Project.Dir, "app")).Wrap("/bin/sh", "-c", """
                dotnet build -v q >/dev/null 2>&1 && dotnet run --no-build | grep -q 'Hello, World!'
                """).Run();
            var sandbox = RunWithDotNet("cd app && dotnet build -v q >/dev/null && dotnet run --no-build");
            return new HostRoundTrip(host, sandbox);
        }

        /// <summary>Builds and runs a new web app on ASPNETCORE_HTTP_PORTS (8080), forwarded to the host, and gets
        /// what the host reads from it.</summary>
        private WebAppResult RunWebApp()
        {
            var session = Track(Cli.Start("--tool", "dotnet", "--port", "8080", "run", "bash", "-c", """
                mkdir -p web && cd web && dotnet new web -o . --force >/dev/null && dotnet build -v q >/dev/null || exit 1
                dotnet run --no-build --no-launch-profile >/dev/null 2>&1 &
                for i in $(seq 120); do curl -s --noproxy "*" -o /dev/null localhost:8080 && break; sleep 0.5; done
                echo serving
                for i in $(seq 60); do [ -e .served ] && break; sleep 0.5; done
                """));
            Polling.WaitUntil(
                () => session.HasExited || session.OutputSoFar.Split('\n').Contains("serving"),
                TimeSpan.FromSeconds(300));
            var served = new HostCommand("curl", ["-s", "-m", "5", "http://127.0.0.1:8080/"]).Run().CapturedStdout;
            var web = Path.Combine(Project.Dir, "web");
            if (Directory.Exists(web))
                File.WriteAllText(Path.Combine(web, ".served"), "");
            var result = session.WaitForExit();
            Files.DeleteTree(web);
            return new WebAppResult(result, served);
        }

        /// <summary>Checks whether something on the host accepts connections on 127.0.0.1:port within 2 s.</summary>
        private static bool IsPortOpen(int port)
        {
            using var client = new TcpClient();
            try
            {
                return client.ConnectAsync("127.0.0.1", port).Wait(TimeSpan.FromSeconds(2)) && client.Connected;
            }
            catch (AggregateException)
            {
                return false;
            }
        }
    }
}
