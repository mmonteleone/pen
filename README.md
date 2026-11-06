# pen

`pen` (as in playpen) runs AI coding agents (Claude Code, Codex, OpenCode, Pi) in a container
sandbox instead of directly on your machine. In the sandbox an agent can:

- see only the project directory you run `pen` in;
- reach only an allowlist of domains, through a proxy;
- read from GitHub with a read-only token, but never push.

Its logins and config survive image rebuilds. It works the same on a Mac (OrbStack, arm64) and a
Linux server (Docker, amd64), and a project needs no files at all to use it.

```
cd ~/source/repos/myproject
pen claude        # or: pen codex, pen opencode, pen pi, pen (a shell), pen run <cmd…>
```

- [Setup](#setup)
- [Daily use](#daily-use)
- [Commands](#commands)
- [Network access](#network-access)
- [GitHub](#github)
- [Logging in](#logging-in)
- [Your agent config: the home seed](#your-agent-config-the-home-seed)
- [Project settings: `.pen/`](#project-settings-pen)
- [Commit signing](#commit-signing)
- [Upgrading and resetting](#upgrading-and-resetting)
- [Differences from running on the host](#differences-from-running-on-the-host)
- [Moving off host Claude](#moving-off-host-claude)
- [Docker-in-Docker](#docker-in-docker)
- [Threat model and limitations](#threat-model-and-limitations)
- [Recommended host settings](#recommended-host-settings)

## Setup

**Prerequisites**
- **Mac:** [OrbStack](https://orbstack.dev), the .NET 11 SDK and Xcode's command-line tools
  (`xcode-select --install`). The last two are only needed to build the CLI.
- **Linux server:** Docker Engine with the Compose v2 plugin, rootful or rootless. The CLI builds inside a container,
  so you don't need a .NET SDK. The binary needs glibc 2.34 or newer (Ubuntu 22.04+, Debian 12+).

**Install**, once per machine:

```
git clone <this repo> ~/source/repos/pen
~/source/repos/pen/cli/install.sh       # builds the CLI and installs it to ~/.local/bin/pen
```

Make sure `~/.local/bin` is on your `PATH`. The CLI is a native binary, so it works from bash, zsh
and fish alike.

The first `pen` command creates `~/.config/pen/` and tells you so in one line:
- `config`: your settings and secrets (mode 600). Every key is commented out and explained.
- `home/`: the [home seed](#your-agent-config-the-home-seed), with your git identity filled in.

The first session then builds the `pen:latest` image, which takes a few minutes.

**For private repos**, create a read-only GitHub token for each owner (see [GitHub](#github)) and
add it to `~/.config/pen/config`:

```
github.my-user = github_pat_…
github.my-org = github_pat_…
```

**Optional:** API keys and extra domains in the same file, and your own agent instructions and MCP
servers in `~/.config/pen/home/`.

### First time in a project

Before handing a project to an agent, run its restore or build through the sandbox:

```
pen run dotnet restore     # or: pen run npm ci, pen run ./build.sh
```

When it exits, pen lists any domains the proxy blocked. A missing SDK or tool shows up as a build
error. Fix what turns up on the host:
- `pen allow <domain…>` for blocked domains;
- a [`.pen/Dockerfile`](#project-settings-pen) for SDKs or tools the image lacks;
- Docker, if the project needs it, isn't available yet (see [Docker-in-Docker](#docker-in-docker)).

For a brand-new project, run `git init` on the host first. pen quarantines a `.git` that the agent
creates (see [Threat model](#threat-model-and-limitations)).

## Daily use

1. `cd` into a project (the repo root) and run `pen claude`, `pen codex`, or `pen` for a shell.
2. The first time in each project, log in to the agent inside the sandbox (see [Logging in](#logging-in)).
   The login sticks for that project.
3. When the agent is done, review its changes and commits on the host. Run `pen sign` if you sign
   commits, then push.
4. If the agent reports a blocked domain, run `pen allow <domain>` in another terminal. The running
   session gets it immediately, so the agent can just retry.

Several sessions can run in the same project at once, each with its own stack. They share the
project's logins and caches.

## Commands

```
pen [<cmd…>]                    a shell, or any command, in this project's sandbox (e.g. pen claude)
pen run <cmd…>                  the same, for a command named like a subcommand (pen run upgrade)
pen allow <domain…> [--global]  allow domains, applied to running sessions too
pen allow                       list recently blocked domains
pen sign [<base>]               on the host: re-sign your unpushed commits with your key
pen upgrade [--clean]           newer base image and agents; --clean skips the build cache
pen reset                       wipe this project's sandbox state (asks first)
```

`pen run ls | grep x` works: without a terminal, pen runs the command without one too. Ctrl-C and
Ctrl-\ go to the agent, not to pen. If you close the terminal, pen still stops the session and
cleans up.

**Where pen runs.** pen mounts only the current directory, read-write, at the same absolute path, so
paths in build output and tool indexes stay valid. It refuses to run in:
- `/`, your home directory or any directory above it, or anything containing `~/.config/pen`;
- a subdirectory of a repo (run it from the repo root);
- a folder that contains other git repos (run it in each repo instead);
- a linked worktree or a repo with submodules. Neither is supported yet. Worktrees made with
  `git worktree add` *inside* the project, like Claude Code's `.claude/worktrees/`, are fine.

## Network access

The agent's container has no route out. Everything goes through a Squid proxy that allows only
listed domains, with no TLS interception. Anything that ignores the proxy just fails.

The allowlist is three sources, merged at session start:
1. pen's defaults (AI providers, GitHub, NuGet and .NET, npm, PyPI, a few docs sites, container registries);
2. `allow = <domain>` lines in `~/.config/pen/config` (every project);
3. `allow = <domain>` lines in the project's `.pen/config`.

`.example.com` allows `example.com` and all its subdomains.

- **`pen allow <domain…>`** adds `allow =` lines to the project's `.pen/config`, creating it if
  needed. It applies them to the project's running sessions right away. With `--global`, the lines
  go in `~/.config/pen/config` instead and apply to every project's running sessions.
- **At session end**, pen prints what was blocked, e.g.
  `blocked: foo.com → pen allow foo.com (add --global for all projects)`.
- **`pen allow`** with no arguments lists the domains blocked in the project's sessions over the
  last 7 days, newest first, including sessions that are still running or crashed.

Over HTTPS, an agent only sees a bare connection error (`CONNECT tunnel failed`, or a proxy 403),
never an explanation. The sandbox note in the seeded `CLAUDE.md` and `AGENTS.md` tells it what that
means: report the domain and wait.

## GitHub

Inside the sandbox, git and `gh` use a read-only fine-grained token (`GH_TOKEN`), so the agent can
clone, fetch and read but not push. Its commits land in your working copy, and you push from the host.
SSH remotes (`git@github.com:…`) are rewritten to https inside the sandbox only. Your host remotes
and SSH keys are untouched, and no SSH key ever enters the sandbox.

A fine-grained token covers a single owner, so you create **one token per owner** (your account,
and each org). pen picks the token by the owner in the project's `origin` URL.

**Creating a token** (GitHub → Settings → Developer settings → Personal access tokens →
Fine-grained tokens → Generate new token):
1. **Resource owner:** your account or the org. An org may need to approve the token.
2. **Repository access:** "Only select repositories" if you can. Anything the token can read can
   leak through an allowed domain.
3. **Permissions → Repository permissions:** Contents: *Read-only*. Metadata: *Read-only* (always
   included). Optionally Issues and Pull requests: *Read-only*.
4. Add it to `~/.config/pen/config` as `github.<owner> = github_pat_…`. The next session uses it.

Public repos need no token. When a project's owner has none, pen prints a one-line notice with the
line to add. It does this once per project. Use `github_owner = <owner>` in `.pen/config` when
`origin` doesn't name the right owner, for example in a fork or a repo with no remote yet.

## Logging in

Each agent logs in once per project, inside that project's sandbox, and the login lives in that
project's sandbox home. Projects never share logins.
- **Claude:** run `claude` and follow the URL, then paste the code back.
- **Codex:** `codex login --device-auth`.
- **OpenCode / Pi:** their own login flows, or API keys (below).

**API keys** go in `~/.config/pen/config` as `env.<NAME> = <value>`, e.g.
`env.ANTHROPIC_API_KEY = sk-ant-…`. They're passed to the agent container by name only, so the
values never appear on a command line.

**Skip Claude's per-project login:** run `claude setup-token` once (anywhere) and add the result as
`env.CLAUDE_CODE_OAUTH_TOKEN = …`. Claude in every project then starts logged in. The token is
long-lived, so treat it like an API key. Codex has no equivalent: its refresh tokens rotate, so
copies would invalidate each other.

## Your agent config: the home seed

`~/.config/pen/home/` is laid out like `$HOME` and holds config you want in every project's
sandbox. It's for config only, never secrets (those go in the config file) or logins. pen
mounts it read-only. At the start of each session it copies anything newer than the project's copy
into the project's sandbox home:
- a file you add or edit in the seed reaches every project on its next session;
- an agent's edit to its copy lasts until you next edit that file in the seed;
- a seeded file you delete inside a project comes back next session (empty it instead).

On OrbStack, an edit saved *in place* (rather than by the usual write-and-rename) can take a few
seconds to become visible to new containers. A session started right after such an edit gets the
change on its next start.

What pen creates there on first run:

| File | What |
|---|---|
| `.gitconfig` | `user.name`/`user.email` for sandbox commits, from your host identity. Change it (e.g. `Your Name (agent)`) to make sandbox commits stand out. |
| `.claude/CLAUDE.md`, `.codex/AGENTS.md` | the sandbox note (blocked domains, no push, read-only `.git/config`). Add your own instructions after it. |
| `.claude/settings.json` | skip-permissions mode by default, commit and PR attribution off |
| `.claude.json` | only `mcpServers` (CodeGraph to start with) |
| `.codex/config.toml` | approval policy `never`, sandbox `danger-full-access` (the container is the boundary), and MCP servers |

Agents run in their skip-permissions modes because the container is the boundary.

**`.claude.json` is special**, because it also holds Claude's login. It's copied only into a project
that doesn't have one yet. After that, each session merges the seed's `mcpServers` into it: seed
entries win, and servers added inside the sandbox stay.

**Claude memory:** `.claude/projects/<slug>/` in the seed is copied only into the project whose
path matches `<slug>` (the absolute path with every non-alphanumeric character turned into `-`), so
one project never sees another's memory.

**Where each agent looks:**

| Agent | Instructions | MCP servers |
|---|---|---|
| Claude Code | `~/.claude/CLAUDE.md` | `mcpServers` in `~/.claude.json` |
| Codex | `~/.codex/AGENTS.md` | `[mcp_servers.<name>]` in `~/.codex/config.toml` |
| OpenCode | `~/.config/opencode/AGENTS.md` | `mcp` in `~/.config/opencode/opencode.json` |
| Pi | `~/.pi/agent/AGENTS.md` | `~/.pi/agent/mcp.json` (or `pi mcp add`) |

Shell preferences (`.bashrc` aliases, `.inputrc`) can go in the seed too.

**CodeGraph** is installed in the image and seeded as an MCP server for Claude and Codex. Index a
project once from a sandbox session with `codegraph init`. Its `.codegraph/` folder is ignored by
git inside the sandbox. Add `.codegraph/` to your global gitignore on the host too. Only run
CodeGraph inside the sandbox, so only one side ever writes the index.

## Project settings: `.pen/`

Everything here is optional. A project needs no files to use pen.

- **`.pen/config`:** the same `key = value` format as the user config, with project keys only:
  - `allow = <domain>` (repeatable)
  - `github_owner = <owner>`
  - `docker = true`, once [Docker-in-Docker](#docker-in-docker) is available

  Tokens and `env.` lines are refused here, since the file is usually committed.
- **`.pen/Dockerfile`:** a project layer for SDKs and tools the base image lacks, e.g.

  ```dockerfile
  FROM pen:latest
  RUN apt-get update && apt-get install -y --no-install-recommends postgresql-client \
   && rm -rf /var/lib/apt/lists/*
  ```

  pen builds it with `.pen/` as the context on every launch, as `pen-<dir>-<hash>:latest`. The build
  is cached, so it's usually instant, and it picks up base-image rebuilds. Its `RUN` steps build on
  your host's Docker, outside the proxy.

**`.pen/` is read-only inside the sandbox**, because it sets the next session's policy. When a
project has no `.pen/`, pen creates it empty so the agent can't create one. Git ignores empty
directories, so this doesn't show up in `git status`. Changes you make on the host apply to the
next session.

## Commit signing

There's no signing key in the sandbox, so agent commits are unsigned, even in a repo with
`commit.gpgsign=true`. You sign after reviewing:

1. Review the agent's commits on the host.
2. Run `pen sign` in the project. It lists the commits it will rewrite, then re-creates each one
   with your signature. Each commit keeps the same tree, author, date and message, and merges keep
   their exact trees. You become the committer, which is what GitHub checks for "Verified".
3. Push.

- `pen sign [<base>]` signs `<base>..HEAD`. `<base>` defaults to the branch's upstream, then `origin/HEAD`.
- It's not a rebase: commits stay on their original parents even if you've fetched since, so the
  trees you reviewed are the trees you sign.
- It refuses if any of those commits is already on a remote branch, and while a session runs in the
  project. No session can start while it runs.
- It uses your normal git signing setup (`gpg.format`, `user.signingkey`, …) from your global
  config, so SSH signing through 1Password or ssh-agent works. It never runs a signing program named
  in the repo's own config.
- The old commits stay in the reflog.

If your global git config has `commit.gpgsign=true`, each session ends with a reminder like
`3 unsigned commits; review, then run pen sign`.

**Safety net:** turn on "Require signed commits" in GitHub branch protection or rulesets. A
forgotten `pen sign` then fails at push time instead of slipping through.

## Upgrading and resetting

- **Agents and tools:** `pen upgrade` pulls a newer base image, reinstalls the agents (never from
  cache), pulls the proxy image and, run in a project, rebuilds its `.pen/Dockerfile` layer.
  `pen upgrade --clean` rebuilds everything without the cache. Logins and config survive both.
- **pen itself:** `git pull` here, then rerun `cli/install.sh`. The next session rebuilds the image
  if its files changed.
- **Wipe a project's sandbox state** (logins, sandbox home, NuGet cache): `pen reset` in the
  project. It asks first and refuses while a session runs. It lists the running sessions' container
  names, so you can close them or `docker rm -f` one you can't reach.
- **Leftover volumes:** moving or renaming a project gives it fresh volumes, and deleting a project
  leaves its volumes behind. To list every project's volumes:

  ```
  docker volume ls --format '{{.Name}}' | grep -E '^pen-.*_(home|nuget|dind)$'
  ```

  Remove the ones you don't need with `docker volume rm <name>…`.

Nothing else needs tearing down: each session's stack exists only while it runs.

## Differences from running on the host

- **Clipboard images:** pasting an image into an agent doesn't work, since the container can't see
  the Mac's clipboard. Save the image into the project and give the agent its path.
- **New repos:** run `git init` on the host. A `.git` the agent creates gets quarantined at session end.
- **Apps the agent starts** (web servers, APIs) can't be reached from the host, because the sandbox
  publishes no ports. Run them on the host, which sees the same files.
- **Git limits inside the sandbox:** `.git/config` and `.git/hooks` are read-only. Commit, branch,
  checkout, merge, rebase, fetch, pull, stash and `git worktree add` all work. These fail by design:
  - `git config` (local scope) and `git remote add`;
  - `git branch -d`/`-m` (the ref changes, then an error);
  - setting upstream tracking;
  - `gh pr checkout` and `git submodule add`;
  - hook installers such as husky.

  Run those on the host. `git config --global` works and stays in the project's sandbox home.
- **.NET:** the sandbox uses your host's NuGet path (backed by its own per-project cache), so
  `obj/` works from both sides. Don't build on both sides at the same time.

## Moving off host Claude

Nothing in pen needs an agent installed on the host. Removing it also means the project's
`.claude/settings.json` hooks and `.mcp.json`, which the agent can edit, only ever run inside the
sandbox. Before uninstalling, copy what you want to keep into the seed (`~/.config/pen/home/`):
- `~/.claude/CLAUDE.md`, `settings.json`, `commands/`, `agents/` and `skills/`, to the same paths in
  the seed. Check them for host paths like `/Users/<you>/…` in hooks or the status line: `$HOME` in
  the sandbox is `/home/agent`.
- From `~/.claude.json`, only the `mcpServers` block, into the seed's `.claude.json`. The rest is
  host login and app state.
- Plugins: reinstall them inside the sandbox rather than copying `~/.claude/plugins/`, which records
  host paths.
- Per-project memory: copy `~/.claude/projects/<slug>/memory/` to the same path in the seed. The
  sandbox mounts each project at its host path, so the slug matches, and each project gets only its own.

For Codex, copy `~/.codex/config.toml` (minus anything host-specific) and `AGENTS.md`, never `auth.json`.

## Docker-in-Docker

*Not in this version yet.* It will be turned on with `pen --docker …` or `docker = true` in
`.pen/config`. It adds a privileged `docker:dind` sidecar on the sandbox network, with the project
mounted at the same path and image pulls going through the proxy. Things to know once it lands:
- Published ports show up at `dind:<port>`, not `localhost`. Testcontainers handles this through `DOCKER_HOST`.
- Containers inside dind get the same allowlist as the agent, so their registries and package feeds
  must be on it.
- Only one `--docker` session per project at a time.
- While it's on, isolation is weaker: on OrbStack, a privileged escape reaches the VM, which can see
  the Mac's `/Users`.

## Threat model and limitations

The thing pen contains is an *unattended agent*. Through bugs, misreading the task, or prompt
injection from content it reads, it might delete things, leak credentials, push somewhere, or reach
outside the project. The code being worked on is trusted. pen aims for a strong default boundary
that's hard to breach by accident. It doesn't defend against a deliberate kernel-level escape.

**What pen does about the repo.** Host git runs whatever `.git/config` and `.git/hooks` say: hooks,
`core.fsmonitor`, `core.sshCommand`. It runs them during a session (IDE polling) and right after it
(your shell prompt). So inside the sandbox:
- `.git/config`, `.git/hooks`, each worktree's `commondir` and `.pen/` are mounted read-only;
- `.git` itself can't be renamed away.

At session end, before pen returns, a file-only check renames anything that would redirect host
git to `….pen-quarantine` and prints how to restore it after you've looked. That covers a new
`.git/commondir`, a `.git/modules/`, a nested repo, or a `.git` made in a project that had none.
Every git call pen makes on the host overrides hooks, fsmonitor and the pager.

**DNS.** External names don't resolve inside the sandbox (checked on OrbStack: `getent hosts
example.com` fails), so DNS isn't a side channel out. Only the proxy resolves names.

**Rootless Docker.** Under rootless Docker your user is root *inside* containers, so the agent
runs as container UID 0. That's still just your host user. It has no capabilities and can never
regain any (the `noroot` securebits are locked), and its root filesystem is read-only, so it can't
change the image's tools. `id` shows `root` in those sessions. pen detects rootless Docker by itself.

**Limitations:**
- Allowed domains, including the AI providers and github.com, are still possible exfiltration channels.
- Docker-in-Docker, when on, weakens isolation (privileged container).
- Files the agent edits still get built or run on the host later: MSBuild `.props`/`.targets`,
  `package.json` scripts, `.vscode/tasks.json`, `.envrc`. These show up in the diff, so review it
  before building on the host.
- The end-of-session check only runs once the session ends, so host git that runs *during* a session
  (mainly an IDE) can still be affected by a planted `.git/commondir` or a new nested repo. The host
  settings below narrow this.
- A repo whose `core.hooksPath` already points into the working tree (e.g. husky's `.husky/`) has
  hooks the agent can edit. Host git runs them on your next commit or checkout, before you've
  reviewed the diff. pen protects `.git/hooks` only. For such repos, review changes under that path
  before running git on the host, or point `core.hooksPath` outside the project.
- The project mount is read-write, so the agent can delete files, including uncommitted changes and
  the contents of `.git` (unpushed commits, stashes). Only what you've pushed is safe. Back up your
  machine (e.g. Time Machine); pen isn't a backup.

## Recommended host settings

These close most of the gap for nested repos an agent creates during a session:

```
git config --global safe.bareRepository explicit
```

In VS Code, set `git.autoRepositoryDetection` to `false` (or `openEditors`) so it doesn't scan
subfolders for repos. Also add `.codegraph/` to your global gitignore.
