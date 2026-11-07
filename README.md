# pen

`pen` is an opinionated sandbox for AI coding agents (Claude Code, Codex, OpenCode, Pi). It runs
them in a container instead of directly on your machine. Inside, the agent:

- sees only the project directory you run `pen` in;
- reaches only an allowlist of domains;
- can read from GitHub but never push.

Logins and config persist across sessions and image rebuilds. Projects need no pen-specific files.

```sh
cd ~/source/repos/myproject
pen claude
# or: pen codex, pen opencode, pen pi, pen (a shell), pen run <cmd…>
```

The base image is .NET-first, with Node and Python built in. Anything else goes in a
[`.pen/Dockerfile`](#project-settings).

## Why pen

Agents increasingly ship their own sandboxes, and a devcontainer with a firewall gets you part of
the way. pen is for letting agents run unattended with permission prompts off, and it's built
around what can still go wrong once the agent is in a container:

- **Your git, on the host.** An agent that can write to the project can also write `.git/config`
  or `.git/hooks`, which run the next time *you* use git. pen mounts both read-only, refuses repo
  layouts it can't protect (nested repos), and checks `.git` before running any git on the host.
- **GitHub without push.** The agent gets a read-only token per owner. It can clone and fetch but
  never push: you review, then push.
- **Signed commits without a key in the sandbox.** `pen sign` re-creates the agent's unpushed
  commits with your own signature.
- **An allowlist you can adjust mid-session.** All traffic goes through a proxy. `pen allow`
  updates running sessions, and pen lists blocked domains on exit.
- **One setup for several agents.** Claude Code, Codex, OpenCode and Pi share the same image and
  network rules. Each project keeps its own logins, caches and home directory.

It's opinionated: run from a repo root, one sandbox per project, git config and remotes managed on
the host, commits signed on the host. If that doesn't fit how you work, pen will get in your way.

**pen isn't a good fit if:**

- you approve agent actions one by one anyway. An agent's built-in sandbox is lighter.
- you need protection from a deliberate container escape. Use a VM.
- you're on Windows. pen supports macOS and Linux.

## Install

> **Note:** the Homebrew tap, release binaries and ghcr.io image aren't published yet. Until they
> are, build pen from source as described in
> [Standing in for a release](CONTRIBUTING.md#standing-in-for-a-release).

Requires Docker with Compose v2:

- **macOS:** any Docker engine, e.g. [OrbStack](https://orbstack.dev) (what pen is tested on),
  Docker Desktop or Colima. pen bind-mounts your projects and `~/.config/pen` at their real paths,
  so the engine's VM must share your home directory read-write. OrbStack and Docker Desktop do
  this by default. Projects outside your home directory need their own share.
- **Linux:** Docker Engine, rootful or rootless, on glibc 2.34+.

```sh
brew install mmonteleone/tap/pen
```

Homebrew on Linux works too. Without Homebrew, download the binary for your platform from
[GitHub Releases](https://github.com/mmonteleone/pen/releases) and put it on your `PATH` (e.g.
`~/.local/bin/pen`).

The first run creates `~/.config/pen/` (settings and agent config) and pulls the base image from
`ghcr.io/mmonteleone/pen`. For private repos, add a GitHub token (see [GitHub](#github)).

## Usage

1. From a repo root, run `pen claude` (or another agent, or `pen` for a shell).
2. The first time in each project, log in to the agent inside the sandbox.
3. When the agent is done, review its commits on the host, run `pen sign` if you sign commits,
   then push.

If the agent hits a blocked domain, run `pen allow <domain>` in another terminal. The running
session picks it up immediately. Several sessions can run in the same project at once.

**New project?** Run its restore or build through pen first, e.g. `pen run npm ci`. On exit, pen
lists any blocked domains; missing tools show up as build errors. Fix them with `pen allow` or a
[`.pen/Dockerfile`](#project-settings).

pen must run from a repo root. It refuses your home directory, subdirectories of a repo and folders
containing multiple repos.

### Commands

```
pen claude | codex | opencode | pi
                                start an agent in this project's sandbox
pen [--docker] [--port <n>]… [<cmd…>]
                                shell or command in the sandbox (--docker, --port: see below)
pen [--docker] [--port <n>]… run <cmd…>
                                same, for a command that clashes with a subcommand name
pen allow <domain…> [--global]  allow domains for this project (or all projects)
pen allow                       list recently blocked domains
pen allow --defaults            list the domains pen allows out of the box
pen ps                          list running sessions, in every project, with their forwarded ports
pen sign [<base>]               re-sign your unpushed commits with your own key
pen upgrade [--clean]           update the base image and agents
pen reset                       wipe this project's sandbox state (logins, caches)
pen prune                       remove what moved or deleted projects left behind
```

Options go before the agent: `pen --docker --port 5173 claude`.

## Configuration

Settings live in `~/.config/pen/config.toml` (created on first run) and, optionally, a project's
`.pen/config.toml`. Both are [TOML](https://toml.io): strings are quoted, and unknown keys are
errors so a typo doesn't go unnoticed.

```toml
# ~/.config/pen/config.toml (keep it private: it holds secrets)
allow = [
  "example.com",
]

[github]
my-org = "github_pat_…"

[env]
CLAUDE_CODE_OAUTH_TOKEN = "…"
```

```toml
# .pen/config.toml (usually committed, so no secrets)
allow = [
  ".internal.example.org",
]
github_owner = "my-org"
docker = true
ports = [5173]
```

Each key belongs to one file, except `allow`: a project's list adds to yours, which adds to pen's
defaults. Changes apply to the next session. `pen allow` also applies to running ones.

### Network

The default allowlist covers AI providers, GitHub, common package registries, developer docs and
container registries (`pen allow --defaults` shows it). `.example.com` matches the domain and all
its subdomains. `pen allow <domain>` adds to the project's `allow` (`--global`: to yours), leaving
the rest of the file as you wrote it.

The defaults are sites an agent without credentials can only read from. A domain that accepts
anonymous writes (a wiki, a pastebin) could be used to send your code out, so think twice before
allowing one.

### GitHub

git and `gh` inside the sandbox use a read-only token, so the agent can clone and fetch but not
push. Public repos need no token. For private repos, create a fine-grained token per owner
(GitHub → Settings → Developer settings → Fine-grained tokens):

- **Resource owner:** your account or the org.
- **Repository access:** only the repos you need.
- **Permissions:** Contents *Read-only* (optionally Issues and Pull requests *Read-only*).

Add it under `[github]` in `~/.config/pen/config.toml` as `<owner> = "…"`. pen picks the token
from the owner in `origin`; set `github_owner = "<owner>"` in `.pen/config.toml` to override.
Only that owner's token is in the sandbox.

### Logins and API keys

Each agent logs in once per project, inside the sandbox (`claude`, `codex login --device-auth`,
etc.). Logins aren't shared between projects.

API keys go under `[env]` in `~/.config/pen/config.toml` as `<NAME> = "<value>"`. To skip
Claude's per-project login, run `claude setup-token` and add the result as
`CLAUDE_CODE_OAUTH_TOKEN = "…"`.

### Agent config (the home seed)

`~/.config/pen/home/` is laid out like `$HOME` and is copied into every project's sandbox home at
session start. Put agent instructions, settings, MCP servers and shell config there (never
secrets). Edits in the seed overwrite the sandbox copy on the next session.

| Agent | Instructions | MCP servers |
|---|---|---|
| Claude Code | `.claude/CLAUDE.md` | `mcpServers` in `.claude.json` |
| Codex | `.codex/AGENTS.md` | `[mcp_servers.<name>]` in `.codex/config.toml` |
| OpenCode | `.config/opencode/AGENTS.md` | `mcp` in `.config/opencode/opencode.json` |
| Pi | `.pi/agent/AGENTS.md` | `.pi/agent/mcp.json` |

Agents run in skip-permissions mode by default; the container is the boundary. Edit
`.gitconfig` in the seed to change the identity used for sandbox commits.

### Project settings

Optional, in the project's `.pen/` directory (read-only inside the sandbox):

- **`.pen/config.toml`:** the keys shown above. Secrets (`[github]`, `[env]`) aren't allowed here.
- **`.pen/Dockerfile`:** extra SDKs or tools on top of the base image (which pen keeps tagged
  locally as `pen:latest`):

  ```dockerfile
  FROM pen:latest
  RUN apt-get update && apt-get install -y --no-install-recommends postgresql-client \
   && rm -rf /var/lib/apt/lists/*
  ```

## Git in the sandbox

Everyday git works inside: commit, branch, merge, rebase, fetch, worktrees. `.git/config` and
`.git/hooks` are read-only, so these must run on the host:

- `git init` for a new project;
- `git config`, `git remote add`, setting upstreams, deleting branches, hook installers (husky);
- `git submodule add`, `update --init` and `sync`;
- `git worktree prune` and `remove` for worktrees outside the project;
- pushing.

### Worktrees

A linked worktree (`git worktree add ../feat -b feat`) is a repo root too, so you can run an agent
per branch in parallel. All worktrees of a repo are one project: they share its logins, caches,
`pen reset` and the one-`--docker`-session limit. Each worktree has its own `.pen/`, so `pen allow`
in one applies only to that worktree. In the sandbox, a worktree sees the repo's shared git data
(objects, branches) but not the main checkout's files. Worktrees of a bare repo aren't supported.

### Submodules

Set submodules up on the host (`git submodule update --init --recursive`). In the sandbox, the
agent can commit inside a submodule and commit the bump in the parent repo; each submodule's config
and hooks are read-only like the parent's. Only the `origin` owner's GitHub token is available, so
submodules from another owner can be fetched only if they're public.

A submodule whose checkout has its own `.git` directory (made by older git, or by cloning in place
before `git submodule add`) is refused: `git submodule absorbgitdirs` on the host moves it into the
parent's `.git/modules/`.

### Commit signing

There's no signing key in the sandbox, so agent commits are unsigned. After reviewing them, run
`pen sign` on the host: it re-creates `<base>..HEAD` (default: the upstream branch) with your
signature, keeping trees, authors and messages, using your normal git signing setup. It refuses
to touch commits that are already pushed.

Turning on "Require signed commits" on GitHub catches a forgotten `pen sign` at push time.

## Dev servers

To open a server running in the sandbox (`npm run dev`, `dotnet watch`) from a browser on the host,
forward its port: `pen --port 5173 claude`, or `ports = [5173]` in `.pen/config.toml` for every
session. `localhost:5173` on the host then reaches port 5173 in the sandbox; the port number is the
same on both sides, and only the host's `127.0.0.1` listens.

- The server must listen on `0.0.0.0`, not only `localhost`: e.g. `vite --host`,
  `ASPNETCORE_URLS=http://0.0.0.0:5000`.
- A port that's already in use on the host isn't remapped: `--port` fails, and a port from
  `.pen/config.toml` is skipped with a warning. `pen ps` shows which session has it.
- Forwarding goes from the host into the sandbox only; it gives the sandbox no way out.

## Docker-in-Docker

For projects that need Docker (Testcontainers, compose, image builds), run `pen --docker claude`
or set `docker = true` in `.pen/config.toml`. The session gets its own Docker daemon, reachable at
`DOCKER_HOST=tcp://dind:2375`, with traffic going through the same allowlist.

- One `--docker` session per project at a time.
- Published ports are at `dind:<port>`, not `localhost`, and only the sandbox reaches them.
- Containers get proxy env vars; add service names (e.g. `api`) to `NO_PROXY` in compose files.
- On rootful Linux, files containers write to the project are owned by root; pass `--user`.
- The daemon runs privileged, so isolation is weaker while it's on.

## Upgrading and resetting

- `brew upgrade pen` updates pen and the base image that goes with it.
- `pen upgrade` reinstalls the agents at their latest versions and pulls newer proxy and Docker
  images, without waiting for a pen release (`--clean` skips the build cache). Run it in a project
  with a `.pen/Dockerfile` to rebuild that layer too. Logins and config are kept.
- `pen reset` wipes a project's sandbox state.
- `pen prune` finds the volumes, project layers and state of projects you've moved or deleted, and
  removes them after asking. Ones it can't place are listed with the commands to remove them by
  hand. A directory on a volume that isn't mounted looks deleted, so mount it first.

## Differences from running on the host

- Pasting images into the agent doesn't work; save the image to the project and pass its path.
- Servers the agent starts are reachable from the host only on [forwarded ports](#dev-servers).
- Some git commands must run on the host; see [Git in the sandbox](#git-in-the-sandbox).

## Limitations

pen guards against an unattended agent doing damage by accident or via prompt injection. It is not
a defense against a deliberate container escape.

- Allowed domains (including AI providers and GitHub) can still be used to exfiltrate data.
- The project is mounted read-write: the agent can delete files, uncommitted work and unpushed
  commits. Keep backups.
- Files the agent edits may later run on the host (build scripts, `package.json` scripts,
  `.envrc`, hooks under a `core.hooksPath` in the repo). Review the diff before building or
  committing on the host.
- `.gitmodules` is a project file the agent can edit, and host `git submodule` commands clone the
  URLs and paths it names. Review `.gitmodules` diffs before running submodule commands on the host.
- Docker-in-Docker weakens isolation.

pen moves nested repos aside at session end, but during a session the agent can create one, with
its own config and hooks, that host tools would run git in. To close that gap, on the host:

```
git config --global safe.bareRepository explicit
```

and in VS Code, set `git.autoRepositoryDetection` to `false` or `openEditors` so it doesn't scan
subfolders for repos.

## Developing pen

See [CONTRIBUTING.md](CONTRIBUTING.md) for building, testing and releasing pen itself.
