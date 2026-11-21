<p align="center"><img src="docs/assets/pen-logo.png" alt="Pen logo" width="256"></p>

# Pen

[![CI status on main](https://github.com/mmonteleone/pen/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/mmonteleone/pen/actions/workflows/ci.yml)

Run coding agents in a container with access only to your project, the tools they need, and the network resources you allow. Your other host files, keys, secrets, and protected in-project files stay out of reach. Pen runs alongside your existing setup without changing your workflow.

```sh
cd ~/myproject
pen claude
```

No project config or Dockerfile is needed to start. Pen builds the environment, installs the agent and manages the session's containers and network proxy.

Agents run with permission prompts off, so they can install dependencies, run tests and make commits without approval for each command. You choose the sandbox's access, then review the result on the host.

## What you get

- **A containerized agent on your live checkout.** Run [Claude Code, Codex, OpenCode or Pi](docs/tools.md#agents) with your project mounted read-write. Changes appear immediately in your host development environment.
- **Control over outbound traffic.** Common developer services are allowed by default. Inspect blocked domains and change the rules while the agent works.
- **Control over project secrets.** [Hide specific files or directories, or substitute safe copies](docs/usage.md#hiding-files-from-the-agent): the agent can see an empty `.env` or one with placeholder values while your host keeps the real file.
- **A working agent-facing development environment.** Node, Python, Git and C build tools are included. Add .NET, Go, Rust or Java toolchains, forward dev server ports, or enable Docker-in-Docker.
- **Git without your host keys.** Agents can commit, branch and rebase. Use separate Git worktrees for parallel sessions. Give agents a scoped GitHub token for private repos and `gh`; sign and push from the host afterwards.
- **Persistent logins and settings.** Log in once per repo; settings and caches survive container and image rebuilds. Keep shared agent instructions, MCP settings and shell config in one [home seed](docs/usage.md#agent-config-the-home-seed).
- **Optional project defaults.** Commit tools, network rules and file-hiding settings in `.pen/config.toml` to share the setup. Pen asks before using project settings that expand access.
- **Best-effort safeguards.** Pen mounts Git config and hooks read-only and checks for known hazards at session end. Read the [limitations](docs/limitations.md) before relying on Pen.

## Getting started

### Requirements

- macOS or Linux
- Docker and Compose v2

On macOS, [OrbStack](https://orbstack.dev) is a great option. On Linux, the binary needs glibc 2.34 or later and supports both rootful and rootless Docker.

### Install from a package

> [!NOTE]
> Homebrew packages are not yet available, but are coming soon.

_With [Homebrew](https://brew.sh) on macOS or Linux:_

```sh
brew install mmonteleone/tap/pen
```

Alternatively, download a package from [releases](https://github.com/mmonteleone/pen/releases).

### Or build from source

- On macOS, you'll need the [.NET 11.0 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/11.0) and Xcode command-line tools (`xcode-select --install`).
- On Linux, the installer builds in a container; no SDK is needed.

```sh
git clone https://github.com/mmonteleone/pen.git
cd pen
cli/install.sh
```

This builds `pen` for your machine and installs it to `~/.local/bin/`, so make sure that's in your `PATH`.

### Start a session

Start an agent from your project root:

```sh
cd ~/myproject
pen claude
```

The first session builds the image and installs the agent, which can take a few minutes. Log in inside the sandbox when prompted.

For Codex, use `pen codex login --device-auth`.

For private repositories, configure a [read-only GitHub token](docs/usage.md#github).

Exit the agent to end the session. Read Pen's warnings, wait for all other sessions in the repo and its worktrees to end, then review changes on the host.

Use [`pen sign`](docs/usage.md#commit-signing) if you want to sign the agent's commits before pushing.

## Everyday commands

| Task | Command |
|---|---|
| Start Claude Code | `pen claude` |
| Continue the last conversation | `pen claude --continue` |
| Start another agent | `pen codex`, `pen opencode`, `pen pi` |
| Open a sandbox shell | `pen` |
| Run one command in the sandbox | `pen run npm ci` |
| See blocked domains | `pen allow` |
| Allow a domain | `pen allow example.com` |
| Forward a dev server port | `pen --port 5173 claude` |
| Add a toolchain for one session | `pen --tool go claude` |
| List sessions across projects | `pen ps` |
| Sign the agent's commits | `pen sign` |

Put Pen's options before the agent name; Pen passes everything after the name to the agent.

Run `pen allow` from another host terminal in the same project directory; it saves the rule and applies it to running sessions.

See [all commands](docs/usage.md#commands) for project settings, deny rules, upgrades and cleanup.

## Configuration

While Pen doesn't require configuration stored within a project, keeping a `.pen/config.toml` can make it even easier. A config can define toolchains, allow and deny lists, ports, and [more](docs/usage.md#configuration).

Run `pen init` and review the generated `.pen/config.toml` before committing it.

Keep personal tokens, shared agent settings and hide rules for all projects under your user-scoped [`~/.config/pen/`](docs/usage.md#configuration) instead.

## Documentation

- [Usage](docs/usage.md): sessions, GitHub access, network rules, configuration and commands.
- [Limitations](docs/limitations.md): what the sandbox protects and where you still need care.
- [Tools and agents](docs/tools.md): versions, agent settings and toolchain notes.
- [Git precautions and recovery](docs/git-safety.md): checks, quarantine notices and recovery failures.
- [Running in herdr](docs/herdr.md): terminal multiplexer setup and session cleanup.
- [Contributing](CONTRIBUTING.md): building, testing and adding tools.
- [Security](SECURITY.md): reporting vulnerabilities.

[MIT License](LICENSE) · [Code of conduct](CODE_OF_CONDUCT.md)
