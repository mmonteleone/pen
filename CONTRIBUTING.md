# Contributing to Pen

Build, run unit tests, reinstall pen, then run the relevant smoke tests. Smoke tests use the installed binary, so reinstall before testing a change, including changes to `compose.yaml`, `image/`, `proxy/`, `templates/` and `tools/`, which are embedded in it.

For installation and everyday use, start with the [README](README.md#get-started). Commands below run from the repo root unless shown otherwise.

## Development setup

You need Docker with Compose v2, Python 3 for smoke tests, and the .NET 11 SDK pinned in [`cli/global.json`](cli/global.json). On macOS, also install Xcode command-line tools (`xcode-select --install`).

On Linux, the installer can build without a host SDK, but the `dotnet` commands below need one. The default installer image is Azure Linux 3.0; its binary runs on glibc 2.34+ (Ubuntu 22.04+, Debian 12+). Set `PEN_BUILD_IMAGE` to use a different build image.

```sh
cd cli
dotnet build      # warnings are errors
dotnet test
cd ..
cli/install.sh    # install the dev build to ~/.local/bin/pen
```

Check `command -v pen` and `pen --version` to confirm you're using the binary you just installed.

## Test a change

After building, running unit tests and reinstalling:

```sh
SMOKE_ONLY="7 13" tests/smoke.sh  # sections your change touches
tests/smoke.sh                    # full suite before finishing a larger change
```

Section numbers are in [`tests/smoke.sh`](tests/smoke.sh). The fixture setup always runs. For tools, run section 28; for .NET, also run section 11. The suite needs Docker and uses throwaway projects and a throwaway `HOME` for config checks; it doesn't edit your existing `~/.config/pen` (a first run may create it). Set `PEN=/absolute/path/to/pen` to test a different binary.

To narrow unit tests or run the CLI without installing (from `cli/`):

```sh
dotnet test --filter-class Pen.Tests.CliTests
dotnet test --filter-method "*Name*"
dotnet run --project Pen/Pen.csproj -- <args>
```

Optional smoke checks:

- `SMOKE_SLOW=1 tests/smoke.sh` also tests `pen upgrade`.
- `CLAUDE_CODE_OAUTH_TOKEN` and `OPENAI_API_KEY` enable agent-prompt checks. The suite writes them only to a throwaway config file (mode 600) and removes it on exit.
- [`tests/pty-run.py`](tests/pty-run.py) runs commands on a real pseudo-terminal for signal and hangup checks; see its header for options.

Before merging, run the suite on macOS arm64 and Linux amd64, with both rootful and rootless Docker on Linux. macOS testing so far uses OrbStack; results from other engines are welcome.

## Where to make changes

| Path | Purpose |
|---|---|
| `cli/Pen/` | Host CLI (.NET 11, Native AOT) |
| `cli/Pen.Tests/` | Unit tests (xunit v3 on Microsoft.Testing.Platform) |
| `compose.yaml` | Agent, proxy and optional Docker-in-Docker services |
| `image/` | Base image and entrypoint |
| `proxy/` | Squid proxy config and default domain allowlist |
| `templates/` | Initial user config and agent home seed |
| `tools/` | Tool and agent recipes: `tools/<name>/tool.toml` and `install.sh` |
| `tests/` | Container smoke tests and helpers |

**Container files and templates are embedded in the CLI.** The installed pen never reads this checkout. Reinstall after changing `compose.yaml`, `image/`, `proxy/`, `templates/` or `tools/`, just as you would after a CLI change. Changes under `image/` trigger an image rebuild on the next session.

[AGENTS.md](AGENTS.md) explains command flow, sessions, locking and git protection. Read it before changing session handling or anything that touches the host repo.

Keep code AOT/trim-safe: use source-generated serialization rather than reflection-based APIs. Native calls belong in `Native.cs`. Tests access internals through `InternalsVisibleTo`; use the existing `TempDir` and `Git` helpers.

Tool recipes pair a `tool.toml` (default version, accepted versions, domains and environment) with an `install.sh` that receives versions as separate arguments and runs as root during the image build. Recipes must work independently. To add one, create `tools/<name>/` with those two files, reinstall and try `pen --tool <name>`. `kind = "agent"` marks a recipe that installs an AI agent: `pen --tool list` shows it apart from tools, and a command that starts with its name (`pen <name>`) brings it in at its default version. `hidden = true` keeps a recipe out of listings, as for `pen-test` and `pen-test-b`, which the tests use. An agent distributed through npm needs only a few lines of `install.sh`: the base's `/opt/pen/lib/npm-agent.sh` (from `image/npm-agent.sh`, off `PATH`) installs the package with Pen's private Node into `/opt/pen/<command>`, links its native binary or wraps its JavaScript entry into `/usr/local/bin`, refuses releases older than the oldest supported one and checks `<command> --version` against a pinned version; its header documents the flags, and the `claude`, `codex`, `opencode` and `pi` recipes show both kinds. The recipe exports its agent's auto-update switches before running it and keeps anything else of its own (Codex's managed config) in its `install.sh`. Changing the helper rebuilds the base, so every agent reinstalls on next use. When adding or changing one, update the [tools reference](docs/usage.md#tools) and run smoke section 28 under rootful and rootless Docker.

## Code style

These rules apply to `cli/Pen/`. The build enforces what it can: `Pen.csproj` sets `EnforceCodeStyleInBuild`, and the `[cli/Pen/**.cs]` section of [`.editorconfig`](.editorconfig) puts its rules (usings, formatting, embedded statements, braces, blank lines, field naming, accessibility modifiers, readonly fields, unused members and parameters, expression bodies) at warning, which the build treats as an error. `SourceStyleTests` fails on any line over 120 characters outside raw strings. `cli/Pen.Tests/` isn't held to this style: its `.editorconfig` section turns these diagnostics down to suggestions, and the test build doesn't check code style.

- **Files:** one top-level type per file, named after the type, in a folder matching its namespace. A small type used only by one class may be nested in it.
- **Static or instance:** static for stateless helpers (`Allowlist.IsValidDomain`, `Out.Field`, `Native`, `Proc`); instances wherever there's context or accumulated state.
- **Commands:** each is a sealed `…Command` class in `Pen.Commands` that gets its context and dependencies through its constructor and has one `Run(...)` returning the exit code. Code that needs the host, project, user config and project config takes one `ProjectContext`.
- **Collected results:** a check or planner that collects results keeps them in its own fields and returns a result object, instead of taking lists to fill in.
- **Dependencies:** whatever production code would otherwise get by default (`Answers`, `RecipeCatalog`, stdin) is passed explicitly, through constructors. No optional parameters as test seams.
- **Signatures:** at most about 5 parameters, or a parameter object. Records with more than 3 optional members use `init` properties instead of positional optionals. Return a result type rather than a tuple whenever the meaning of its parts isn't obvious.
- **Statements:** one statement per line, and an embedded statement goes on its own line. A multi-line body has braces. No one-line `try { } catch { }`, nested ternaries or conditional collection spreads (`[.. (c ? new[] { … } : [])]`): use `if`, `yield` or a list.
- **Line length:** at most 120 characters. Raw-string help text is exempt.
- **Method and property names:** methods are verbs (`FindUnapproved`, `SaveWith`), or `Is`/`Has`/`Try` predicates. Properties are nouns.
- **Local and parameter names:** words (`result`, `dindLock`). Single letters are fine only for short lambda parameters and loop indices.
- **Field names:** private instance fields are `_camelCase`; constants and static readonly fields are `PascalCase`.
- **Doc comments:** `<summary>` says in one plain sentence what the member does or is. `<returns>` is there when the return value isn't obvious, especially for null, false or a "problem" string. The why (attack scenarios, races) goes in `<remarks>` or in an inline comment by the code it explains. For example, `TrustGate.ReadOriginOwner` has the `<summary>` "Gets the GitHub owner of the project's origin remote.", the `<returns>` "The owner, or null if there's no GitHub origin or git config can't be read." and the `<remarks>` "Null makes any github_owner in .pen/ count as differing from origin, so it needs approval."

## Build and update options

```sh
cli/install.sh                   # build and install for this machine
cli/install.sh --out DIR         # build only; leave the binary in DIR
cli/install.sh --container --out DIR  # build a Linux binary in a container, even on macOS
```

Container builds target the host machine's architecture. To install on another machine, build there or copy a binary built for the same OS and architecture to its `~/.local/bin/pen`.

The first session builds `pen:latest` locally from the embedded Dockerfile and supporting files. Agents and tools are recipes built on top of it, in `pen-tools:<hash>` images. Run `pen upgrade` to build it ahead of time or rebuild it: the base always gets a new ID, so the tools and agents in your config are rebuilt right away (in a project, also its approved tools and `.pen/Dockerfile`), and any other set of tools, such as the agent `pen claude` brings, on its next use. Logins and config persist across image rebuilds.

To update a source installation, pull this repo and reinstall:

```sh
git pull
cli/install.sh
```

## Release plan

Releases aren't published or automated yet. The intended process is:

1. Bump `<Version>` in `cli/Pen/Pen.csproj` and tag `vX.Y.Z`.
2. Publish AOT binaries for `osx-arm64`, `osx-x64`, `linux-x64` and `linux-arm64` to GitHub Releases.
3. Update the formula in [`mmonteleone/homebrew-tap`](https://github.com/mmonteleone/homebrew-tap).

Releases distribute the CLI only. Each binary embeds the Dockerfile and supporting files; Docker builds the base image on the user's machine.

The planned user workflow is in the [README](README.md#once-published-planned). When switching to Homebrew, remove `~/.local/bin/pen` or put Homebrew earlier on your `PATH`. Config, logins and project volumes don't depend on the installation method.
