# Using Pen

Run Pen from your project root. For installation, see the [README](../README.md#get-started). Before your first session, read the [limitations](limitations.md).

- [Run an agent or command](#running-commands)
- [Add tools](#tools), [allow blocked domains](#network) or [open a dev server](#dev-servers)
- [Use git](#git-in-the-sandbox): [sign commits](#commit-signing), [worktrees](#worktrees), [submodules](#submodules)
- [Configure Pen](#configuration): [logins](#logins-and-api-keys), [GitHub](#github), [shared agent settings](#agent-config-the-home-seed), [project settings](#project-settings)
- [Use Docker-in-Docker](#docker-in-docker)
- [Update Pen or clean up](#upgrading-and-cleaning-up), or [troubleshoot host differences](#differences-from-running-on-the-host)
- [Command reference](#commands), [agent versions and details](#agents), [herdr integration](#running-in-herdr)

## Running commands

Start an agent, open a shell or run a single command:

```sh
pen claude                  # or: pen codex, pen opencode, pen pi
pen                         # interactive shell
pen run npm ci              # run npm
pen run uv sync             # run uv
```

Pen installs the agent you name. A plain `pen` opens your shell when your host `$SHELL` is bash, zsh or fish, and bash otherwise. Put the shell's config in the [home seed](#agent-config-the-home-seed) (`.config/fish/config.fish`, `.zshrc`). Node, Python and C build tools are preinstalled; a plain `pen` shell has no agents or other toolchains unless you [add them](#tools). Agents run with permission prompts off; the container is the boundary.

Put Pen's options before the command. Everything after the command name goes to that command: `pen --tool go claude --help` passes `--help` to Claude. `pen run <command>` also lets you run a command whose name matches a Pen subcommand, such as `pen run clean`.

Log in inside the sandbox when prompted; see [logins and API keys](#logins-and-api-keys). Your project files are mounted directly, so edits appear on the host immediately. Logins, sandbox settings and caches persist per project; linked worktrees share them.

Ctrl-C goes to the agent or command. When it exits, Pen ends the session and reports blocked domains and any git warnings. Read those warnings, then review changes and commits on the host. **Keep host git, IDE git integrations and git-aware prompts out of the repo and all its worktrees until every session ends.** See [Git in the sandbox](#git-in-the-sandbox).

Run `pen ps` in another terminal to see running sessions, their ports and tools across all projects.

## Tools

Add the tools your project needs with `--tool`:

```sh
pen --tool list                              # list available agents and tools
pen --tool go claude                         # Go
pen --tool rust codex                        # Rust, with clippy and rustfmt
pen --tool dotnet:8 --tool dotnet:10 claude  # two .NET SDKs
```

Use `name` for the default version or `name:version` to choose one. To keep tools installed for future sessions, add `tools = ["go", "rust"]` to your [user or project config](#configuration). The lists and command-line options add together; order and duplicates don't matter. Project tools need [approval](#project-settings).

Each tool also allows the network domains it needs. For example, `go` brings the Go module proxy. Tools are built into an image once and reused across projects with the same selection. Use [`pen upgrade`](#upgrading-and-cleaning-up) to refresh them.

| Tool | Default | Accepted versions | Multiple versions together |
|---|---|---|---|
| `dotnet` | `10` | `6` to `11` (SDK major version) | yes |
| `go` | `1.27` | `1.21` to `1.27` | no |
| `rust` | `stable` | `stable`, a minor version from `1.49` to `1.99` (`1.85` or `1.85.0`), or a patch release the recipe lists (`1.85.1`) | yes |
| `java` | `21` | `8`, `11`, `17`, `21`, `25` | yes |
| `codegraph` | `1` | `1` (major version) | no |

Versions are taken literally: `go:1.27` is accepted; `go:1.27.0` isn't. Each selection installs the newest matching release when the image is built. New versions outside the table also need a Pen update.

<details>
<summary>What's included without tools</summary>

The base image has `git`, `gh`, the Docker CLI (with buildx and compose plugins), `curl`, `wget`, `jq`, `ripgrep`, `fd`, `vim`, `less`, `unzip`, `xz`, `bzip2`, `patch`, `gnupg`, `openssl` and `socat`.

- Node LTS with `npm` and `npx`; see [Node and Python](#node-and-python).
- Python 3 with `pip`, `venv`, `uv` and `uvx`.
- Ubuntu's `build-essential` (`gcc`, `g++`, `make` and headers) and `python3-dev`.
- `fish` and `zsh`, besides bash.
- `ssh` and `ssh-keygen`. The sandbox reaches the network only through the HTTP proxy (plain HTTP, and HTTPS on port 443), so ssh can't connect out; GitHub SSH remotes (`git@github.com:`, `ssh://git@github.com/`) are rewritten to HTTPS.

</details>

<details>
<summary>Domains added by each tool</summary>

| Tool | Domains it adds |
|---|---|
| `dotnet` | `.nuget.org`, `builds.dotnet.microsoft.com`, `aka.ms`, `www.microsoft.com`, `crl3.digicert.com`, `ocsp.digicert.com` |
| `go` | `proxy.golang.org`, `sum.golang.org`, `storage.googleapis.com` |
| `rust` | `index.crates.io`, `static.crates.io` |
| `java` | `repo.maven.apache.org`, `repo1.maven.org`, `services.gradle.org`, `downloads.gradle.org`, `plugins.gradle.org`, `plugins-artifacts.gradle.org` |
| `codegraph` | none |

</details>

<details>
<summary>Tool-specific paths, environment and setup</summary>

### .NET

SDKs install in `/usr/share/dotnet`, with the newest release of each selected major version. PowerShell (`pwsh`) is included at a version pinned by Pen. The NuGet cache uses a project volume at your host's `NUGET_PACKAGES` path, with or without this tool.

The tool sets these environment variables:

| Variable | Value |
|---|---|
| `ASPNETCORE_HTTP_PORTS` | `8080` |
| `DOTNET_RUNNING_IN_CONTAINER` | `true` |
| `DOTNET_GENERATE_ASPNET_CERTIFICATE` | `false` |
| `DOTNET_USE_POLLING_FILE_WATCHER` | `true` |
| `NUGET_XMLDOC_MODE` | `skip` |
| `POWERSHELL_DISTRIBUTION_CHANNEL` | `PSDocker-DotnetSDK-Ubuntu-24.04` |
| `DOTNET_NOLOGO` | `1` |
| `DOTNET_CLI_TELEMETRY_OPTOUT` | `1` |

### Go

Go installs in `/usr/local/go` from the official tarball, with the newest patch release of the selected version. Ubuntu's `gcc` and `libc6-dev` are included, so cgo and `go test -race` work.

If a module needs a newer Go, Go downloads that toolchain through the module proxy into the home volume. The proxy redirects toolchains and large modules to `storage.googleapis.com`. This allows access to any bucket on that domain.

`go install` puts commands in `~/go/bin`, which isn't on `PATH`. Run them by path or add that directory to `PATH` in your shell config.

### Rust

Toolchains install through rustup in `/usr/local/rustup`, with clippy, rustfmt and Ubuntu's `build-essential` for linking and build scripts. The default toolchain is `stable` if selected, otherwise the newest installed version.

A toolchain requested by `rust-toolchain.toml` must be installed in the image; it can't be added during a session. Add it with `--tool` or in config.

Cargo's cache and installed commands go to `~/.cargo` in the home volume. `~/.cargo/bin` isn't on `PATH`; run installed commands by path or add the directory in your shell config.

### Java

Ubuntu's headless JDKs install in `/usr/lib/jvm`. `java` and `javac` use the newest installed version. Use the project's Maven or Gradle wrappers (`mvnw`, `gradlew`); Maven and Gradle aren't installed.

Java ignores `HTTPS_PROXY`, so Pen configures each JDK to use `proxy:3128` and sets `MAVEN_OPTS` for Maven to use those settings. Dockerfile build steps have no proxy: a step that downloads with Java needs `JAVA_TOOL_OPTIONS="-Dhttp.proxyHost= -Dhttps.proxyHost="` to connect directly.

### Node and Python

These are in the base image, so every session has them, and the domains for npm, PyPI and uv's downloads are allowed by default.

Node is the newest LTS release when the base is built, installed in `/usr/local` with npm and npx. `pen upgrade` rebuilds the base and picks up the newest LTS then. `npm install -g` can't write to the image's `/usr/local` prefix during a session; use `npx`, project dependencies or a `.pen/Dockerfile`. `npm_config_nodedir=/usr/local` makes node-gyp use the tarball's headers rather than download them, and `NPM_CONFIG_UPDATE_NOTIFIER=false` turns off npm's update notices because npm updates with the image.

Python is Ubuntu's `python3` (3.12) with `pip`, `venv`, `python3-dev` and a `python` alias. Use a virtual environment (`python3 -m venv`, `uv venv`) for `pip install`: the interpreter is read-only and marked `EXTERNALLY-MANAGED`. uv and uvx are at a version pinned by Pen. If a project needs another Python, uv downloads it into `~/.local/share/uv/python` from `releases.astral.sh` (or GitHub) and checks its hash.

uv's cache is `~/.cache/uv` in the home volume. `UV_LINK_MODE=copy` avoids attempts to hardlink from that volume into the project. `uv tool install` puts commands in `~/.local/bin`, which isn't on `PATH`; use `uvx`, run them by path or add the directory in your shell config.

A different Node major version or system Python needs a [`.pen/Dockerfile`](#project-settings).

### CodeGraph

[CodeGraph](https://www.npmjs.com/package/@colbymchenry/codegraph) installs from npm into `/opt/pen/codegraph` and runs on its bundled Node. Git ignores `.codegraph/` in every session.

To use it from an agent, add `tools = ["codegraph"]` to your user config and register its MCP server in the [home seed](#agent-config-the-home-seed). For Claude, use `.claude.json`:

```json
{ "mcpServers": { "codegraph": { "type": "stdio", "command": "codegraph", "args": ["serve", "--mcp"] } } }
```

For Codex, use `.codex/config.toml`:

```toml
[mcp_servers.codegraph]
command = "codegraph"
args = ["serve", "--mcp"]
```

</details>

<details>
<summary>How Pen shares images</summary>

| Project has | Image | Shared? |
|---|---|---|
| Nothing | `pen:latest` | All projects |
| Tools | `pen-tools:<hash>` | Any project with the same tools |
| `.pen/Dockerfile` | `<key>:latest`, or `<key>-<dirhash>:latest` in a linked worktree | This project or worktree |
| Tools and `.pen/Dockerfile` | `<key>:<hash>`, or `<key>-<dirhash>:<hash>` in a linked worktree | This project or worktree |

</details>

## Network

If a download or API request is blocked, run `pen allow` in another host terminal from the same project directory:

```sh
pen allow                       # see recently blocked domains
pen allow example.com           # allow this domain for the project
pen allow .example.com          # allow the domain and all its subdomains
pen allow example.com --global  # allow it in every project
pen allow --defaults            # list Pen's default allowlist
```

The change is saved and applied to running sessions in the same directory (`--global`: all projects). Retry the blocked request; you don't need to restart the agent. If a live update fails, repeat `pen allow example.com`, even if the domain is already saved. Blocked domains also appear when a session ends.

Pen's defaults cover GitHub, npm, PyPI and uv's Python downloads, container registries and developer docs. [Agents](#agents) and [tools](#tools) add their provider and other package-registry domains when installed. You can allow other domains with the commands above.

Allowed domains can accept uploads, including with credentials supplied by a prompt injection. The proxy restricts destinations; it doesn't keep your code private from those destinations. See [network limits and the older-Docker DNS caveat](limitations.md#network-and-credentials).

## Dev servers

Forward a port to open a sandbox dev server in your host browser:

```sh
pen --port 5173 claude
```

Have the server listen on `0.0.0.0` inside the sandbox, for example with `vite --host`. Then open `http://localhost:5173` on the host. For ASP.NET Core, set `ASPNETCORE_URLS=http://0.0.0.0:5000` and forward port 5000.

To forward a port in every session, set `ports = [5173]` in [`.pen/config.toml`](#project-settings). Repeat `--port` to forward several ports.

The port number is the same on both sides, and only the host's `127.0.0.1` listens. If the port is already in use, an explicit `--port` fails; a configured port is skipped with a warning. `pen ps` shows ports used by running sessions.

Forwarding goes from the host into the sandbox only. It gives the sandbox no way out.

## Git in the sandbox

The agent can commit, branch, merge, rebase and fetch inside the sandbox. Review its work and push from the host after every session in the repo has ended. [Host git is unsafe during a session](limitations.md#while-a-session-runs), including in other worktrees and IDE integrations.

Repo config and hooks are mounted read-only. Run these setup and maintenance commands on the host between sessions:

- `git init` for a new project;
- `git config`, `git remote add`, setting upstreams, deleting branches, hook installers (husky);
- `git submodule add`, `update --init` and `sync` (a submodule repo made while a session is open is moved aside when that session ends, and when any session started alongside it ends);
- `git worktree prune` and `remove` for worktrees outside the project.

Read-only tokens prevent sandbox pushes. Pen doesn't block pushes made with write-capable credentials.

### Commit signing

Agent commits are unsigned because your signing key stays on the host. After all sessions end and you've reviewed the work, sign the unpushed commits, then push:

```sh
pen sign
# or: pen sign <base>
git push
```

Pen uses your normal host signing setup and keeps trees, authors and messages. Commit hashes change. The base defaults to the upstream branch, then `origin/HEAD`. Pen refuses to rewrite pushed commits or commits with headers it can't safely re-create. It also refuses to sign while a session is open in the same directory; wait for sessions in other worktrees too.

For SSH signing, set `gpg.ssh.allowedSignersFile` in your global git config to a file outside the repo (an absolute path or `~/…`). Otherwise Pen can't verify the signatures it creates and re-signs the commits on every run, changing their hashes again. Pen explains how to fix this when it happens.

Turning on "Require signed commits" on GitHub catches a forgotten `pen sign` at push time.

<details>
<summary>How Pen decides a commit is already signed</summary>

A commit whose signature verifies under your global git config is kept, unless a commit below it changes. A `gpgsig` header alone doesn't count. Settings in the repo's own config don't count either, so a repo can't make its own key verify. `gpg.minTrustLevel` is `marginal` unless you set it globally.

</details>

### Worktrees

Run an agent per branch in parallel by starting Pen from each linked worktree. Create the worktrees on the host between sessions:

```sh
git worktree add ../feat -b feat
cd ../feat
pen claude
```

All worktrees of a repo share logins, caches, `pen clean` and the one-`--docker`-session limit. Each worktree has its own `.pen/` settings and approvals; `pen allow` in one applies only to that worktree.

A linked worktree's sandbox sees its own files and the repo's shared git data (objects and branches). Other checkouts' files stay outside. Because that git data is writable, a session in any worktree makes [host git unsafe](limitations.md#while-a-session-runs) in all of them and in the main checkout. Worktrees of a bare repo aren't supported.

### Submodules

Set submodules up on the host (`git submodule update --init --recursive`). In the sandbox, the agent can commit inside a submodule and commit the bump in the parent repo. Each submodule's config and `hooks` directory are mounted read-only like the parent's, with the same [gaps](limitations.md#at-session-start), and a submodule's `core.hooksPath` gets no warning. Only the `origin` owner's GitHub token is available, so submodules from another owner can be fetched only if they're public.

A submodule whose checkout has its own `.git` directory (made by older git, or by cloning in place before `git submodule add`) is refused: `git submodule absorbgitdirs` on the host moves it into the parent's `.git/modules/`.

## Configuration

Pen creates `~/.config/pen/` on first run. A project needs no Pen files unless you want to share its setup.

| Location | Use it for |
|---|---|
| `~/.config/pen/config.toml` | Your default tools, allowed domains, GitHub tokens and environment variables |
| `~/.config/pen/home/` | Shared agent instructions and settings, using their usual home-directory paths |
| `.pen/config.toml` | This project's tools, allowed domains, ports, Docker and GitHub owner; no secrets |
| `.pen/Dockerfile` | Extra software beyond the built-in tools |

Config files use [TOML](https://toml.io): quote strings; unknown keys are errors so typos don't go unnoticed. Put secrets only in your user config:

```toml
# ~/.config/pen/config.toml
tools = ["dotnet:10"]
allow = ["example.com"]

[github]
my-org = "github_pat_…"

[env]
CLAUDE_CODE_OAUTH_TOKEN = "…"
```

`allow` and `tools` lists add together across user and project config. `--tool` adds to the tool list; `allow` also includes Pen's defaults. Other keys belong to just one file. Changes apply to the next session, with [approval](#project-settings) where required. `pen allow` also updates running sessions.

### Logins and API keys

Log in to each agent inside the sandbox, once per project:

```sh
pen claude  # follow Claude's login prompts
pen codex login --device-auth
```

Logins persist between sessions and are shared by a repo's linked worktrees. Separate projects have separate logins.

To use an API key, add it under `[env]` in `~/.config/pen/config.toml` as `<NAME> = "<value>"`. To skip Claude's per-project login, run `claude setup-token` and save the result there as `CLAUDE_CODE_OAUTH_TOKEN = "…"`.

### GitHub

Public repos need no token. For private repos, supply a read-only fine-grained token per owner. Pen passes it to git and `gh` without checking or reducing its permissions: a write-capable token or a sandbox login allows writes. In GitHub → Settings → Developer settings → Fine-grained tokens, choose:

- **Resource owner:** your account or the org.
- **Repository access:** only the repos you need.
- **Permissions:** Contents *Read-only* (optionally Issues and Pull requests *Read-only*).

Add the token to your user config:

```toml
# ~/.config/pen/config.toml
[github]
my-org = "github_pat_…"
```

Pen picks the token for the owner of the `origin` remote. Only that owner's token is passed to the sandbox. To use another owner, set `github_owner = "<owner>"` in `.pen/config.toml`; a different owner needs [approval](#project-settings).

### Agent config (the home seed)

Put instructions, settings, MCP servers and shell config that you want in every project under `~/.config/pen/home/`. Pen calls this the **home seed**. Paths in the table are relative to the seed and the sandbox's home directory: for example, `~/.config/pen/home/.codex/AGENTS.md` becomes `~/.codex/AGENTS.md` inside each sandbox. Keep secrets in your [user config](#logins-and-api-keys).

| Agent | Instructions | MCP servers |
|---|---|---|
| Claude Code | `.claude/CLAUDE.md` | `mcpServers` in `.claude.json` |
| Codex | `.codex/AGENTS.md` | `[mcp_servers.<name>]` in `.codex/config.toml` |
| OpenCode | `.config/opencode/AGENTS.md` | `mcp` in `.config/opencode/opencode.json` |
| Pi | `.pi/agent/AGENTS.md` | `.pi/agent/mcp.json` |

For project-specific settings, use each agent's usual project files. To change the identity used for sandbox commits, edit `.gitconfig` in the seed.

Seed files overwrite matching sandbox files on every start, regardless of modification times. Removing a file from the seed stops managing it but leaves its last sandbox copy; delete that copy inside the sandbox too if you want it removed. Files that aren't in the seed, including logins and session state, stay in the project's home volume.

The seed Pen creates registers no MCP servers.

<details>
<summary>Claude MCP settings, credentials and project memory</summary>

Claude's `.claude.json` mixes MCP settings with login state. If the seed has an `mcpServers` map, it replaces the sandbox's whole map while preserving all other fields. Remove servers from the seed map to remove them next session. Remove `.claude.json` from the seed to let each project manage its own servers again; new seeds leave them to the project.

Only the current project's `.claude/projects/<slug>/` seed directory is copied, so other projects' memory stays out.

Known credential paths (`.claude/.credentials.json`, `.codex/auth.json`, `.local/share/opencode/auth.json`, `.pi/agent/auth.json`) and `.docker/` are excluded from seed copying. Docker sessions configure their client proxy separately, preserving other fields.

</details>

<details>
<summary>If you have an older home seed</summary>

Pen creates the seed once and never updates it. Seeds from before tools were added register CodeGraph in `.claude.json` and `.codex/config.toml`, and disable Claude's commit and PR attribution in `.claude/settings.json`. Edit or remove those settings yourself. CodeGraph needs `tools = ["codegraph"]`; new seeds leave attribution at Claude's default.

</details>

### Project settings

Use `.pen/config.toml` to share a project's setup. It is usually committed, so secrets (`[github]` and `[env]`) aren't allowed here. Include only the settings you need:

```toml
# .pen/config.toml
tools = ["go", "rust"]
allow = [".internal.example.org"]
ports = [5173]
docker = true
github_owner = "my-org"
```

The `.pen/` directory is read-only inside the sandbox. Because its contents may have come from a clone or an earlier agent edit, Pen asks you to approve new settings before a session starts:

| Setting | What needs approval |
|---|---|
| `allow` | Domains not already allowed |
| `tools` | Each tool's version and domains |
| `docker = true` | Access to Docker-in-Docker |
| `github_owner` | An owner different from the one in `origin` |
| `.pen/Dockerfile` | The Dockerfile and everything else in `.pen/` except `config.toml` |

Ports need no approval because they only publish to host loopback. If you decline, the session doesn't start. Without a terminal, settings that need approval are refused.

```sh
pen trust         # review and approve before starting a session
pen trust --list  # show everything needing approval; approve nothing
pen trust --yes   # approve without asking, for scripts
```

Approvals are per directory, so a new worktree starts with none. Domains added through `pen allow` count as approved; unapproved domains never reach running sessions. `pen upgrade` skips unapproved project tools and Dockerfiles.

#### Extra software with a Dockerfile

For software beyond the built-in tools, add `.pen/Dockerfile`:

```dockerfile
FROM pen:latest
RUN apt-get update && apt-get install -y --no-install-recommends postgresql-client \
 && rm -rf /var/lib/apt/lists/*
```

`FROM pen:latest` includes your selected tools, so build steps can use them. **Build steps have direct network access, outside the session allowlist.**

Pen shows the Dockerfile and a path to a private snapshot of the build context for review. It builds from the approved snapshot. A change to the Dockerfile or any other build-context file needs approval again. The context excludes `config.toml`, so `COPY config.toml` won't work.

<details>
<summary>Tool approvals and build-context restrictions</summary>

Tool approvals include their domains and are separate from `allow` approvals. A recipe adding a domain later asks again; removing one doesn't. Tools requested through `--tool` or your user config need no project approval and don't create one.

Special files, oversized trees, invalid UTF-8 names and symlinks that don't point directly to a file or directory within `.pen/` prevent Dockerfile approval. Approvals are stored in Pen's state directory, outside the project.

</details>

## Docker-in-Docker

For Testcontainers, compose or image builds, give the session its own Docker daemon:

```sh
pen --docker claude
```

The daemon runs privileged and the agent controls it, which weakens isolation and lets it [defeat the git protections](limitations.md#docker-in-docker-and-the-git-protections). Use it only when the project needs Docker.

Set `docker = true` in `.pen/config.toml` to enable it for every session. The daemon is reachable at `DOCKER_HOST=tcp://dind:2375`, with traffic going through the same allowlist.

- One `--docker` session per project at a time.
- Published ports are at `dind:<port>`, not `localhost`, and only the sandbox reaches them.
- Containers get proxy env vars. Add service names (e.g. `api`) to `NO_PROXY` in compose files.
- On rootful Linux, files containers write to the project are owned by root. Pass `--user`.

## Upgrading and cleaning up

### Update Pen itself

From your Pen source checkout:

```sh
git pull
cli/install.sh
```

If the bundled image files changed, the next session rebuilds the base image. Homebrew and standalone release binaries are planned; source installation is the current option.

### Update agents and tools

```sh
pen upgrade             # refresh the images; keep logins and config
pen upgrade --no-cache  # rebuild without the build cache
```

`pen upgrade` rebuilds the base image and pulls newer proxy and Docker-in-Docker images. It doesn't update the Pen CLI.

Tools and agents in your user config rebuild right away. In a project, its approved tools and Dockerfile rebuild too. Other combinations, including an agent selected only by `pen claude`, rebuild on their next use. Agents at `latest` get the newest release; exact agent versions stay pinned. Tool versions follow the rules in [Tools](#tools).

### Clean up

`pen clean` works on one project; `pen clean --stale` on what's left over across all of them. Both list what they'll remove and ask first.

```sh
pen clean          # start this project's sandbox fresh
pen clean --stale  # remove what moved or deleted projects and old upgrades left behind
```

`pen clean` removes this project's sandbox home (agent logins and config, shell history), NuGet cache and Docker-in-Docker images. It keeps your project files, `~/.config/pen/`, the project's approvals (`pen trust`) and its blocked-domain logs (`pen allow`). Linked worktrees share the sandbox, so it's refused while a session or `pen sign` runs in the main checkout or any worktree.

`pen clean --stale` never touches a project still in use. For projects that were moved or deleted it removes everything Pen kept: sandbox, `.pen/Dockerfile` images, approvals and logs. It also removes tool images built on a base image older than the current one, which `pen upgrade` leaves behind. State it can't identify is listed with commands to remove it by hand. If you keep projects on an external drive or another mounted filesystem, mount it first: an unavailable project directory looks deleted.

## Differences from running on the host

- Pasting images into the agent doesn't work. Save the image to the project and pass its path.
- Servers the agent starts are reachable from the host only on [forwarded ports](#dev-servers).
- Some git commands must run on the host. See [Git in the sandbox](#git-in-the-sandbox).
- The image has no `tzdata-legacy`, so old time zone names such as `US/Pacific` don't resolve. Use current ones (`America/Los_Angeles`), or install the package in a `.pen/Dockerfile`.

## Commands

```
pen [--docker] [--port <n>]… [--tool <spec>]… [<cmd…>]
                                shell or command in the sandbox
pen [--docker] [--port <n>]… [--tool <spec>]… run <cmd…>
                                same, for a command that clashes with a subcommand name
pen --tool list                 list the agents and tools pen can install
pen allow <domain…> [--global]  allow domains for this project (or all projects)
pen allow                       list recently blocked domains
pen allow --defaults            list the domains pen allows out of the box
pen trust [--yes | --list]       review and approve what this project's .pen/ asks for
pen ps                          list running sessions, ports and tools across projects
pen sign [<base>]               re-sign your unpushed commits with your own key
pen upgrade [--no-cache]        rebuild the base image, agents and tools
pen clean                       start this project's sandbox fresh (logins, caches)
pen clean --stale               remove moved/deleted projects' state and old tool images
```

Options go before the agent: `pen --docker --port 5173 claude`. Run `pen --help` for an overview or `pen <subcommand> --help` for details.

## Agents

`pen claude`, `pen codex`, `pen opencode` and `pen pi` install the named agent at its default version. No config is needed. `pen --tool list` lists the available agents and tools.

To pin a release or include an agent in a shell, select it with `--tool`:

```sh
pen --tool claude                 # a shell with Claude installed
pen --tool claude:2.1.300 claude  # run a pinned release
```

You can also add agents to `tools` in either config file. If a list already names the agent, that version is used.

Only the command's first word selects an agent automatically. `pen run claude` includes Claude; `pen run timeout 60 claude` doesn't. For a wrapper command, use `pen --tool claude run timeout 60 claude`.

`latest` means the newest release when the image is built. It isn't checked on each session. Run [`pen upgrade`](#upgrading-and-cleaning-up) to rebuild with current releases; pinned releases stay at the requested version.

OpenCode and Pi can reach Anthropic and OpenAI by default. For another provider, use `pen allow` for its API domain. Pi packages from npm (`pi install npm:<pkg>`) work, since npm is in the base.

<details>
<summary>Supported releases and domains</summary>

| Agent | Command | Default | Versions | Domains it adds |
|---|---|---|---|---|
| `claude` | `claude` (Claude Code, from npm) | `latest` | `latest` or an exact release, 2.1.113 or later (`2.1.300`) | `.anthropic.com`, `.claude.ai`, `.claude.com` |
| `codex` | `codex` (Codex, from npm) | `latest` | `latest` or an exact release, 0.1.2504211509 or later (`0.167.0`) | `.openai.com`, `.chatgpt.com` |
| `opencode` | `opencode` (OpenCode, from npm) | `latest` | `latest` or an exact release, 1.15.1 or later (`1.19.0`) | `.opencode.ai`, `models.dev`, `.anthropic.com`, `.claude.ai`, `.claude.com`, `.openai.com`, `.chatgpt.com` |
| `pi` | `pi` (Pi, from npm) | `latest` | `latest` or an exact release, 0.84.3 or later (`1.5.2`) | `.anthropic.com`, `.claude.ai`, `.claude.com`, `.openai.com`, `.chatgpt.com` |

</details>

<details>
<summary>Agent installation and auto-update settings</summary>

Agents install in `/opt/pen/<agent>`, which sessions can't write. Auto-updaters are off:

- Claude: `DISABLE_AUTOUPDATER=1` and `CLAUDE_CODE_DISABLE_AUTOUPDATE=1`.
- Codex: `check_for_update_on_startup = false` in `/etc/codex/managed_config.toml`.
- OpenCode: `OPENCODE_DISABLE_AUTOUPDATE=1`.
- Pi: `PI_SKIP_VERSION_CHECK=1`.

Claude Code and OpenCode are native binaries. Codex and Pi run on the base's Node through wrappers that call `/usr/local/bin/node` directly, so a Node from nvm or volta in the home volume doesn't affect them.

</details>

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
