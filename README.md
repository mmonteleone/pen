# pen

Run AI programming agents unattended, with access to your project instead of your whole machine.
`pen` runs Claude Code, Codex, OpenCode and Pi in Docker and limits their network access to
allowed domains.

> Wait, is this just Docker, a proxy, and some strong opinions as code?

It's not *not* that.

```sh
cd ~/source/repos/myproject
pen claude
# or: pen codex, pen opencode, pen pi
```

Agents run with permission prompts off. You review their work on the host, then push it yourself.
Logins and caches persist per project. No `pen`-specific files are required in your repo.

## Features

- **Project isolation:** agents run with permission prompts off inside a container, with access
  to your project rather than the rest of your host files.
- **Network control:** outbound traffic goes through a domain allowlist. Add domains with `pen allow`
  without restarting the session.
- **Host git protection:** repo config and hooks are read-only. `pen` checks git metadata before
  running host git, protecting against commands planted there by an agent.
- **Scoped credentials and host signing:** supply a read-only GitHub token for the project's owner.
  Review and push on the host; `pen sign` signs commits without sharing your signing key.
- **Persistent setup:** each project keeps its logins, settings and caches across sessions and
  image rebuilds. Share agent instructions and settings through one
  [configuration directory](docs/usage.md#agent-config-the-home-seed).
- **Parallel work:** run several sessions or agents at once, including in separate git worktrees.
- **Development tools:** .NET, Node and Python are built in. Add tools with a
  [`.pen/Dockerfile`](docs/usage.md#project-settings),
  forward dev-server ports, or enable Docker-in-Docker when needed.

Read-only GitHub access depends on your token's permissions; pen doesn't reduce them.
The agent can still delete project files or send data to allowed domains. [See the limits below.](#limits)

## How it compares

`pen` can be thought of as a close cousin to a [dev container](https://containers.dev/overview), intended to be more transparent and with (hopefully in most cases) less intrusion on the project and its normal dev workflow.

| Approach | When it fits |
|---|---|
| Agent on the host | You want direct access to your installed tools, files and credentials. |
| Agent's built-in sandbox | You're happy with that agent's isolation and permission controls. |
| Dev Container tooling | You want an environment defined by `devcontainer.json`, with integrations for editors and other tools that support it. |
| **pen** | You want a transparent development container managed from your terminal, with agent setup, a network allowlist and protections for host git included with little or no setup or config. |
| VM | You need a separate kernel as an isolation boundary. `pen` uses containers. |

If your current sandbox or devcontainer already does what you need, you're probably all set.

`pen` is opinionated: work from a repo root, manage git config and remotes on the host, and review
before pushing. For macOS and Linux.

## How it works

1. pen starts a container with your project mounted read-write and a persistent home directory
   for that project's agent logins and settings.
2. The agent has no direct internet access. A separate proxy allows requests only to listed domains.
3. Repo config, hooks and `.pen/` settings are mounted read-only. `pen` refuses repo layouts it
   can't protect and checks git metadata when the session ends.
4. The session's containers are removed on exit. Your edits, commits, logins and caches remain.

Your project is a live mount: edits appear on the host immediately, with no copy or sync step.

## Get started

**Install from source for now.** Homebrew packages, release binaries and the prebuilt `pen` image
aren't published yet.

You need Docker with Compose v2, plus:

- **macOS:** the .NET 11 SDK (see [`cli/global.json`](cli/global.json)) and Xcode command-line
  tools (`xcode-select --install`). The Docker VM must share your project and `~/.config/pen`
  read-write at their real paths. Projects outside your home directory may need an extra share.
- **Linux:** glibc 2.34+ and rootful or rootless Docker. The installer builds in a container;
  no host .NET SDK is needed.

```sh
git clone https://github.com/mmonteleone/pen.git
cd pen
cli/install.sh
```

This installs `~/.local/bin/pen`; make sure `~/.local/bin` is on your `PATH`.

Then open a session in your project:

```sh
cd ~/source/repos/myproject
pen claude
```

The first session builds the image, which takes a few minutes, and creates `~/.config/pen/`.
Log in to your agent inside the sandbox; each project keeps its own login. For private GitHub
access, [add a read-only token](docs/usage.md#github).

When the agent exits, review its changes and commits on the host. If you sign commits, run
`pen sign`, then push as usual. Run git setup commands such as `git remote add` on the host too.

## Everyday commands

```sh
pen                         # shell in the sandbox
pen run npm ci              # run a command in the sandbox
pen allow                   # show recently blocked domains
pen allow example.com       # allow a domain, including in running sessions
pen --port 5173 claude      # reach a sandbox dev server at localhost:5173
pen --docker claude         # give the session its own Docker daemon
pen ps                      # list running sessions across projects
pen sign                    # sign unpushed commits on the host
pen upgrade                 # rebuild the image with current agents
```

Options go before the agent name. Dev servers must listen on `0.0.0.0` inside the sandbox.
Several sessions can run at once; linked worktrees share the repo's logins and caches.

For a new project, try its restore or build through `pen` first. Missing domains appear on exit;
add them with `pen allow`. Missing tools go in a [`.pen/Dockerfile`](docs/usage.md#project-settings).

## Limits

`pen` limits the damage from mistakes and prompt injection. It doesn't protect against a
deliberate container escape.

- The agent can delete project files, uncommitted work and unpushed commits. Keep backups.
- Allowed domains, including AI providers and GitHub, can still carry data out of the sandbox.
- Review changes before running them on the host: build scripts and other project files can
  execute commands. Read-only git config and hooks don't prevent that.
- Docker-in-Docker uses a privileged daemon and weakens isolation. Enable it only when needed.

See [the full limitations and host setup precautions](docs/usage.md#limitations).

## More

- [Usage reference](docs/usage.md): all commands, configuration, agent settings, GitHub tokens,
  worktrees, submodules, dev servers, Docker, updates and cleanup.
- [Contributing](CONTRIBUTING.md): build, test and develop `pen`.
