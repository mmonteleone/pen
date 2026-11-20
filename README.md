# Pen

Run coding agents in a container with your project, the tools they need, and network access you control. Your other host files, SSH keys and signing keys stay outside by default.

```sh
cd ~/myproject
pen claude                  # or: pen codex, pen opencode, pen pi
```

No project config or Dockerfile is needed to start. Pen builds the environment, installs the agent and manages the session's containers and network proxy.

Agents run with permission prompts off, so they can install dependencies, run tests and make commits without approval for each command. You choose the sandbox's access, then review the result on the host.

## What you get

- **A working development environment.** Node, Python, Git and C build tools are included. Add .NET, Go, Rust or Java, forward dev server ports, or enable Docker-in-Docker for Testcontainers and Compose.
- **Control over project secrets.** [Hide specific files or directories, or substitute safe copies](docs/usage.md#hiding-files-from-the-agent): the agent can see an empty `.env` or one with placeholder values while your host keeps the real file.
- **Control over outbound traffic.** Common developer services are allowed by default. Inspect blocked domains and change the rules while the agent works.
- **Persistent logins and settings.** Log in once per repo; settings and caches survive container and image rebuilds. Keep shared agent instructions, MCP settings and shell config in one [home seed](docs/usage.md#agent-config-the-home-seed).
- **Git without your host keys.** Agents can commit, branch and rebase. Use separate Git worktrees for parallel sessions. Give agents a scoped GitHub token for private repos and `gh`; sign and push from the host afterwards.
- **Optional project defaults.** Commit tools, network rules and file-hiding settings in `.pen/config.toml` to share the setup. Pen asks before using project settings that expand access.

The checkout is a live, read-write mount: changes appear immediately, and an agent can delete work. Keep backups and keep host Git, including IDE integrations and Git-aware prompts, out of the repo until all sessions end.

Pen mounts Git config and hooks read-only and checks for known hazards at session end. Git and file-hiding protections are best-effort; Docker-in-Docker can bypass them. Allowed sites can receive uploads, and Pen doesn't protect against container escapes. Read the [limitations](docs/limitations.md) before relying on Pen.

## Get started

Pen currently installs from source. You need macOS or Linux, Docker running, and Compose v2.

- **macOS:** install the .NET SDK pinned in [`cli/global.json`](cli/global.json) and Xcode command-line tools (`xcode-select --install`).
- **Linux:** the installer builds in a container; no host .NET SDK is needed. The binary needs glibc 2.34 or later. Rootful and rootless Docker both work.

```sh
git clone https://github.com/mmonteleone/pen.git
cd pen
cli/install.sh
```

Add `~/.local/bin` to your `PATH`, then start an agent from your project root:

```sh
cd ~/myproject
pen claude
```

The first session builds the image and installs the agent, which can take a few minutes. Log in inside the sandbox when prompted. For Codex, use `pen codex login --device-auth`. For private repositories, configure a [read-only GitHub token](docs/usage.md#github).

Exit the agent to end the session. Read Pen's warnings, wait for any other sessions in the repo to end, then review changes on the host. Use [`pen sign`](docs/usage.md#commit-signing) if you want to sign the agent's commits before pushing.

## Everyday commands

| Task | Command |
|---|---|
| Open a sandbox shell | `pen` |
| Run a build or install dependencies | `pen run npm ci` |
| See blocked domains | `pen allow` |
| Allow a domain | `pen allow example.com` |
| Deny a domain, overriding allow rules | `pen deny example.com` |
| Add a toolchain for one session | `pen --tool go claude` |
| Forward a dev server port | `pen --port 5173 claude` |
| List sessions across projects | `pen ps` |
| Create project settings | `pen init` |

Run `pen allow` and `pen deny` from another host terminal in the same project directory; they save the rules and apply them to running sessions.

A forwarded dev server must listen on `0.0.0.0` inside the sandbox, then you can open `http://localhost:5173` on the host.

When a project needs its own defaults, run `pen init` and review the generated `.pen/config.toml` before committing it. Keep personal tokens, shared agent settings and hide rules for all projects under [`~/.config/pen/`](docs/usage.md#configuration).

## Documentation

- [Usage](docs/usage.md): sessions, GitHub access, network rules, configuration and commands.
- [Limitations](docs/limitations.md): what the sandbox protects and where you still need care.
- [Tools and agents](docs/tools.md): versions, agent settings and toolchain notes.
- [Git precautions and recovery](docs/git-safety.md): checks, quarantine notices and recovery failures.
- [Running in herdr](docs/herdr.md): terminal multiplexer setup and session cleanup.
- [Contributing](CONTRIBUTING.md): building, testing and adding tools.
- [Security](SECURITY.md): reporting vulnerabilities.

[MIT License](LICENSE) · [Code of conduct](CODE_OF_CONDUCT.md)
