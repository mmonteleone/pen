# Using Pen

Commands, settings and workflows. Start with the [README](../README.md) for installation and a first session, and [limitations](limitations.md) for host precautions.

- [Commands](#commands)
- [Configuration](#configuration): [network](#network), [GitHub](#github), [logins and API keys](#logins-and-api-keys), [agent config](#agent-config-the-home-seed), [tools](#tools), [project settings](#project-settings)
- [Git](#git-in-the-sandbox): [worktrees](#worktrees), [submodules](#submodules), [commit signing](#commit-signing)
- [Dev servers](#dev-servers) and [Docker-in-Docker](#docker-in-docker)
- [Running in herdr](#running-in-herdr)
- [Upgrading and resetting](#upgrading-and-resetting)
- [Differences from running on the host](#differences-from-running-on-the-host)

## Commands

```
pen claude | codex | opencode | pi
                                start an agent in this project's sandbox
pen [--docker] [--port <n>]… [--tool <spec>]… [<cmd…>]
                                shell or command in the sandbox
pen [--docker] [--port <n>]… [--tool <spec>]… run <cmd…>
                                same, for a command that clashes with a subcommand name
pen --tool list                 list the tools pen can install
pen allow <domain…> [--global]  allow domains for this project (or all projects)
pen allow                       list recently blocked domains
pen allow --defaults            list the domains pen allows out of the box
pen trust [--yes | --list]      review and approve what this project's .pen/ asks for
pen ps                          list running sessions, ports and tools across projects
pen sign [<base>]               re-sign your unpushed commits with your own key
pen upgrade [--clean]           update the base image and agents
pen reset                       wipe this project's sandbox state (logins, caches)
pen prune                       remove what moved or deleted projects left behind
```

Options go before the agent: `pen --docker --port 5173 claude`.

## Configuration

Settings live in `~/.config/pen/config.toml` (created on first run) and, optionally, a project's `.pen/config.toml`. Both are [TOML](https://toml.io): strings are quoted, and unknown keys are errors so a typo doesn't go unnoticed.

```toml
# ~/.config/pen/config.toml (keep it private: it holds secrets)
allow = [
  "example.com",
]
tools = ["dotnet:10"]

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
tools = ["go", "rust"]
```

`allow` and `tools` lists add together across both files; `allow` also includes Pen's defaults. Other keys belong to just one file. Changes apply to the next session, with [approval](#project-settings) where required. `pen allow` also updates running sessions.

### Network

The default allowlist covers AI providers, GitHub, common package and container registries, and developer docs. `pen allow --defaults` shows it.

```sh
pen allow                       # see recently blocked domains
pen allow example.com           # add to this project's allowlist
pen allow .example.com          # include all subdomains too
pen allow example.com --global  # allow in all projects
```

`pen allow` preserves the rest of your config and updates running sessions in the same directory (`--global`: all projects). Repeat the command to retry a failed live update, even if the domain is already saved. Blocked domains also appear at session end. Displayed names over 80 characters are shortened; the suggested command includes the full name.

Allowed domains can accept uploads, including with credentials supplied by a prompt injection. The proxy restricts destinations; it doesn't keep your code private from those destinations. See [network limits and the older-Docker DNS caveat](limitations.md#network-and-credentials).

### GitHub

Public repos need no token. For private repos, supply a read-only fine-grained token per owner. Pen passes it to git and `gh` without checking or reducing its permissions: a write-capable token or a sandbox login allows writes. In GitHub → Settings → Developer settings → Fine-grained tokens, choose:

- **Resource owner:** your account or the org.
- **Repository access:** only the repos you need.
- **Permissions:** Contents *Read-only* (optionally Issues and Pull requests *Read-only*).

Add it under `[github]` in `~/.config/pen/config.toml` as `<owner> = "…"`. Pen picks the token from the owner in `origin`. Set `github_owner = "<owner>"` in `.pen/config.toml` to override. Only that owner's token is in the sandbox.

### Logins and API keys

Log in to each agent inside the sandbox (`claude`, `codex login --device-auth`, etc.), once per project. Logins aren't shared between projects.

API keys go under `[env]` in `~/.config/pen/config.toml` as `<NAME> = "<value>"`. To skip Claude's per-project login, run `claude setup-token` and add the result as `CLAUDE_CODE_OAUTH_TOKEN = "…"`.

### Agent config (the home seed)

`~/.config/pen/home/` is laid out like `$HOME` and is copied into every project's sandbox home at session start. Put agent instructions, settings, MCP servers and shell config there (never secrets).

Seed files overwrite matching files on every start, regardless of modification times. Files absent from the seed are left alone: deleting a seed file stops managing it but leaves its last sandbox copy. Delete that copy inside the sandbox too if you want it removed.

Logins, credentials, history and session state stay in each project's home volume. Known credential paths (`.claude/.credentials.json`, `.codex/auth.json`, `.local/share/opencode/auth.json`, `.pi/agent/auth.json`) and `.docker/` are excluded from seed copying. Docker sessions configure their client proxy separately, preserving other fields.

Claude's `.claude.json` mixes MCP settings with login state. If the seed has an `mcpServers` map, it replaces the sandbox's whole map while preserving all other fields. Remove servers from the seed map to remove them next session. Remove `.claude.json` from the seed to let each project manage its own servers again; new seeds leave them to the project. Only the current project's `.claude/projects/<slug>/` seed directory is copied, so other projects' memory stays out.

<details>
<summary>If you have an older home seed</summary>

Pen creates the seed once and never updates it. Seeds from before tools were added register CodeGraph in `.claude.json` and `.codex/config.toml`, and disable Claude's commit and PR attribution in `.claude/settings.json`. Edit or remove those settings yourself. CodeGraph needs `tools = ["codegraph"]`; new seeds leave attribution at Claude's default.

</details>

Paths below are relative to `~/.config/pen/home/` (and `$HOME` in the sandbox). For project-specific settings, use each agent's usual project files.

| Agent | Instructions | MCP servers |
|---|---|---|
| Claude Code | `.claude/CLAUDE.md` | `mcpServers` in `.claude.json` |
| Codex | `.codex/AGENTS.md` | `[mcp_servers.<name>]` in `.codex/config.toml` |
| OpenCode | `.config/opencode/AGENTS.md` | `mcp` in `.config/opencode/opencode.json` |
| Pi | `.pi/agent/AGENTS.md` | `.pi/agent/mcp.json` |

Agents run in skip-permissions mode by default. The container is the boundary. Edit `.gitconfig` in the seed to change the identity used for sandbox commits.

### Tools

The base image has Python, the agents and common command-line tools. It has no Node on `PATH` (no `node`, `npm` or `npx`): Pen keeps a private Node in `/opt/pen/node` that only runs the agents and recipes installed through npm, and isn't for your own use: for Node in a session, use the `node` tool. Language toolchains come from tool recipes built into Pen. Name them with `--tool`, or with `tools` in either `config.toml` (the lists add up). `pen --tool list` shows them.

```
pen --tool dotnet:8 --tool dotnet:10 claude
```

Use `name` for the default version or `name:version`. Versions are taken literally: `go:1.27` is accepted; `go:1.27.0` isn't. Order and duplicates don't matter. Tools add their required domains to the session's allowlist. Project tools need [approval](#project-settings), including those domains.

| Tool | Default | Versions | Several at once | Domains it adds |
|---|---|---|---|---|
| `dotnet` | `10` | `6` to `11` (SDK major version) | yes | `.nuget.org`, `builds.dotnet.microsoft.com`, `aka.ms`, `www.microsoft.com`, `crl3.digicert.com`, `ocsp.digicert.com` |
| `go` | `1.27` | `1.21` to `1.27` | no | `proxy.golang.org`, `sum.golang.org`, `storage.googleapis.com` |
| `rust` | `stable` | `stable`, a minor version from `1.49` to `1.99` (`1.85` or `1.85.0`), or a patch release the recipe lists (`1.85.1`) | yes | `index.crates.io`, `static.crates.io` |
| `java` | `21` | `8`, `11`, `17`, `21`, `25` | yes | `repo.maven.apache.org`, `repo1.maven.org`, `services.gradle.org`, `downloads.gradle.org`, `plugins.gradle.org`, `plugins-artifacts.gradle.org` |
| `node` | `26` | `20`, `22`, `24`, `26` (major version) | no | `registry.npmjs.org` (also a default, for OpenCode), `registry.yarnpkg.com`, `repo.yarnpkg.com`, `nodejs.org` |
| `python` | `3.15` | `3.9` to `3.15` (minor version) | yes | `pypi.org`, `files.pythonhosted.org` (both also defaults for now), `releases.astral.sh` |
| `codegraph` | `1` | `1` (major version) | no | none |

Each version installs the newest release that matches it, when the image is built, except `python`: its patch release is the newest that the recipe's pinned uv knows, so a newer one needs a newer Pen, and `pen upgrade` doesn't bring it. A release newer than a recipe knows (a new Go or Python minor version, Rust release, .NET or Node major version, or JDK) needs a recipe update in a newer Pen.

<details>
<summary>Tool-specific paths, environment and setup</summary>

- **`dotnet`:** the latest SDK of each major version in `/usr/share/dotnet`, and PowerShell (`pwsh`, a version pinned in the recipe). Sets `ASPNETCORE_HTTP_PORTS=8080`, `DOTNET_RUNNING_IN_CONTAINER=true`, `DOTNET_GENERATE_ASPNET_CERTIFICATE=false`, `DOTNET_USE_POLLING_FILE_WATCHER=true`, `NUGET_XMLDOC_MODE=skip`, `POWERSHELL_DISTRIBUTION_CHANNEL`, `DOTNET_NOLOGO=1` and `DOTNET_CLI_TELEMETRY_OPTOUT=1`. .NET 6 and 7 are out of support, and 11 is a preview until it ships. The NuGet cache is the project's own volume, at your host's `NUGET_PACKAGES` path, with or without this tool.
- **`go`:** the official tarball of the newest patch release, in `/usr/local/go`. A module that asks for a newer Go downloads that toolchain through the module proxy into the home volume. The proxy redirects toolchains and large modules to `storage.googleapis.com`, which is all of Google Cloud Storage, so sessions with Go can reach any bucket there. `go install` puts programs in `~/go/bin`, which isn't on `PATH`: run them by path, or add it to `PATH` in your shell config.
- **`rust`:** rustup in `/usr/local/rustup`, each toolchain with clippy and rustfmt. The default toolchain is `stable` if listed, otherwise the newest. Cargo's cache and `cargo install` go to `~/.cargo` in the home volume. `~/.cargo/bin` isn't on `PATH`, so run what `cargo install` puts there by path, or add it to `PATH` in your shell config. A toolchain that isn't in the image (asked for by `rust-toolchain.toml`, say) fails in a session rather than installing: add it as a tool.
- **`java`:** Ubuntu's headless JDK. `java` and `javac` are the newest one's; the others are in `/usr/lib/jvm`. Maven and Gradle aren't installed: use the project's wrappers (`mvnw`, `gradlew`). Java ignores `HTTPS_PROXY`, so the recipe points each JDK at the session's proxy (`proxy:3128`), and sets `MAVEN_OPTS` so Maven uses that. A `.pen/Dockerfile` step builds without a proxy, so a `RUN` step that downloads with Java needs `JAVA_TOOL_OPTIONS="-Dhttp.proxyHost= -Dhttps.proxyHost="` to connect directly.
- **`node`:** the official tarball of the newest release of the major version, in `/usr/local`, with `npm` and `npx` (and `corepack` up to Node 24; Node 25 and later don't bundle it). npm's global prefix is `/usr/local`, in the image, so `npm install -g` doesn't work in a session: use `npx` or a project dependency, or a `.pen/Dockerfile`. Sets `npm_config_nodedir=/usr/local`, so node-gyp builds native modules against the tarball's headers rather than downloading them, and installs Ubuntu's `python3` and `build-essential` for it. Sets `NODE_OPTIONS=--disable-warning=UNDICI-EHPA`, which silences only Node 22's warning that its proxy support is experimental; a `NODE_OPTIONS` of your own replaces it. Node 20 is out of support, and its `fetch()` ignores the proxy (it has no `NODE_USE_ENV_PROXY`), so it can't reach the network from a session; npm itself works. MCP servers in the [seed](#agent-config-the-home-seed) started with `npx` need this tool in every project: use `tools = ["node"]` in your own config.
- **`python`:** uv's managed CPython builds of each minor version, in `/usr/local/share/uv/python`, with `python3.X` and `python3.X-config` in `/usr/local/bin` for each and `python3`, `python` and `python3-config` for the newest, ahead of the base image's Ubuntu `python3`. uv and `uvx` are in `/usr/local/bin`, at a version pinned in the recipe, and the patch release of each Python is the newest that uv version knows (uv has the list of builds built in), so a newer one needs a newer Pen. Ubuntu's `build-essential` comes too, for C extensions. uv finds these Pythons on `PATH` for `uv venv`, `uv run` and `uv sync`; a Python a project asks for that isn't in the image, uv downloads at run time into `~/.local/share/uv/python` in the home volume, from `releases.astral.sh` (or GitHub), checking it against the hash uv has for that build. Its cache is `~/.cache/uv`, in the home volume too, and the recipe sets `UV_LINK_MODE=copy`, since uv can't hardlink from there into a project. The interpreters in the image are marked `EXTERNALLY-MANAGED` (and read-only in a session), so `pip install` needs a venv (`python3 -m venv`, `uv venv`). `uv tool install` puts commands in `~/.local/bin`, which isn't on `PATH`: run them by path or with `uvx`, or add it to `PATH` in your shell config. 3.9 and 3.10 are out of support. MCP servers in the [seed](#agent-config-the-home-seed) started with `uvx` need this tool in every project: use `tools = ["python"]` in your own config.
- **`codegraph`:** [CodeGraph](https://www.npmjs.com/package/@colbymchenry/codegraph) from npm, in `/opt/pen/codegraph`, running on the Node it bundles. Git ignores `.codegraph/` in every session. To use it from an agent, add its MCP server to the [seed](#agent-config-the-home-seed), and use `tools = ["codegraph"]` in your own config so it's in every project. Without the tool, the agent can't start the server. For Claude, in `.claude.json`:

  ```json
  { "mcpServers": { "codegraph": { "type": "stdio", "command": "codegraph", "args": ["serve", "--mcp"] } } }
  ```

  For Codex, in `.codex/config.toml`:

  ```toml
  [mcp_servers.codegraph]
  command = "codegraph"
  args = ["serve", "--mcp"]
  ```

</details>

Tools are built once, on top of `pen:latest`, and shared. `pen upgrade` rebuilds the base image, and with it the tools in your config and the approved ones in the current project. The image a session runs depends on what the project has:

| Project has | Image | Shared? |
|---|---|---|
| Nothing | `pen:latest` | All projects |
| Tools | `pen-tools:<hash>` | Any project with the same tools |
| `.pen/Dockerfile` | `<key>:latest`, or `<key>-<dirhash>:latest` in a linked worktree | This project or worktree |
| Tools and `.pen/Dockerfile` | `<key>:<hash>`, or `<key>-<dirhash>:<hash>` in a linked worktree | This project or worktree |

### Project settings

Optional, in the project's `.pen/` directory (read-only inside the sandbox):

- **`.pen/config.toml`:** the keys shown above. Secrets (`[github]`, `[env]`) aren't allowed here.
- **`.pen/Dockerfile`:** anything else on top of the base image (which Pen keeps tagged locally as `pen:latest`). With [tools](#tools), `FROM pen:latest` builds on the tools image, so a `RUN` step can use them:

  ```dockerfile
  FROM pen:latest
  RUN apt-get update && apt-get install -y --no-install-recommends postgresql-client \
   && rm -rf /var/lib/apt/lists/*
  ```

A repo's `.pen/` is input you may not trust, whether it came from a clone or an agent. Before using new settings, Pen asks you to approve:

- `allow` domains not already allowed;
- each tool's version and domains. A recipe adding a domain later asks again; removing one doesn't. These approvals are separate from `allow`. Tools you request through `--tool` or user config need no project approval, and don't create one;
- `docker = true`;
- a `github_owner` that differs from `origin`'s owner;
- a `.pen/Dockerfile` and its build context: everything in `.pen/` except `config.toml`. Any change there asks again. The prompt shows the Dockerfile and a path to a private snapshot for review. Builds use an approved snapshot without `config.toml`, so `COPY config.toml` won't work. Build steps have direct network access, outside the session allowlist. Special files, oversized trees, invalid UTF-8 names and symlinks that don't point directly to a file or directory within `.pen/` prevent approval.

`ports` needs no approval because it only publishes to host loopback. If you decline an approval, the session doesn't start; without a terminal, it refuses settings that need approval. `pen trust` lets you review them ahead of time, and `pen trust --list` shows the full list if a prompt was shortened. `pen trust --yes` approves without asking.

Domains added through `pen allow` count as approved; unapproved domains never reach running sessions. `pen upgrade` skips unapproved project tools and Dockerfiles. Approvals live in Pen's state directory, per project directory, so a new worktree starts with none.

## Git in the sandbox

Everyday git works inside: commit, branch, merge, rebase, fetch and worktrees. Keep host git out until all sessions end, including IDE integrations: [`.git` is untrusted during a session](limitations.md#while-a-session-runs). Because repo config and hooks are mounted read-only, run these on the host between sessions:

- `git init` for a new project;
- `git config`, `git remote add`, setting upstreams, deleting branches, hook installers (husky);
- `git submodule add`, `update --init` and `sync`;
- `git worktree prune` and `remove` for worktrees outside the project.

Push reviewed commits from the host. Read-only tokens prevent sandbox pushes; Pen doesn't block pushes made with write-capable credentials.

### Worktrees

A linked worktree (`git worktree add ../feat -b feat`) is a repo root too, so you can run an agent per branch in parallel. All worktrees of a repo are one project: they share its logins, caches, `pen reset` and the one-`--docker`-session limit. Each worktree has its own `.pen/`, so `pen allow` in one applies only to that worktree. In the sandbox, a worktree sees the repo's shared git data (objects, branches) but not the main checkout's files. It can write that data, so a session in one worktree makes [host git unsafe](limitations.md#while-a-session-runs) in all of them and in the main checkout. Worktrees of a bare repo aren't supported.

### Submodules

Set submodules up on the host (`git submodule update --init --recursive`). In the sandbox, the agent can commit inside a submodule and commit the bump in the parent repo. Each submodule's config and `hooks` directory are mounted read-only like the parent's, with the same [gaps](limitations.md#at-session-start), and a submodule's `core.hooksPath` gets no warning. Only the `origin` owner's GitHub token is available, so submodules from another owner can be fetched only if they're public.

A submodule whose checkout has its own `.git` directory (made by older git, or by cloning in place before `git submodule add`) is refused: `git submodule absorbgitdirs` on the host moves it into the parent's `.git/modules/`.

### Commit signing

Agent commits are unsigned because your signing key stays on the host. After review, `pen sign [<base>]` re-creates `<base>..HEAD` with your normal signing setup, keeping trees, authors and messages. The base defaults to the upstream branch, then `origin/HEAD`. It refuses pushed commits and headers it can't safely re-create. It also refuses a session in the same directory; [wait for sessions in other worktrees too](limitations.md#while-a-session-runs).

Commits whose signature verifies under your global git config are kept, unless a commit below them changes. A `gpgsig` header alone doesn't count: a commit the agent forged is re-signed. Settings in the repo's own config don't count either, so a repo can't make its own key verify. `gpg.minTrustLevel` is `marginal` unless you set it globally. A signature only verifies if your global config can check it: for SSH signing, set `gpg.ssh.allowedSignersFile` globally to a path outside the repo (absolute or `~/…`), or each `pen sign` re-signs everything and changes the hashes again. Pen says so when this happens, and how to fix it.

Turning on "Require signed commits" on GitHub catches a forgotten `pen sign` at push time.

## Dev servers

To open a server running in the sandbox (`npm run dev` with the `node` tool, `dotnet watch` with `dotnet`) from a browser on the host, forward its port: `pen --port 5173 --tool node claude`, or `ports = [5173]` in `.pen/config.toml` for every session. `localhost:5173` on the host then reaches port 5173 in the sandbox. The port number is the same on both sides, and only the host's `127.0.0.1` listens.

- The server must listen on `0.0.0.0`, not only `localhost`: e.g. `vite --host`, `ASPNETCORE_URLS=http://0.0.0.0:5000`.
- A port that's already in use on the host isn't remapped: `--port` fails, and a port from `.pen/config.toml` is skipped with a warning. `pen ps` shows which session has it.
- Forwarding goes from the host into the sandbox only. It gives the sandbox no way out.

## Docker-in-Docker

For projects that need Docker (Testcontainers, compose, image builds), run `pen --docker claude` or set `docker = true` in `.pen/config.toml`. The session gets its own Docker daemon, reachable at `DOCKER_HOST=tcp://dind:2375`, with traffic going through the same allowlist.

- One `--docker` session per project at a time.
- Published ports are at `dind:<port>`, not `localhost`, and only the sandbox reaches them.
- Containers get proxy env vars. Add service names (e.g. `api`) to `NO_PROXY` in compose files.
- On rootful Linux, files containers write to the project are owned by root. Pass `--user`.
- The daemon runs privileged, and the agent has full control of it, so isolation is weaker and [the git protections don't hold](limitations.md#docker-in-docker-and-the-git-protections) while it's on.

## Running in herdr

[herdr](https://herdr.dev) runs agents in terminal panes with a state sidebar. These notes come from tests with herdr 0.9.5 on macOS with OrbStack.

First, remove the sidebar's ahead/behind count in `~/.config/herdr/config.toml`:

```toml
[ui.sidebar.spaces]
rows = [["state_icon", "workspace"], ["branch"]]
```

The count runs host `git rev-list` when refs change, including agent commits, while [`.git` is untrusted](limitations.md#while-a-session-runs). With these rows, no git ran in our tests. The branch name stays; herdr reads it directly from `.git`.

Start an agent with `HERDR_AGENT=claude pen claude` (or `codex`, `opencode`, `pi`). The hint lets herdr recognize the agent behind the foreground `pen` process. It may still show idle; working, blocked and done detection hasn't been checked. Inspect it with `herdr agent get <pane>` and `herdr agent explain <pane>`.

**Exit the agent before closing its pane or stopping herdr.** herdr sends a hangup, then kills pen about a second later. In our tests, the agent was removed and git checked, but session files and possibly the proxy remained. Warnings disappear with the pane, and `pen ps` shows nothing. If this happens, run `pen true` in the project before any host git to finish cleanup. Look for `….pen-quarantine` files in the project and git dir; quarantine notices aren't repeated.

Other things to know:

- Multiple panes work, including one per worktree. Create and remove worktrees between sessions: herdr's worktree actions run host git.
- Detaching (`ctrl+b q`) or closing the attached terminal leaves sessions running; reattaching finds them again.
- `herdr agent start --kind claude` types `claude` into the shell, so it runs on the host. To route it through pen, define a host shell function `claude() { HERDR_AGENT=claude command pen claude "$@"; }`. Without the hint, herdr times out while the session keeps running.
- Splitting a pane running pen opens in `~/.local/state/pen`. Split from a shell or pass `--cwd`.
- A stopped agent can leave bracketed-paste mode on and garble what herdr types next. Run `reset` in that pane.

Keep herdr's socket out of the sandbox: its API can run host commands. Pen doesn't pass `HERDR_SOCKET_PATH`, `HERDR_BIN_PATH` or `SSH_AUTH_SOCK` through, and our test session had no `HERDR_*` variables or `~/.config/herdr`. Don't add these variables to `[env]` or put herdr's config directory inside the mounted project. Its agent integrations aren't needed for state; leave them out of the [home seed](#agent-config-the-home-seed).

## Upgrading and resetting

- To update the CLI, run `git pull` and `cli/install.sh` from your Pen source checkout. If the embedded image files changed, the next session rebuilds the base image.
- `pen upgrade` reinstalls the agents at their latest versions and pulls newer proxy and Docker images, without waiting for a Pen release (`--clean` skips the build cache). Run it in a project with approved `tools` or an approved `.pen/Dockerfile` to rebuild those too; `tools` in your own config are always rebuilt. Logins and config are kept.
- `pen reset` wipes a project's sandbox state. Linked worktrees share it, so it refuses while a session or `pen sign` runs in the main checkout or any worktree of the repo.
- `pen prune` finds the volumes, project layers and state of projects you've moved or deleted, and tools images on an old base image, and removes them after asking. Ones it can't place are listed with the commands to remove them by hand. A directory on a volume that isn't mounted looks deleted, so mount it first.

Once releases are published, Homebrew installs will use `brew upgrade pen` for CLI updates; standalone installs will use a replacement release binary. See the [planned install flow](../README.md#once-published-planned).

## Differences from running on the host

- Pasting images into the agent doesn't work. Save the image to the project and pass its path.
- Servers the agent starts are reachable from the host only on [forwarded ports](#dev-servers).
- Some git commands must run on the host. See [Git in the sandbox](#git-in-the-sandbox).
- The image has no `tzdata-legacy`, so old time zone names such as `US/Pacific` don't resolve. Use current ones (`America/Los_Angeles`), or install the package in a `.pen/Dockerfile`.
