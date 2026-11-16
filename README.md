# Pen

Run Claude Code, Codex, OpenCode or Pi in Docker, with your project mounted inside and network access limited to allowed domains.

```sh
cd ~/source/repos/myproject
pen claude
```

Agents run with permission prompts off. When they finish, review their edits and commits on the host, then push. Logins and caches persist per project, and your repo needs no Pen-specific files.

## What it does

- Mounts your project read-write, leaving your other host files and credentials outside the container.
- Sends outbound traffic through a domain allowlist. `pen allow` adds domains without restarting.
- Mounts repo config and `.git/hooks` read-only and checks git metadata at session end for known ways to make host git run code. [Limitations apply.](docs/limitations.md)
- Passes in the GitHub token you supply for the repo's owner. Use a read-only token; your host SSH and signing keys stay outside.
- Keeps agent logins, settings and caches between sessions. Several agents can run at once, including in linked worktrees.
- Adds [toolchains](docs/usage.md#tools) (Node, Python, .NET, Go, Rust, Java), a [project Dockerfile](docs/usage.md#project-settings), [dev-server ports](docs/usage.md#dev-servers) or [Docker-in-Docker](docs/usage.md#docker-in-docker) as needed.

Pen gives these terminal agents a shared container setup, network restrictions and host git checks. If your existing agent sandbox or dev container covers those needs, you may not need Pen. Containers share a kernel; use a VM if you need a separate one.

The project is a live mount, so keep backups, and keep host git (including IDE integrations and git-aware prompts) out of the repo and its worktrees while a session runs. [Read the limitations](docs/limitations.md) before relying on Pen.

## Requirements

Pen runs on **macOS or Linux** with **Docker and Compose v2**. On macOS, OrbStack and Colima are options. Linux supports rootful and rootless Docker and needs glibc 2.34+ (Ubuntu 22.04+, Debian 12+, etc.).

Install with Homebrew or a release binary. To build from source on macOS, you also need the .NET 11 SDK pinned in [`cli/global.json`](cli/global.json) and Xcode command-line tools (`xcode-select --install`); on Linux the installer builds in a container, so no host .NET SDK is needed.

## Get started

### Install

With [Homebrew](https://brew.sh) on macOS or Linux:

```sh
brew install mmonteleone/tap/pen
```

Or download the archive for your platform (`osx-arm64`, `osx-x64`, `linux-x64` or `linux-arm64`) from [Releases](https://github.com/mmonteleone/pen/releases), check it against `SHA256SUMS` (or with `gh attestation verify <archive> --repo mmonteleone/pen`), and put `pen` on your `PATH`:

```sh
tar -xzf pen-*.tar.gz pen
install -m 0755 pen ~/.local/bin/pen
```

Release binaries aren't notarized; on macOS, if you downloaded the archive in a browser, run `xattr -d com.apple.quarantine ~/.local/bin/pen` once.

### Install from source

```sh
git clone https://github.com/mmonteleone/pen.git
cd pen
cli/install.sh
```

This builds `pen` for your machine and installs it to `~/.local/bin/`. Make sure that directory is in your `PATH`.

### Start a session

From your project root, choose an agent:

```sh
cd ~/source/repos/myproject
pen claude  # or: pen codex, pen opencode, pen pi
```

The first session builds the sandbox image and installs your chosen agent. This may take a few minutes; later sessions reuse the images. Run `pen upgrade` to refresh agents and tools.

Log in to your agent inside the sandbox when prompted. Each project keeps its own login. The first run also creates `~/.config/pen/`; put shared instructions and settings in [`~/.config/pen/home/`](docs/usage.md#agent-config-the-home-seed), using their usual home-directory paths.

For private GitHub access, [supply a read-only token](docs/usage.md#github) for the project's owner. Pen passes its permissions through unchanged, so a write-capable token allows writes.

When all sessions in the repo have ended, read any warnings, then review changes and commits on the host. Run `pen sign` if you sign commits, then push as usual. Run git setup commands such as `git remote add` on the host between sessions: `.git/config` is read-only in the sandbox.

### Allowing a blocked domain

If a download is blocked, run `pen allow` in another host terminal from the same project directory:

```sh
pen allow                # list recently blocked domains
pen allow example.com    # allow one, including in running sessions
```

Retry the request in the agent; no restart is needed. Blocked domains also appear when a session ends. [Tools](docs/usage.md#tools) add their own package-registry domains automatically.

## Commands

```sh
pen                          # shell in the sandbox
pen run git status           # run a command in the sandbox
pen --tool node run npm ci   # ...with Node and npm installed
pen allow                    # show recently blocked domains
pen allow example.com        # allow a domain, including in running sessions
pen --tool list              # list the agents and tools Pen can install
pen --tool dotnet:10 claude  # add a toolchain for this session
pen --port 5173 claude       # reach a sandbox dev server at localhost:5173
pen --docker claude          # give the session its own Docker daemon
pen trust                    # review what this project's .pen/ asks for
pen ps                       # list running sessions across projects
pen sign                     # sign unpushed commits on the host
pen upgrade                  # rebuild the base image, agents and tools
pen reset                    # wipe this project's logins and caches
pen prune                    # remove unused project state and old tool images
```

Options go before the agent name. Dev servers must listen on `0.0.0.0` inside the sandbox. Linked worktrees share the repo's logins and caches.

For a new project, add its toolchain and try a restore or build first, such as `pen --tool node run npm ci`. A repo's [`.pen/` settings](docs/usage.md#project-settings) need your approval before they can add domains, tools, Docker, a Dockerfile or a different owner's token.

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
- [Code of conduct](CODE_OF_CONDUCT.md).
- [MIT License](LICENSE).
