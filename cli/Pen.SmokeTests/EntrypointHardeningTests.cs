using System.Text.RegularExpressions;

namespace Pen.SmokeTests;

/// <summary>Who the agent runs as and what it can't do: capabilities, limits, mounts and the home volume.</summary>
public sealed class EntrypointHardeningTests(EntrypointHardeningTests.Fixture fixture)
    : IClassFixture<EntrypointHardeningTests.Fixture>
{
    private static uint AgentUid => SmokeEnvironment.AgentUid;

    private static uint AgentGid => SmokeEnvironment.AgentGid;

    private CommandResult Session => fixture.Hardening;

    [Fact(DisplayName = "runs as the host user (inside: $AGENT_UID:$AGENT_GID)")]
    public void RunsAsTheHostUser() =>
        AssertLine($"id={AgentUid}:{AgentGid}", $"runs as the host user (inside: {AgentUid}:{AgentGid})");

    [Fact(DisplayName = "user $AGENT_USER, HOME=/home/agent")]
    public void UserAndHome() =>
        AssertLine(
            $"user={SmokeEnvironment.AgentUser} home=/home/agent",
            $"user {SmokeEnvironment.AgentUser}, HOME=/home/agent");

    [Fact(DisplayName = "CapInh empty")]
    public void NoInheritableCapabilities() => AssertLine("CapInh:\t0000000000000000");

    [Fact(DisplayName = "CapPrm empty")]
    public void NoPermittedCapabilities() => AssertLine("CapPrm:\t0000000000000000");

    [Fact(DisplayName = "CapEff empty")]
    public void NoEffectiveCapabilities() => AssertLine("CapEff:\t0000000000000000");

    [Fact(DisplayName = "CapBnd empty")]
    public void NoBoundingCapabilities() => AssertLine("CapBnd:\t0000000000000000");

    [Fact(DisplayName = "CapAmb empty")]
    public void NoAmbientCapabilities() => AssertLine("CapAmb:\t0000000000000000");

    [Fact(DisplayName = "no_new_privs set")]
    public void NoNewPrivsIsSet() => AssertLine("NoNewPrivs:\t1");

    [Fact(DisplayName = "capsh: empty bounding set")]
    public void CapshShowsAnEmptyBoundingSet() => AssertLine("Bounding set =");

    [Fact(DisplayName = "no sudo")]
    public void HasNoSudo() => AssertLine("sudo=absent");

    [Fact(DisplayName = "PID 1 is tini, running as the agent's UID")]
    public void PidOneIsTini() => AssertLine($"pid1=tini pid1uid={AgentUid}");

    [Fact(DisplayName = "the image's files can't be changed (/usr/local)")]
    public void ImageFilesAreProtected() => AssertLine("rootfs=protected");

    [Fact(DisplayName = "rootless: securebits noroot set and locked")]
    public void RootlessSecurebitsAreLocked()
    {
        Assert.SkipUnless(SmokeEnvironment.IsRootless, "rootful Docker: the agent isn't container root");

        // noroot, no_setuid_fixup, both locked, keep_caps_locked
        Assert.Matches(new Regex("^Securebits: [0-7]+/0x2f/", RegexOptions.Multiline), Session.CapturedStdout);
    }

    [Fact(DisplayName = "rootless: the root filesystem is read-only")]
    public void RootlessRootFilesystemIsReadOnly()
    {
        Assert.SkipUnless(SmokeEnvironment.IsRootless, "rootful Docker: the agent isn't container root");

        Assert.Contains("rootfs=protected", Session.CapturedStdout, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "rootless: IS_SANDBOX=1 (Claude's skip-permissions as root)")]
    public void RootlessSetsIsSandbox()
    {
        Assert.SkipUnless(SmokeEnvironment.IsRootless, "rootful Docker: the agent isn't container root");

        AssertLine("is_sandbox=1");
    }

    [Fact(DisplayName = "pids_limit applied")]
    public void PidsLimitIsApplied() => AssertLine("pids.max=4096");

    [Fact(DisplayName = "no core dumps (soft and hard limit 0)")]
    public void NoCoreDumps() => AssertLine("core=0/0");

    [Fact(DisplayName = "new home volume owned by the agent")]
    public void HomeVolumeIsTheAgents() => AssertLine($"home_owner={AgentUid}:{AgentGid}");

    [Fact(DisplayName = "new NuGet volume owned by the agent")]
    public void NuGetVolumeIsTheAgents() => AssertLine($"nuget_owner={AgentUid}:{AgentGid}");

    [Fact(DisplayName = "no docker.sock")]
    public void HasNoDockerSocket() => AssertLine("docker.sock=absent");

    [Fact(DisplayName = "/tmp is exec")]
    public void TmpIsExec() => AssertLine("tmp-exec-ok");

    [Fact(DisplayName = "working dir is the project path")]
    public void WorkingDirIsTheProject() => AssertLine($"pwd={fixture.Project.Dir}");

    [Fact(DisplayName = "git status: no dubious-ownership error")]
    public void GitStatusWorks() => AssertLine("git-status-ok");

    [Fact(DisplayName = "file created inside is owned by the host user")]
    public void CreatedFileIsTheHostUsers() =>
        Assert.True(
            fixture.CreatedInsideOwner == SmokeEnvironment.HostUid,
            $"owner {fixture.CreatedInsideOwner?.ToString() ?? "(no file)"}, host user {SmokeEnvironment.HostUid}");

    [Fact(DisplayName = "a repo with commit.gpgsign=true still has signing off")]
    public void SigningStaysOff() => AssertLine("gpgsign_effective=false");

    [Fact(DisplayName = "under $home_root only the project and NuGet paths exist")]
    public void OnlyTheProjectAndNuGetPathsAreUnderTheHomeRoot()
    {
        var project = fixture.Project.Dir;
        var nuget = SmokeEnvironment.NuGetPackages;
        List<string> stray = [];
        foreach (var path in fixture.HomeRootListing.StdoutLines)
        {
            if (path.Length == 0 || (project + "/").StartsWith(path + "/", StringComparison.Ordinal)
                || (nuget + "/").StartsWith(path + "/", StringComparison.Ordinal))
            {
                continue;
            }
            if (path != "/home/agent" && path != project && path != nuget)
                stray.Add(path);
        }

        Assert.True(
            stray.Count == 0,
            $"under {fixture.HomeRoot} only the project and NuGet paths exist (stray: {string.Join(' ', stray)})");
    }

    [Fact(DisplayName = "home volume persists across sessions")]
    public void HomeVolumePersists() => Assert.Equal("hi", fixture.Persisted);

    private void AssertLine(string line) => AssertLine(line, $"no line \"{line}\"");

    private void AssertLine(string line, string message) =>
        Assert.True(Session.HasStdoutLine(line), $"{message}; {Session.Describe()}");

    /// <summary>Runs the hardening probe with commit.gpgsign set in the repo, then lists the host's home root
    /// from the sandbox and writes and reads a file in the home volume, in sessions of their own.</summary>
    public sealed class Fixture : SmokeFixture
    {
        /// <summary>Gets the session that printed what the agent is and can do, one fact per line.</summary>
        internal CommandResult Hardening { get; private set; } = null!;

        /// <summary>Gets the owner on the host of a file the agent created in the project, or null if
        /// there's none.</summary>
        internal uint? CreatedInsideOwner { get; private set; }

        /// <summary>Gets the host's home root, e.g. /Users.</summary>
        internal string HomeRoot { get; private set; } = null!;

        /// <summary>Gets the session that listed everything under the home root but the project, NuGet and
        /// /home/agent.</summary>
        internal CommandResult HomeRootListing { get; private set; } = null!;

        /// <summary>Gets what a second session read from the file a first one wrote in $HOME, as $(…) captures
        /// both sessions' stdout.</summary>
        internal string Persisted { get; private set; } = null!;

        protected override void Prepare()
        {
            var createdInside = Path.Combine(Project.Dir, "created-inside");
            Project.RunGit("config", "commit.gpgsign", "true");
            Hardening = Cli.Run("run", "bash", "-c", """
                echo "id=$(id -u):$(id -g)"
                echo "user=$(id -un) home=$HOME"
                grep -E "^(CapInh|CapPrm|CapEff|CapBnd|CapAmb|NoNewPrivs):" /proc/self/status
                capsh --print | grep -E "^Bounding set"
                command -v sudo >/dev/null && echo "sudo=present" || echo "sudo=absent"
                echo "pid1=$(cat /proc/1/comm) pid1uid=$(stat -c %u /proc/1)"
                echo "pids.max=$(cat /sys/fs/cgroup/pids.max 2>/dev/null)"
                echo "core=$(ulimit -c)/$(ulimit -Hc)"
                capsh --print | grep -E "^Securebits:"
                touch /usr/local/bin/x 2>/dev/null && echo "rootfs=writable" || echo "rootfs=protected"
                echo "is_sandbox=${IS_SANDBOX:-}"
                echo "home_owner=$(stat -c %u:%g /home/agent)"
                echo "nuget_owner=$(stat -c %u:%g "$NUGET_PACKAGES")"
                test -e /var/run/docker.sock && echo "docker.sock=present" || echo "docker.sock=absent"
                printf "#!/bin/sh\necho tmp-exec-ok\n" >/tmp/x.sh && chmod +x /tmp/x.sh && /tmp/x.sh
                cd "$PROJECT_DIR" && echo "pwd=$PWD"
                git status >/dev/null 2>/tmp/gs.err && echo git-status-ok || cat /tmp/gs.err
                touch "$PROJECT_DIR/created-inside"
                echo "gpgsign_effective=$(git config commit.gpgsign)"
                """);
            CreatedInsideOwner = Files.GetOwnerUid(createdInside);
            File.Delete(createdInside);
            Project.RunGit("config", "--unset", "commit.gpgsign");

            // Only the project path and the NuGet path exist under the host's home root (/Users on the Mac,
            // /home on Linux, where /home/agent is the sandbox's own). This session runs with the real HOME, so a
            // path of it mounted at its host path would land under the home root too.
            HomeRoot = "/" + SmokeEnvironment.RealHome.Split('/')[1];
            HomeRootListing = PenCli.CreateForRealHome(Project.Dir).Run("run", "bash", "-c", $$"""
                find {{HomeRoot}} \( -path /home/agent -o -path "$PROJECT_DIR" -o -path "$NUGET_PACKAGES" \) -prune -o -print
                """);

            var write = Cli.Run("run", "bash", "-c", """echo hi >"$HOME/.persist" """);
            var read = Cli.Run("run", "bash", "-c", """cat "$HOME/.persist" """);
            Persisted = (write.Stdout + read.Stdout).TrimEnd('\n');
        }
    }
}
