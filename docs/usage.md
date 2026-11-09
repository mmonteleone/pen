# Using pen

Detailed settings and workflows. For installation and a first session, see the
[README](../README.md).

- [Commands](#commands)
- [Configuration](#configuration): [network](#network), [GitHub](#github),
  [logins and API keys](#logins-and-api-keys), [agent config](#agent-config-the-home-seed),
  [project settings](#project-settings)
- [Git](#git-in-the-sandbox): [worktrees](#worktrees), [submodules](#submodules),
  [commit signing](#commit-signing)
- [Dev servers](#dev-servers) and [Docker-in-Docker](#docker-in-docker)
- [Upgrading and resetting](#upgrading-and-resetting)
- [Differences from running on the host](#differences-from-running-on-the-host) and
  [limitations](#limitations)

## Commands

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
pen trust [--yes | --list]      review and approve what this project's .pen/ asks for
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
defaults. Changes apply to the next session, once [approved](#project-settings). `pen allow` also
applies to running ones.

### Network

The default allowlist covers AI providers, GitHub, common package registries, developer docs and
container registries (`pen allow --defaults` shows it). `.example.com` matches the domain and all
its subdomains. `pen allow <domain>` adds to the project's `allow` (`--global`: to yours), leaving
the rest of the file as you wrote it.
Repeating the command reapplies the current configuration to running sessions, even if the
domains are already saved, so a failed live update can be retried with the same command.

The defaults are sites an agent without credentials can only read from. Sites that accept
anonymous writes, such as wikis or pastebins, could let an agent send your code out.

### GitHub

Read-only GitHub access depends on supplying read-only credentials. pen passes your token to
git and `gh`. It does not validate or reduce its permissions. A write-capable token or a login
you add inside the sandbox can allow pushes and other writes to GitHub. Public repos need no
token. For private repos, create a fine-grained token per owner
(GitHub → Settings → Developer settings → Fine-grained tokens):

- **Resource owner:** your account or the org.
- **Repository access:** only the repos you need.
- **Permissions:** Contents *Read-only* (optionally Issues and Pull requests *Read-only*).

Add it under `[github]` in `~/.config/pen/config.toml` as `<owner> = "…"`. pen picks the token
from the owner in `origin`. Set `github_owner = "<owner>"` in `.pen/config.toml` to override.
Only that owner's token is in the sandbox.

### Logins and API keys

Log in to each agent inside the sandbox (`claude`, `codex login --device-auth`, etc.), once per
project. Logins aren't shared between projects.

API keys go under `[env]` in `~/.config/pen/config.toml` as `<NAME> = "<value>"`. To skip
Claude's per-project login, run `claude setup-token` and add the result as
`CLAUDE_CODE_OAUTH_TOKEN = "…"`.

### Agent config (the home seed)

`~/.config/pen/home/` is laid out like `$HOME` and is copied into every project's sandbox home at
session start. Put agent instructions, settings, MCP servers and shell config there (never
secrets).

Seed files overwrite matching files on every start, regardless of modification times. Files
absent from the seed are left alone: deleting a seed file stops managing it but leaves its last
sandbox copy. Delete that copy inside the sandbox too if you want it removed.

Instructions, settings, shell config and `.gitconfig` belong in the seed. Logins, credentials,
history and session state belong in each project's home volume. Credential files
(`.claude/.credentials.json`, `.codex/auth.json`, `.local/share/opencode/auth.json` and
`.pi/agent/auth.json`) and `.docker/` are excluded from seed copying. Docker sessions set the
proxy in the project's Docker client config separately, preserving its other fields.

Claude's `.claude.json` mixes MCP settings with login state. Only its `mcpServers` map comes
from the seed, replacing that map completely while keeping all other sandbox fields. Remove a
server from that map in the seed to remove it from subsequent sessions. To keep sandbox-added
servers instead, remove `.claude.json` from the seed. Only the current project's
`.claude/projects/<slug>/` seed directory is copied; other projects' memory is excluded.

The paths below are relative to `~/.config/pen/home/` in the seed (and to `$HOME`
inside the sandbox). Agent changes to seed-managed files can be overwritten at the next start.
Project-specific agent files can instead live in the project, according to each agent’s conventions.

| Agent | Instructions | MCP servers |
|---|---|---|
| Claude Code | `.claude/CLAUDE.md` | `mcpServers` in `.claude.json` |
| Codex | `.codex/AGENTS.md` | `[mcp_servers.<name>]` in `.codex/config.toml` |
| OpenCode | `.config/opencode/AGENTS.md` | `mcp` in `.config/opencode/opencode.json` |
| Pi | `.pi/agent/AGENTS.md` | `.pi/agent/mcp.json` |

Agents run in skip-permissions mode by default. The container is the boundary. Edit
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

An agent can write a `.pen/` for a later session (in a worktree inside the project, say), so a
session asks before using what's new there, and doesn't start if you decline:

- `allow` domains not already allowed;
- `docker = true`;
- a `github_owner` that differs from `origin`'s owner;
- a `.pen/Dockerfile`, by a hash of everything in `.pen/` except `config.toml`, so any change there
  asks again. The prompt shows it, and the path of a private copy of the rest, from the same moment
  as the hash. Builds read such a copy, without `config.toml`, so `COPY config.toml` doesn't work. A `.pen/` with a special file, an oversized tree, a name that isn't valid UTF-8 or a
  symlink leading anywhere but straight to a file or directory in `.pen/` can't be approved at all.

`ports` isn't asked about: it only publishes to the host's loopback. Without a terminal a session
refuses instead. `pen trust` reviews and approves the same list, `pen trust --yes` without asking.
A long list of domains is cut short; `pen trust --list` shows everything.
Domains you add with `pen allow <domain>` count as approved, and unapproved ones never reach a
running session's allowlist. Approvals are per directory, kept in pen's state directory, so a new
worktree starts with none.

## Git in the sandbox

Everyday git works inside: commit, branch, merge, rebase, fetch, worktrees. `.git/config` and
`.git/hooks` are read-only, so these must run on the host:

- `git init` for a new project;
- `git config`, `git remote add`, setting upstreams, deleting branches, hook installers (husky);
- `git submodule add`, `update --init` and `sync`;
- `git worktree prune` and `remove` for worktrees outside the project.

Push reviewed commits from the host using your own credentials. The sandbox's read-only GitHub
credentials cannot push. pen itself does not block pushes made with write-capable credentials.

### Worktrees

A linked worktree (`git worktree add ../feat -b feat`) is a repo root too, so you can run an agent
per branch in parallel. All worktrees of a repo are one project: they share its logins, caches,
`pen reset` and the one-`--docker`-session limit. Each worktree has its own `.pen/`, so `pen allow`
in one applies only to that worktree. In the sandbox, a worktree sees the repo's shared git data
(objects, branches) but not the main checkout's files. Worktrees of a bare repo aren't supported.

### Submodules

Set submodules up on the host (`git submodule update --init --recursive`). In the sandbox, the
agent can commit inside a submodule and commit the bump in the parent repo. Each submodule's config
and hooks are read-only like the parent's. Only the `origin` owner's GitHub token is available, so
submodules from another owner can be fetched only if they're public.

A submodule whose checkout has its own `.git` directory (made by older git, or by cloning in place
before `git submodule add`) is refused: `git submodule absorbgitdirs` on the host moves it into the
parent's `.git/modules/`.

### Commit signing

There's no signing key in the sandbox, so agent commits are unsigned. After reviewing them, run
`pen sign` on the host: it re-creates `<base>..HEAD` with your signature, keeping trees, authors
and messages, using your normal git signing setup. The base defaults to the upstream branch,
falling back to `origin/HEAD`. It refuses to touch commits that are already pushed.

Commits whose signature verifies under your global git config are kept, unless a commit below them
changes. A signature only verifies if your global config can check it: for SSH signing, set
`gpg.ssh.allowedSignersFile` to an absolute path, or each `pen sign` re-signs everything and changes
the hashes again (pen says so when this happens). Settings in the repo's own config don't count.

Turning on "Require signed commits" on GitHub catches a forgotten `pen sign` at push time.

## Dev servers

To open a server running in the sandbox (`npm run dev`, `dotnet watch`) from a browser on the host,
forward its port: `pen --port 5173 claude`, or `ports = [5173]` in `.pen/config.toml` for every
session. `localhost:5173` on the host then reaches port 5173 in the sandbox. The port number is the
same on both sides, and only the host's `127.0.0.1` listens.

- The server must listen on `0.0.0.0`, not only `localhost`: e.g. `vite --host`,
  `ASPNETCORE_URLS=http://0.0.0.0:5000`.
- A port that's already in use on the host isn't remapped: `--port` fails, and a port from
  `.pen/config.toml` is skipped with a warning. `pen ps` shows which session has it.
- Forwarding goes from the host into the sandbox only. It gives the sandbox no way out.

## Docker-in-Docker

For projects that need Docker (Testcontainers, compose, image builds), run `pen --docker claude`
or set `docker = true` in `.pen/config.toml`. The session gets its own Docker daemon, reachable at
`DOCKER_HOST=tcp://dind:2375`, with traffic going through the same allowlist.

- One `--docker` session per project at a time.
- Published ports are at `dind:<port>`, not `localhost`, and only the sandbox reaches them.
- Containers get proxy env vars. Add service names (e.g. `api`) to `NO_PROXY` in compose files.
- On rootful Linux, files containers write to the project are owned by root. Pass `--user`.
- The daemon runs privileged, so isolation is weaker while it's on.

## Upgrading and resetting

- To update the CLI, run `git pull` and `cli/install.sh` from your pen source checkout. If the
  embedded image files changed, the next session rebuilds the base image.
- `pen upgrade` reinstalls the agents at their latest versions and pulls newer proxy and Docker
  images, without waiting for a pen release (`--clean` skips the build cache). Run it in a project
  with an approved `.pen/Dockerfile` to rebuild that layer too. Logins and config are kept.
- `pen reset` wipes a project's sandbox state.
- `pen prune` finds the volumes, project layers and state of projects you've moved or deleted, and
  removes them after asking. Ones it can't place are listed with the commands to remove them by
  hand. A directory on a volume that isn't mounted looks deleted, so mount it first.

Once releases are published, Homebrew installs will use `brew upgrade pen` for CLI updates;
standalone installs will use a replacement release binary. See the
[planned install flow](../README.md#once-published-planned).

## Differences from running on the host

- Pasting images into the agent doesn't work. Save the image to the project and pass its path.
- Servers the agent starts are reachable from the host only on [forwarded ports](#dev-servers).
- Some git commands must run on the host. See [Git in the sandbox](#git-in-the-sandbox).

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

A directory or symlink whose name isn't valid UTF-8 (possible on Linux) can't be checked for a repo inside
it, so pen refuses to start until it's renamed or removed, and at session end reports that git wasn't
checked instead of moving it. Plain files with such names, such as legacy Latin-1 ones, are fine.

At session start pen saves copies of the git files it mounts read-only one by one (`config`,
`config.worktree`, included config files, `commondir` and a worktree's `.git` file, including those of
submodule repos), and refuses to start if one isn't a regular file of at most 1 MB. At session end,
any that changed is put back, with the changed version kept as `….pen-quarantine` and a warning that
says how to restore it. That includes changes you made on the host during the session, such as
`git config`, so make those between sessions.
If a file can't be put back, pen says so, prints the command that fixes it, keeps its copies and
refuses new sessions until that works.

Also at session end, pen quarantines any `config.worktree` in the git dir, its worktree entries and
submodule repos that wasn't there when the session started, or that first appeared while another
session of the project was running. git reads settings from that file, some of which run programs. A worktree
made with `git worktree add` while `extensions.worktreeConfig` is on, in the sandbox or during a session on
the host, loses the `config.worktree` git copies into it, and the warning says how to restore it.
`pen sign` has no record of what was there at a session's start, so it leaves these files alone and doesn't
mention them. A session moves what it made when it ends, so one can only be there while a session is still running in
another worktree. pen sign's own git commands turn off `core.fsmonitor` and hooks; your own git in such a worktree isn't protected until that session ends.

pen moves nested repos aside at session end, but during a session the agent can create one, with
its own config and hooks, that host tools would run git in. To close that gap, on the host:

```
git config --global safe.bareRepository explicit
```

and in VS Code, set `git.autoRepositoryDetection` to `false` or `openEditors` so it doesn't scan
subfolders for repos.

