namespace Pen.CommandLine;

/// <summary>Pen's usage and the help for each subcommand.</summary>
internal static class HelpText
{
    /// <summary>The usage shown by pen --help and after a command-line error.</summary>
    public const string Usage = """
        usage:
          pen [<options>]           interactive shell in this project's sandbox
                                    (your $SHELL if bash, zsh or fish, else bash)
          pen [<options>] <cmd…>    run a command in the sandbox, e.g. pen claude
          pen [<options>] run <cmd…>
                                    same, for a command whose name matches a subcommand
                                    --docker: with Docker-in-Docker (or docker = true in .pen/config.toml)
                                    --port <n>: forward localhost:<n> to the sandbox (repeatable;
                                    or ports = [<n>, …] in .pen/config.toml)
                                    --tool <spec>: a tool or agent, as name or name:version
                                    (repeatable; or tools = […] in either config.toml); a
                                    command named after an agent brings it: pen claude
          pen --tool list           list the agents and tools pen can install
          pen upgrade [--no-cache]  rebuild the base image, agents and tools; --no-cache skips the cache
          pen allow <domain…> [--global]
                                    allow domains, applied to running sessions too;
                                    pen allow '*' (quoted) allows any domain
          pen allow                 list recently blocked domains
          pen allow --defaults      list the domains pen allows out of the box
          pen deny <domain…> [--global]
                                    deny domains, over every allow, applied to running sessions too
          pen deny                  list the denied domains
          pen trust [--yes|--list]  review and approve what this project's .pen/ asks for
          pen ps                    list running sessions, in every project
          pen sign [<base>]         on the host: re-sign unpushed commits with your key
          pen clean                 start this project's sandbox fresh: logins, caches (asks first)
          pen clean --stale         remove what moved or deleted projects and old upgrades left
                                    behind, in every project (asks first)
          pen <subcommand> --help   more about one of them
        """;

    /// <summary>The help for each subcommand, shown by pen &lt;subcommand&gt; --help, keyed by its name.</summary>
    public static readonly Dictionary<string, string> Subcommands = new(StringComparer.Ordinal)
    {
        ["run"] = """
            usage: pen [--docker] [--port <n>]… [--tool <spec>]… [<cmd…>]
                   pen [--docker] [--port <n>]… [--tool <spec>]… run [--] <cmd…>

            Runs a command in this project's sandbox, or an interactive shell when there's
            none (your $SHELL when it's bash, zsh or fish, otherwise bash), e.g. pen claude,
            pen codex, pen run npm ci. Everything after the command's name is passed to it
            unchanged, including options like --help. Use pen run when the command's name
            is also one of pen's subcommands (pen run clean).

            Run it from a repo root. Ctrl-C goes to the command; the session ends when it
            exits, and pen then lists any domains the proxy blocked.

              --docker   give the session its own Docker daemon (one per project at a
                         time); docker = true in .pen/config.toml does this for every session
              --port <n> forward port <n> on the host's 127.0.0.1 to port <n> in the
                         sandbox (repeatable); ports = [<n>, …] in .pen/config.toml does
                         this for every session. A server in the sandbox must listen on
                         0.0.0.0, not only localhost
              --tool <spec>
                         a tool or agent, as name or name:version (repeatable); tools = […]
                         in either config.toml adds to these, and pen --tool list shows what's
                         available. The session runs with them installed, and can reach the
                         domains they need. A command named after an agent (pen claude, pen
                         run claude) brings that agent at its default version unless --tool
                         or tools in either config.toml already names it; to have an agent in
                         a shell or behind another command, name it here or in tools
            """,
        ["upgrade"] = """
            usage: pen upgrade [--no-cache]

            Rebuilds the base image, always as a new image, then pulls newer proxy and
            Docker-in-Docker images. Agents and tools are built on the base, so all of them
            are rebuilt: the tools and agents in your config right away, and run in a
            project, the approved tools and .pen/Dockerfile there (see pen trust); any other
            set of tools, such as an agent a command brings, on its next use. A rebuild
            installs an agent or tool at latest at its newest release, and a pinned one at
            the version given. Logins and config are kept.

              --no-cache rebuild without the build cache
            """,
        ["allow"] = """
            usage: pen allow
                   pen allow <domain…> [--global]
                   pen allow --defaults

            With no arguments, lists domains the proxy blocked in this project's sessions
            (last 7 days) that still aren't allowed. With deny or open egress, it lists
            apart the raw IPs blocked (in open egress, any name blocked anyway, such as one
            that resolves to a private address) and the blocked hosts deny covers now.

            With domains, adds them to allow in .pen/config.toml (creating it if needed),
            approves them for this directory (see pen trust) and applies them to this
            project's running sessions. A leading dot (.example.com) also allows subdomains.
            pen allow '*', quoted so the shell doesn't expand it, allows any domain name
            (never a raw IP, nor a name that resolves to a private address); deny still
            wins over it (see pen deny).

              --global     add them to ~/.config/pen/config.toml instead, for every
                           project, and apply them to all running sessions
              --defaults   list the domains pen allows out of the box
            """,
        ["deny"] = """
            usage: pen deny
                   pen deny <domain…> [--global]

            With no arguments, lists the denied domains, the config file each is in, and
            which of pen's defaults or the agents' and tools' domains each takes.

            With domains, adds them to deny in .pen/config.toml (creating it if needed) and
            applies them to this project's running sessions. Deny wins over every allow:
            the defaults, either config.toml, agents' and tools' domains, and '*'. A leading
            dot (.example.com) also denies subdomains; gist.github.com denies one host under
            an allowed .github.com.

              --global     add them to ~/.config/pen/config.toml instead, for every
                           project, and apply them to all running sessions
            """,
        ["trust"] = """
            usage: pen trust [--yes | --list]

            .pen/ can be written by an agent, so what it asks for needs your approval first:
            allow domains not already allowed, tools with the domains they bring, docker = true,
            a github_owner that differs from origin's, and a .pen/Dockerfile with the rest of
            .pen/ it builds from.
            pen trust lists what's new in this directory and asks, as a session would.
            Approved values aren't asked about again; a changed .pen/Dockerfile or other
            file there is. Approvals are per directory, so a new worktree starts with none.

              --yes      approve without asking, for scripts
              --list     list everything new, however long, and approve nothing
            """,
        ["ps"] = """
            usage: pen ps

            Lists the running sessions of every project: the project's directory, the
            session's id (its containers are named pen-<dir>-<hash>-<id>-…), docker for
            a session with Docker-in-Docker, open for one whose egress is open (allow = ["*"]),
            its ports and tools, how long it has been running, and its command.
            """,
        ["sign"] = """
            usage: pen sign [<base>]

            Re-creates the commits in <base>..HEAD with your own signature, using your
            normal git signing setup, so agent commits (unsigned in the sandbox) can be
            pushed signed. Trees, authors and messages are kept; hashes change, and the
            old commits stay in the reflog. Runs on the host, not in the sandbox.

            <base> defaults to the branch's upstream (or origin/HEAD). pen refuses to
            rewrite commits that are already on a remote branch, and won't run while a
            session is open in this directory or pen clean runs. It doesn't refuse for a
            session in another worktree of the repo, but that session's agent can change
            what pen sign reads, so wait for it to end too.
            """,
        ["clean"] = """
            usage: pen clean
                   pen clean --stale

            With no arguments, starts this project's sandbox fresh, after asking: removes
            its home (agent logins and config, shell history), its NuGet cache and its
            Docker-in-Docker images. The project's files, ~/.config/pen, and the project's
            approvals (pen trust) and blocked-domain logs (pen allow) are kept. A repo's
            linked worktrees share the sandbox, so it refuses while a session or pen sign
            runs in the main checkout or any of its worktrees.

              --stale    instead, across every project, remove what projects you have
                         moved or deleted left behind (sandbox, project-layer images,
                         approvals and logs) and tools images built on an old base image.
                         Projects still in use are listed and left alone. A project
                         whose directory is gone may be on a volume that isn't mounted:
                         mount it first. Projects pen hasn't run in since it began
                         recording their directories are listed with the commands to
                         remove them by hand, never removed. A project with an open
                         session is skipped.
            """,
    };
}
