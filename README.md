# pen

`pen` runs AI coding agents (Claude Code, Codex, OpenCode, Pi) in a container
instead of directly on your machine. Inside, the agent:

- sees only the project directory you run `pen` in;
- reaches only an allowlist of domains;
- can read from GitHub but never push.

Logins and config persist across sessions and image rebuilds. Projects need no pen-specific files.

```sh
cd ~/source/repos/myproject
pen claude
# or: pen codex, pen opencode, pen pi, pen (a shell), pen run <cmd…>
```

## Install

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
`ghcr.io/mmonteleone/pen`.

> **Note:** the Homebrew tap, release binaries and ghcr.io image aren't published yet. Until they
> are, build both locally as described in
> [Standing in for a release](CONTRIBUTING.md#standing-in-for-a-release).

For private repos, add a read-only GitHub token for each owner to `~/.config/pen/config.toml`
(see [GitHub](#github)):

```toml
[github]
my-user = "github_pat_…"
my-org = "github_pat_…"
```

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

### Commands

```
pen [--docker] [<cmd…>]         shell or command in this project's sandbox (--docker: see below)
pen [--docker] run <cmd…>       same, for a command that clashes with a subcommand name
pen allow <domain…> [--global]  allow domains for this project (or all projects)
pen allow                       list recently blocked domains
pen allow --defaults            list the domains pen allows out of the box
pen sign [<base>]               re-sign your unpushed commits with your own key
pen upgrade [--clean]           update the base image and agents
pen reset                       wipe this project's sandbox state (logins, caches)
```

pen must run from a repo root. It refuses your home directory, subdirectories of a repo, folders
containing multiple repos, linked worktrees and repos with submodules.

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
```

Each key belongs to one file, except `allow`: a project's list adds to yours, which adds to pen's
defaults. Changes apply to the next session. `pen allow` also applies to running ones.

### Network

All traffic goes through a proxy that allows only listed domains. The allowlist combines pen's
defaults (AI providers, GitHub, common package registries, developer docs, container registries;
see them with `pen allow --defaults`) with `allow` in `~/.config/pen/config.toml` and in the
project's `.pen/config.toml`. `.example.com` matches the domain and all its subdomains.
`pen allow <domain>` adds it to the project's list (`--global`: to yours), leaving the rest of the
file as you wrote it.

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

Add it under `[github]` as `<owner> = "…"`. pen picks the token from the owner in `origin`; set
`github_owner = "<owner>"` in `.pen/config.toml` to override.

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

- **`.pen/config.toml`:** `allow = [...]`, `github_owner = "<owner>"`, `docker = true`. Secrets
  (`[github]`, `[env]`) aren't allowed here.
- **`.pen/Dockerfile`:** extra SDKs or tools on top of the base image (which pen keeps tagged
  locally as `pen:latest`):

  ```dockerfile
  FROM pen:latest
  RUN apt-get update && apt-get install -y --no-install-recommends postgresql-client \
   && rm -rf /var/lib/apt/lists/*
  ```

## Commit signing

There's no signing key in the sandbox, so agent commits are unsigned. After reviewing them, run
`pen sign` on the host: it re-creates `<base>..HEAD` (default: the upstream branch) with your
signature, keeping trees, authors and messages, using your normal git signing setup. It refuses
to touch commits that are already pushed.

Turning on "Require signed commits" on GitHub catches a forgotten `pen sign` at push time.

## Docker-in-Docker

For projects that need Docker (Testcontainers, compose, image builds), run `pen --docker claude`
or set `docker = true` in `.pen/config.toml`. The session gets its own Docker daemon, reachable at
`DOCKER_HOST=tcp://dind:2375`, with traffic going through the same allowlist.

- One `--docker` session per project at a time.
- Published ports are at `dind:<port>`, not `localhost`.
- Containers get proxy env vars; add service names (e.g. `api`) to `NO_PROXY` in compose files.
- On rootful Linux, files containers write to the project are owned by root; pass `--user`.
- The daemon runs privileged, so isolation is weaker while it's on.

## Upgrading and resetting

- `brew upgrade pen` updates pen and the base image that goes with it.
- `pen upgrade` reinstalls the agents at their latest versions and pulls newer proxy and Docker
  images, without waiting for a pen release (`--clean` skips the build cache). Run it in a project
  with a `.pen/Dockerfile` to rebuild that layer too. Logins and config are kept.
- `pen reset` wipes a project's sandbox state. Moved or deleted projects leave volumes behind; list
  them with `docker volume ls -q | grep '^pen-'` and remove with `docker volume rm`.

## Differences from running on the host

- Pasting images into the agent doesn't work; save the image to the project and pass its path.
- Servers the agent starts aren't reachable from the host.
- `.git/config` and `.git/hooks` are read-only, so `git config`, `git remote add`, setting
  upstreams, deleting branches and hook installers (husky) must be run on the host. Everyday git
  (commit, branch, merge, rebase, fetch, worktrees) works.
- Run `git init` on the host for new projects.

## Limitations

pen guards against an unattended agent doing damage by accident or via prompt injection. It is not
a defense against a deliberate container escape.

- Allowed domains (including AI providers and GitHub) can still be used to exfiltrate data.
- The project is mounted read-write: the agent can delete files, uncommitted work and unpushed
  commits. Keep backups.
- Files the agent edits may later run on the host (build scripts, `package.json` scripts,
  `.envrc`, hooks under a `core.hooksPath` in the repo). Review the diff before building or
  committing on the host.
- Docker-in-Docker weakens isolation.

Recommended on the host:

```
git config --global safe.bareRepository explicit
```

and in VS Code, set `git.autoRepositoryDetection` to `false` or `openEditors`.

## Developing pen

See [CONTRIBUTING.md](CONTRIBUTING.md) for building, testing and releasing pen itself.
