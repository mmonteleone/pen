# pen

Run AI programming agents unattended, with access to your project instead of your whole
machine. `pen` runs Claude Code, Codex, OpenCode and Pi in Docker, with network access
limited to allowed domains.

> Wait, is this just Docker, a proxy, and some strong opinions as code?

It's not *not* that.

```sh
cd ~/source/repos/myproject
pen claude
```

Agents run with permission prompts off. You review their work on the host, then push it yourself.
Logins and caches persist per project. No `pen`-specific files are required in your repo.

## Features

- **Project isolation:** agents can edit your project, while the rest of your host files stay outside
  the container.
- **Network control:** outbound traffic goes through a domain allowlist. Add domains with `pen allow`
  without restarting the session.
- **Host git protection:** repo config and `.git/hooks` are mounted read-only, and `pen` checks git
  metadata when a session ends, against the known ways an agent can plant commands there.
  [There are exceptions](#limitations), so keep host git out of the repo until then.
- **Approval for project settings:** new allowed domains, Docker, another owner's token or a Dockerfile
  in a repo's `.pen/` wait for your [approval](docs/usage.md#project-settings).
- **Credentials and signing:** supply a read-only GitHub token for the project's owner.
  `pen sign` signs commits on the host without sharing your signing key.
- **Persistent setup:** each project keeps its logins, settings and caches across sessions and
  image rebuilds. Share agent instructions and settings through one
  [configuration directory](docs/usage.md#agent-config-the-home-seed).
- **Parallel work:** run several sessions or agents at once, including in separate git worktrees.
- **Development tools:** .NET, Node and Python are built in. Add tools with a
  [`.pen/Dockerfile`](docs/usage.md#project-settings),
  forward dev-server ports, or enable Docker-in-Docker when needed.

Read-only GitHub access depends on your token's permissions. pen doesn't reduce them.
The agent can still delete project files or send data to allowed domains.
[See the limitations below.](#limitations)

## How it compares

Like a [dev container](https://containers.dev/overview), `pen` gives your project a dedicated
environment. It focuses on terminal agents and requires no project configuration.

| Approach | When it fits |
|---|---|
| Agent on the host | You want direct access to your installed tools, files and credentials. |
| Agent's built-in sandbox | You're happy with that agent's isolation and permission controls. |
| Dev Container tooling | You want a `devcontainer.json` environment with editor integrations. |
| **pen** | You want agents, a network allowlist and host git protections in a container managed from your terminal. |
| VM | You want a separate kernel as an isolation boundary. `pen` uses containers. |

If your current sandbox or devcontainer already does what you need, you're probably all set.

`pen` is opinionated: work from a repo root, manage git config and remotes on the host, and review
before pushing. For macOS and Linux.

## How it works

1. `pen` starts a container with your project mounted read-write and a persistent home directory
   for that project's agent logins and settings.
2. The agent has no direct internet access. A separate proxy allows requests only to listed domains.
3. Repo config, `.git/hooks` and `.pen/` settings are mounted read-only, though on a case-insensitive
   filesystem the agent can still change `.git/config`. `pen` refuses repo layouts it can't protect.
   When the session ends, it puts back protected files that changed and moves aside the known cases
   that would make host git run code from the sandbox. [Some things it only warns about or can't
   see](#limitations).
4. The session's containers are removed on exit. Your edits, commits, logins and caches remain.

Your project is a live mount: edits appear on the host immediately, with no copy or sync step.

## Get started

### For now...

Install from source. Homebrew packages and release binaries aren't published yet.

You need Docker (or OrbStack, colima, etc) with Compose v2, plus:

- **macOS:** the .NET 11 SDK (see [`cli/global.json`](cli/global.json)) and Xcode command-line
  tools (`xcode-select --install`).
- **Linux:** glibc 2.34+ and rootful _or_ rootless Docker. The installer builds in a container, so no .NET SDK is needed.

```sh
git clone https://github.com/mmonteleone/pen.git
cd pen
cli/install.sh
```

This installs `~/.local/bin/pen` so make sure `~/.local/bin` is on your `PATH`.

### Once published (planned)

With Docker and Compose v2 installed, the Homebrew path will be:

```sh
brew install mmonteleone/tap/pen
cd ~/source/repos/myproject
pen claude
```

Standalone binaries will also be available from GitHub Releases for macOS and Linux, on
arm64 and x64. Neither installation method will need a host .NET SDK.
`brew upgrade pen` will update the CLI. `pen upgrade` will update the agents between releases.

### Start a session

On macOS, the Docker VM must share your project and `~/.config/pen` read-write at their real
paths. Projects outside your home directory may need an extra share.

From your project root, choose an agent:

```sh
cd ~/source/repos/myproject
pen claude  # or: pen codex, pen opencode, pen pi
```

Every installation builds `pen:latest` locally from the Dockerfile bundled in the CLI.
The first session may take a few minutes to build it, but later sessions reuse it until the bundled
image files change or you run `pen upgrade`.

The first run also creates `~/.config/pen/`. Log in to your agent inside the sandbox. Each
project keeps its own login. Put shared agent settings and instructions in
`~/.config/pen/home/`, using their usual home-directory paths. See [agent config](docs/usage.md#agent-config-the-home-seed) for details. For private GitHub access, [add a read-only token](docs/usage.md#github).

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
pen trust                   # review what this project's .pen/ asks for
pen ps                      # list running sessions across projects
pen sign                    # sign unpushed commits on the host
pen upgrade                 # rebuild the image with current agents
```

Options go before the agent name. Dev servers must listen on `0.0.0.0` inside the sandbox.
Several sessions can run at once. Linked worktrees share the repo's logins and caches.

For a new project, try its restore or build through `pen` first. Missing domains appear on exit.
Add them with `pen allow`. Missing tools go in a [`.pen/Dockerfile`](docs/usage.md#project-settings).

## Limitations

`pen` limits the damage from mistakes and prompt injection. It doesn't protect against a
deliberate container escape.

- While a session runs, `.git` is untrusted. Don't run git on the host in that repo until `pen`
  exits, in any of its worktrees, including through an IDE or a git-aware shell prompt and including
  `pen sign` in another worktree. The agent can plant files that run code as soon as host git reads
  them, and [the host precautions](docs/usage.md#host-precautions) don't stop this.
- The agent can delete project files, uncommitted work and unpushed commits. Keep backups.
- Allowed domains, including AI providers and GitHub, can still carry data out of the sandbox.
- Review changes before running them on the host: build scripts and other project files can
  execute commands. Read-only git config and hooks don't prevent that.
- On a case-insensitive filesystem (macOS's default), the agent can change `.git/config` by writing
  `.git/CONFIG`. `pen` warns at session start and puts the file back at session end, but host git
  would read the changed file until then.
- Hooks under a `core.hooksPath` in the repo (Husky's, say) are ordinary files the agent can change.
  `pen` warns about this at session start for the main repo, not for submodules, and doesn't block it.
- A paused rebase's commands stay in place, since it may be yours. `pen` lists them at session end.
- Docker-in-Docker uses a privileged daemon, and the agent can use it to
  [defeat the git protections](docs/usage.md#docker-in-docker-and-the-git-protections). A hook written
  that way runs on the host with no warning. Enable it only when needed.
- The git protections are best-effort. They cover known ways to make host git run code, and there
  may be others.

See [the full limitations and host setup precautions](docs/usage.md#limitations).

## More

- [Usage reference](docs/usage.md): all commands, configuration, agent settings, GitHub tokens,
  worktrees, submodules, dev servers, Docker, updates and cleanup.
- [Contributing](CONTRIBUTING.md): build, test and develop `pen`.
