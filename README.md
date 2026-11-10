# Pen

Run Claude Code, Codex, OpenCode or Pi in Docker, with your project mounted inside and network access limited to allowed domains.

```sh
cd ~/source/repos/myproject
pen claude
```

Agents run with permission prompts off. You review their edits and commits on the host, then push. Logins and caches persist per project, and your repo needs no pen-specific files.

Pen is a Docker container, an egress proxy and some git and credential handling, packaged as one command so you don't assemble them yourself.

## What it does

- Mounts your project read-write, leaving your other host files and credentials outside the container.
- Sends outbound traffic through a domain allowlist. `pen allow` adds domains without restarting.
- Mounts repo config and `.git/hooks` read-only and checks git metadata at session end for known ways to make host git run code. [Limitations apply.](docs/limitations.md)
- Takes a read-only GitHub token per repo owner. Your host SSH and signing keys stay outside.
- Keeps agent logins, settings and caches between sessions. Several agents can run at once, including in linked worktrees.
- Adds [toolchains](docs/usage.md#tools) (Node, Python, .NET, Go, Rust, Java), a [project Dockerfile](docs/usage.md#project-settings), [dev-server ports](docs/usage.md#dev-servers) or [Docker-in-Docker](docs/usage.md#docker-in-docker) as needed.

If your agent's sandbox or a dev container already does what you need, you're probably all set. Pen adds a shared setup for terminal agents, network restrictions and some host git protections. It uses containers, with a shared kernel; use a VM if you need a separate kernel.

The project is a live mount, so keep backups, and keep host git (including IDE integrations and git-aware prompts) out of the repo and its worktrees while a session runs. [Read the limitations](docs/limitations.md) before relying on Pen.

## Requirements

**To run Pen:**

- macOS or Linux only.
  - For Linux, glibc 2.34+ (Ubuntu 22.04+, Debian 12+, etc).
- Docker with Compose v2.
  - OrbStack and Colima are great options on macOS.
  - Linux can use rootful or rootless Docker.

**To build Pen from source** _(the only option for now btw)_:

- macOS:
  - .NET 11 SDK (see [`cli/global.json`](cli/global.json))
  - Xcode command-line tools (`xcode-select --install`).
- Linux:
  - nothing more. The installer builds in a container, so no host .NET SDK is needed.

> [!NOTE] Homebrew and standalone release binaries are planned. Neither will need a host .NET SDK.

## Get started

### Install from source

```sh
git clone https://github.com/mmonteleone/pen.git
cd pen
cli/install.sh
```

This installs `~/.local/bin/pen`, so make sure that's in your `PATH`.

### Start a session

From your project root, choose an agent:

```sh
cd ~/source/repos/myproject
pen claude  # or: pen codex, pen opencode, pen pi
```

The first session builds `pen:latest` locally from the Dockerfile bundled in the CLI, then installs the agent on top of it. This may take a few minutes. Later sessions reuse them until the bundled image files change or you run `pen upgrade`.

The first run also creates `~/.config/pen/`. Log in to your agent inside the sandbox; each project keeps its own login. Shared instructions and settings go in [`~/.config/pen/home/`](docs/usage.md#agent-config-the-home-seed), using their usual home-directory paths.

For private GitHub access, [supply a read-only token](docs/usage.md#github) for the project's owner. Pen passes its permissions through unchanged, so a write-capable token allows writes.

When the agent exits, read any warnings, then review its changes and commits on the host. Run `pen sign` if you sign commits, then push as usual. Run git setup commands such as `git remote add` on the host, between sessions: `.git/config` is read-only in the sandbox.

### Allowing a blocked domain

A new project often needs domains Pen doesn't allow by default, such as a package registry. Blocked domains are listed when the session ends, and you can allow them without restarting:

```sh
pen allow                # list recently blocked domains
pen allow example.com    # allow one, including in running sessions
pen claude               # retry
```

## Commands

```sh
pen                          # shell in the sandbox
pen run make test            # run a command in the sandbox
pen --tool node run npm ci   # ...with Node and npm installed
pen allow                    # show recently blocked domains
pen allow example.com        # allow a domain, including in running sessions
pen --tool list              # list the agents and tools pen can install
pen --tool dotnet:10 claude  # add a toolchain for this session
pen --port 5173 claude       # reach a sandbox dev server at localhost:5173
pen --docker claude          # give the session its own Docker daemon
pen trust                    # review what this project's .pen/ asks for
pen ps                       # list running sessions across projects
pen sign                     # sign unpushed commits on the host
pen upgrade                  # rebuild the base image, agents and tools
pen reset                    # wipe this project's logins and caches
pen prune                    # remove unused project state and old tools images
```

Options go before the agent name. Dev servers must listen on `0.0.0.0` inside the sandbox. Linked worktrees share the repo's logins and caches.

For a new project, try its restore or build through pen first. Missing domains appear on exit; add them with `pen allow`. A repo's [`.pen/` settings](docs/usage.md#project-settings) need your approval before they can add domains, tools, Docker, a Dockerfile or a different owner's token.

## Limitations

Pen tries to limit damage from mistakes and prompt injection. Its git protections are best-effort, and it doesn't protect against a deliberate container escape. Besides keeping host git out of running sessions:

- Review changes before running them on the host, including [agent and editor config](docs/limitations.md#project-files-host-tools-act-on) and ignored files. They can run commands with your host credentials.
- Use Docker-in-Docker only when needed. Its privileged daemon lets the agent [defeat the git protections](docs/limitations.md#docker-in-docker-and-the-git-protections), including planting hooks that Pen won't detect.

See [all limitations](docs/limitations.md) for the known gaps and host precautions.

## More

- [Usage](docs/usage.md): commands, settings and workflows, including [herdr](docs/usage.md#running-in-herdr).
- [Limitations](docs/limitations.md): what Pen doesn't protect against.
- [Contributing](CONTRIBUTING.md): building, testing and development.
- [Security](SECURITY.md): reporting vulnerabilities.
- [MIT License](LICENSE).
