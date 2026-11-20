# Using Pen

Start here for day-to-day use. For installation, see the [README](../README.md#get-started); for sandbox boundaries, read the [limitations](limitations.md).

- [Running a session](#running-a-session)
- [Git in the sandbox](#git-in-the-sandbox)
- [Network](#network)
- [Configuration](#configuration): [logins](#logins-and-api-keys), [GitHub](#github), [agent settings](#agent-config-the-home-seed), [project settings](#project-settings), [file hiding](#hiding-files-from-the-agent)
- [Tools](#tools)
- [Dev servers](#dev-servers)
- [Docker-in-Docker](#docker-in-docker)
- [Upgrading and cleaning up](#upgrading-and-cleaning-up)
- [Commands](#commands)
- [Differences from running on the host](#differences-from-running-on-the-host)

## Running a session

Run Pen from your project root. No `.pen/` config or Dockerfile is required:

```sh
pen claude                  # or: pen codex, pen opencode, pen pi
pen                         # interactive shell
pen run npm ci              # run one command
pen ps                      # list sessions across projects (from the host)
```

The first session builds the sandbox image and installs the agent you named. Log in inside the sandbox when prompted. Logins, settings and caches persist per repo, including across linked worktrees. A plain `pen` opens bash, zsh or fish to match your host's `$SHELL`, falling back to bash.

**Project files are live and agents run with permission prompts off.** Keep host Git, IDE Git integrations and Git-aware prompts out of the checkout and all its worktrees until every session ends. See [what Pen protects](limitations.md#while-a-session-runs).

Ctrl-C goes to the program inside the sandbox. When the program exits, Pen ends the session and reports blocked domains and Git warnings. Read the warnings before you review files or commits on the host.

### Options and arguments

- Put Pen's options before the program name. Pen passes everything after that name to the program, so `pen --tool go claude --help` shows Claude's help.
- Use `pen run` when a program shares a name with a Pen subcommand, such as `pen run clean`. Options can follow `run`, as in `pen run --docker claude`.
- Use `pen run --` before a program name that begins with a dash.

## Git in the sandbox

Agents can fetch, commit, branch, merge and rebase. Pen mounts Git config and hooks read-only, so set up remotes, hooks, worktrees and submodules on the host between sessions. That includes commands such as `git remote add` and hook installers.

When all sessions in the repo have ended:

1. Read Pen's warnings.
2. Review the changes and commits on the host.
3. Optionally run [`pen sign`](#commit-signing).
4. Push from the host.

If Pen was killed, run `pen true` from that checkout to finish recovery before using host Git. If Pen reports a cleanup or check failure, follow its recovery advice. See [session-end checks and recovery](git-safety.md#at-session-end).

### Commit signing

After all sessions end, sign the agent's unpushed commits with your host signing key:

```sh
pen sign
git push
```

`pen sign` signs commits since the current branch's upstream, or since `origin/HEAD` if there's no upstream. Use `pen sign <base>` to choose the base. Pen keeps trees, authors and messages, but recreated commits get new hashes. It refuses to rewrite commits already on a remote branch, or to run from a detached HEAD.

For SSH signing, set `gpg.ssh.allowedSignersFile` in your global Git config to a file outside the repo, using an absolute path or `~/…`. Pen verifies the signatures it creates with your global Git settings. If it can't verify them, it may sign those commits again on the next run.

### Worktrees

Create linked worktrees between sessions, then start an agent from each checkout:

```sh
git worktree add ../feature -b feature
cd ../feature
pen claude
```

Worktrees share Git data, logins, caches and the one-session Docker-in-Docker limit. Each has its own `.pen/` settings and approvals, and a local `pen allow` or `pen deny` updates sessions in that worktree only. Wait for **all** worktree sessions to end before running Git or `pen sign` on the host. Worktrees of bare repos are unsupported.

### Submodules

Initialize submodules on the host with `git submodule update --init --recursive` before starting Pen. Agents can work and commit inside an initialized submodule. If Pen refuses a submodule with its own `.git` directory, run `git submodule absorbgitdirs` on the host. Pen supplies only the selected GitHub owner's token, so private submodules under another owner need their own access.

## Network

Outbound HTTP and HTTPS traffic goes through a proxy that allows only listed domains. Other outbound protocols are blocked. The defaults cover common developer services including GitHub, npm and PyPI. Agents and tools add the domains they need when selected.

If a download or API call fails, run these commands on the host from the same project directory:

```sh
pen allow                       # show recently blocked domains
pen allow example.com           # allow one hostname
pen allow .example.com          # include subdomains
pen allow example.com --global  # allow it in every project
pen allow --defaults            # list Pen's built-in domains
```

Then retry the request in the agent. Pen saves the rule and updates running sessions in the same directory. With `--global`, it saves the rule in your user config and updates sessions across projects. If a live update fails, repeat the command even if the rule was already saved. To remove a rule, edit the config file that contains it and restart the affected sessions.

Allowed destinations can receive uploads. See [network and credential limits](limitations.md#network-and-credentials).

### Denying domains

```sh
pen deny gist.github.com
pen deny                        # list denied domains and where they came from
```

Deny takes precedence over defaults, tool domains and your own allow rules. An exact deny such as `gist.github.com` blocks that host even when `.github.com` is allowed. Use a leading dot to include subdomains, or `--global` to apply the rule across projects. Like `pen allow`, `pen deny` saves the rule and updates running sessions.

Rules match the hostname requested through the proxy, not what's sent over the connection. See [matching by name](limitations.md#matching-by-name).

### Open egress

If the project needs arbitrary domains, allow all of them with a quoted star. Add `--global` to open egress across projects.

```sh
pen allow '*'
```

Open egress still blocks:

- raw IP addresses and names that fail to resolve
- private destinations reached through the wildcard
- anything in `deny`

A domain allowed by name can still resolve to a private address. A project asking for `*` needs approval unless your user config already allows `*`. Pen warns at startup and shows `open` under EGRESS in `pen ps`. [Read the limits of open egress](limitations.md#open-egress).

## Configuration

| Location | What goes there |
|---|---|
| `~/.config/pen/config.toml` | Personal tools, network rules, hide patterns, GitHub tokens and environment variables |
| `~/.config/pen/home/` | Shared agent settings, instructions and shell config |
| `.pen/config.toml` | Project tools, network rules, hide and replace rules, ports, Docker and GitHub owner |
| `.pen/Dockerfile` | Extra software the project needs |

Pen creates `~/.config/pen/` on first run. Both config files use TOML and reject unknown keys. Keep secrets in your user config, never in `.pen/`.

The `allow`, `deny`, `tools` and `hide` lists add together across files. Edits take effect at the next session. To change network rules in running sessions, use `pen allow` and `pen deny`.

Keep frequently used tools and shared agent settings in user config; put the setup a project needs in `.pen/`. For example:

```toml
# .pen/config.toml
tools = ["go"]
allow = ["docs.example.com"]
ports = [5173]
hide = [".env", "secrets/"]
```

Run `pen init` for a commented starting point. Review it before committing, then see [project approvals](#approving-project-settings).

### Logins and API keys

Log in inside the sandbox, once per repo. For Claude, start `pen claude` and follow its prompts. For Codex, run `pen codex login --device-auth`.

To pass an API key or token instead, put it under `[env]` in your user config. Every session receives these values.

```toml
# ~/.config/pen/config.toml
[env]
CLAUDE_CODE_OAUTH_TOKEN = "…"
```

Run `claude setup-token` to get a Claude token for this setup.

### GitHub

For private repos, create a fine-grained token for the owner in the `origin` remote:

- Scope it to the repositories you need.
- Give it read-only Contents access.
- Add read-only Issues or Pull requests access if the agent needs those.

Add the token to your user config, keyed by owner:

```toml
# ~/.config/pen/config.toml
[github]
my-org = "github_pat_…"
```

Pen supplies only the selected owner's token to Git and `gh`. To select an owner explicitly, set `github_owner = "my-org"` in project config. An owner different from `origin`'s needs approval.

Pen passes the token's permissions through unchanged, so a write-capable token lets the agent push. GitHub SSH remotes are rewritten to HTTPS inside the sandbox so Git can use the token; your host remotes stay as they were.

### Agent config (the home seed)

Put shared settings under `~/.config/pen/home/` using their usual home-relative paths. For example, `~/.config/pen/home/.codex/AGENTS.md` becomes `~/.codex/AGENTS.md` in the sandbox.

| Agent | Instructions | MCP server settings |
|---|---|---|
| Claude Code | `.claude/CLAUDE.md` | `mcpServers` in `.claude.json` |
| Codex | `.codex/AGENTS.md` | `[mcp_servers.<name>]` in `.codex/config.toml` |
| OpenCode | `.config/opencode/AGENTS.md` | `mcp` in `.config/opencode/opencode.json` |
| Pi | `.pi/agent/AGENTS.md` | `.pi/agent/mcp.json` |

Use the seed's `.gitconfig` to set the identity for sandbox commits.

- Seed files replace matching sandbox copies on each start.
- Removing a seed file leaves its last sandbox copy. To remove it entirely, delete that copy inside the sandbox too.
- For Claude's `.claude.json`, which also holds login state, the seed supplies only the `mcpServers` map and leaves other fields alone.
- Known agent credential files and `.docker/` are skipped.

### Project settings

Commit `.pen/config.toml` to share tools, network rules and other project settings. Keep tokens and `[env]` in your user config; project config doesn't accept them.

#### Starting with `pen init`

`pen init` writes a commented `.pen/config.toml` and leaves an existing file in place. It looks up to three directories deep, skipping common build and dependency directories, and suggests:

- tools for .NET, Go, Rust and Java
- hide rules for files such as `.env`, private keys and `secrets/`, while leaving examples such as `.env.example` visible
- Docker and replacement settings, as comments

Review the file before committing it.

#### Approving project settings

A checkout can contain settings someone else wrote. Pen asks before a session uses any of these from `.pen/`:

- new allowed domains
- tools and tool versions, with the domains they add
- `docker = true`
- a `github_owner` different from `origin`'s owner
- a `.pen/Dockerfile` and its build context

Port forwarding, deny rules, hiding and replacement need no approval.

```sh
pen trust --list    # show pending requests
pen trust           # show the requests and ask for approval
pen trust --yes     # approve all pending requests without prompting
```

Approvals are per directory, including for linked worktrees. In a noninteractive session, Pen refuses settings that still need approval. `pen init` creates no approvals. `pen allow <domain>` without `--global` saves and approves the rule for that directory.

#### Extra software with a Dockerfile

Add `.pen/Dockerfile` for software that isn't available as a [tool](tools.md):

```dockerfile
FROM pen:latest
RUN apt-get update && apt-get install -y --no-install-recommends postgresql-client \
 && rm -rf /var/lib/apt/lists/*
```

`FROM pen:latest` includes your selected tools. Pen starts your requested program or shell instead of the Dockerfile's `CMD`.

**Build steps have direct network access, outside the session allowlist.** Pen shows the Dockerfile and a private snapshot of its build context for review, then builds from that approved snapshot. The context is `.pen/`, excluding `config.toml`. Changes to the context need another approval.

### Hiding files from the agent

Use `hide` in user or project config to show empty, read-only files or directories in the sandbox. Use `replace` in project config to show a read-only copy of a different file instead. Neither changes the files on the host.

```toml
# .pen/config.toml
hide = [".env", "secrets/", "**/*.pem"]
replace = { ".env" = ".pen/agent.env" }
```

This hides files named `.env`, directories named `secrets`, and PEM files at any depth. The project-root `.env` shows the contents of `.pen/agent.env` instead. Create that file yourself with safe values, and make sure the target already exists. A replacement wins over a hide rule for the same file, but a file inside a hidden directory can't be replaced.

Hide patterns match paths like this:

- A slash at the start or in the middle anchors the match to the project root.
- A trailing slash matches directories.
- `*` and `?` match within a path segment, and `**` as its own segment spans directories.
- Matching ignores case.

Pen reports what it hid or replaced when the session starts. These mounts have [filesystem limits and gaps](limitations.md#hidden-and-replaced-files), and Docker-in-Docker can bypass them.

<details>
<summary>Detailed pattern and replacement rules</summary>

- Character classes, backslashes, comments, negation, empty segments and `.` or `..` segments are invalid in hide patterns. Pen refuses patterns that hide the whole project or explicitly target `.git` or the top-level `.pen/`.
- A matched symlink can hide its target inside the project. Pen leaves links to outside the project or to missing paths alone. Hide patterns don't traverse symlinked directories. Read startup warnings for hard links, paths that couldn't be listed, and filesystem case or Unicode behavior.
- The replacement target and source are project-relative paths. Both must be regular files reached without symlinks. The target must be outside `.git/` and `.pen/`. The source must be outside `.git/`, visible under the hide rules, at most 1 MB, and have no other hard links. Pen copies it at session start.
- Use the inline `replace = { … }` form above, or put a `[replace]` table after all top-level keys. If you have a `.pen/Dockerfile`, a source in `.pen/` is part of the approved build context.
- A hide or replace mount can't overlap a mount Pen uses to protect Git. Pen allows up to 1,000 hide and replace mounts together. Hide a directory to cover its contents with one mount.

</details>

## Tools

Node LTS, npm, Python, pip, uv, Git and C build tools are in every session. Add other toolchains for one session with `--tool`, or every time with `tools` in config:

```sh
pen tools                     # list what's available
pen --tool go claude
pen --tool dotnet:10 codex
```

Pen offers .NET, Go, Rust, Java and CodeGraph as tools. See [tools and agents](tools.md) for versions, agent pinning, Node and Python packages, and setup notes for each toolchain.

## Dev servers

Forward a port, start the server inside the sandbox bound to `0.0.0.0`, and open it on the host:

```sh
pen --port 5173 claude
# inside the sandbox:
npx vite --host 0.0.0.0
# on the host: http://localhost:5173
```

Pen binds the same port on host loopback. Repeat `--port` for several ports, or set `ports = [5173]` in `.pen/config.toml` to forward it every time. Run `pen ps` to see active ports.

If a port is already in use, an explicit `--port` fails, while a configured port is skipped with a warning.

For ASP.NET Core on port 5000, forward `--port 5000` and set `ASPNETCORE_URLS=http://0.0.0.0:5000` inside the sandbox.

## Docker-in-Docker

For Testcontainers, Compose or image builds, give the session its own Docker daemon with `pen --docker claude`. To make it the project default, set `docker = true` in `.pen/config.toml`.

**The agent controls a privileged daemon in this mode.** It can bypass Git protections and file-hiding mounts. Enable it only for projects that need it. See [Docker-in-Docker limitations](limitations.md#docker-in-docker-and-the-git-protections).

- One Docker-in-Docker session can run per repo, including all its worktrees.
- The daemon is at `DOCKER_HOST=tcp://dind:2375` and uses the session proxy. Published ports are at `dind:<port>` inside the sandbox.
- Add service names to `NO_PROXY` when containers need to talk to each other.
- On rootful Linux, pass `--user` when child containers need to write to the checkout as you.

## Upgrading and cleaning up

### Update Pen itself

With Homebrew, run `brew upgrade pen`. With a release binary, download the new archive from [Releases](https://github.com/mmonteleone/pen/releases) and replace `pen` as you [installed it](../README.md#install). From a source checkout, run `git pull` and `cli/install.sh`. If the bundled base image files changed, the next session rebuilds the image.

### Update agents and tools

Run `pen upgrade` to rebuild the base image and pull newer proxy and Docker-in-Docker images. It rebuilds the tools and agents from your config and from the current project's approved tools and Dockerfile. Other combinations rebuild on their next use. Agents set to `latest` refresh, and exact pins stay pinned. Use `pen upgrade --no-cache` to bypass the build cache. Logins, config and the Pen CLI version stay as they were.

### Clean up

Both commands show what they'll remove and ask before deleting project data or old images.

| Command | Removes |
|---|---|
| `pen clean` | This repo's sandbox home, including agent logins and shell history, plus its NuGet cache and Docker-in-Docker data |
| `pen clean --stale` | State and images left by moved or deleted projects, plus tool images built on an older base |

`pen clean` keeps project files, personal config, project approvals and blocked-domain logs. It applies across worktrees and refuses to run while a session or `pen sign` is active in the repo.

Before running `pen clean --stale`, mount any external drives that hold projects, since a missing mount looks like a deleted project. Pen lists state it can't identify so you can review it manually.

#### Projects with an unfinished session

`pen clean --stale` skips a moved or deleted project whose session hasn't ended, because its saved copies of protected Git files may be the only good ones. **Keep its session files until you've recovered those copies.**

1. Exit the agent if it's still running.
2. If possible, restore the project to its recorded path and run `pen clean` there to finish recovery. Decline the cleanup prompt if you want to keep the sandbox.
3. If you can't put the project back, use the paths Pen prints to save the protected copies and compare them with the project's Git files. Then follow the printed cleanup steps. This manual path skips Pen's Git check.

If Pen reports an unreadable session, check its permissions and retry.

## Commands

Run `pen --help` for the full CLI help or `pen <subcommand> --help` for one command.

| Command | What it does |
|---|---|
| `pen [options] [command…]` | Open a shell or run a program in the sandbox |
| `pen run [options] [--] <command…>` | Run a program explicitly, including one named like a Pen subcommand |
| `pen ps`, `pen ls` | List sessions, ports, tools and egress mode across projects |
| `pen tools` | List available tools, agents and their default versions |
| `pen init` | Create a project config from a template and detected settings |
| `pen allow` | List domains blocked in the last seven days that are still restricted |
| `pen allow --defaults` | List the built-in allowed domains |
| `pen allow <domain…> [--global]` | Save and apply allow rules |
| `pen deny` | List denied domains and affected allow rules |
| `pen deny <domain…> [--global]` | Save and apply deny rules |
| `pen trust`, `pen trust --list`, `pen trust --yes` | Review or approve requested project settings |
| `pen sign [<base>]` | Sign unpushed commits with your host key |
| `pen upgrade [--no-cache]` | Rebuild the base, agent and tool images |
| `pen clean [--stale]` | Reset this sandbox or clear stale state across projects |

| Option | Short | Applies to |
|---|---|---|
| `--docker` | `-d` | Sessions |
| `--port <port>` | `-p` | Sessions, repeatable |
| `--tool <name[:version]>` | `-t` | Sessions, repeatable |
| `--global` | `-g` | `allow`, `deny` |
| `--yes` | `-y` | `trust` |

Write short options separately, as in `pen -d -p 5173 claude`.

## Differences from running on the host

- Pasting images into the sandboxed agent is unsupported. Save images in the project and give the agent their paths.
- Forward ports to reach sandbox servers from your browser. See [dev servers](#dev-servers).
- The proxy carries HTTP and HTTPS only. GitHub SSH remotes are rewritten to HTTPS.
- Use current time zone names such as `America/Los_Angeles`. To use a legacy name such as `US/Pacific`, install `tzdata-legacy` in a [project Dockerfile](#extra-software-with-a-dockerfile).
