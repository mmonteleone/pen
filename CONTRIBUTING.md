# Contributing to Pen

For code changes, build and run unit tests, rebuild the CLI, then run the affected smoke tests. Container files, templates and tool recipes are embedded in the CLI, so changes to those need a rebuild too.

For installation and everyday use, start with the [README](README.md#get-started). Commands below run from the repo root unless shown otherwise. Everyone taking part is expected to follow the [code of conduct](CODE_OF_CONDUCT.md).

## Development setup

You need Docker with Compose v2, Python 3 for the smoke tests that need a terminal, and the .NET 11 SDK pinned in [`cli/global.json`](cli/global.json). On macOS, also install Xcode command-line tools (`xcode-select --install`).

On Linux, the installer can build without a host SDK, but the `dotnet` commands below need one. The default installer image is Azure Linux 3.0; its binary runs on glibc 2.34+ (Ubuntu 22.04+, Debian 12+). Set `PEN_BUILD_IMAGE` to use a different build image.

```sh
cd cli
dotnet build      # warnings are errors
dotnet test       # unit tests (not the smoke tests)
cd ..
cli/install.sh    # install the dev build to ~/.local/bin/pen
```

Check `command -v pen` and `pen --version` to confirm you're using the binary you just installed.

## Test a change

After building, running unit tests and rebuilding the binary to test:

```sh
cd cli
dotnet run --project Pen.SmokeTests                                        # every class: the full suite
dotnet run --project Pen.SmokeTests -- --filter-class "*CliDispatchTests"  # one class
dotnet run --project Pen.SmokeTests -- --filter-method "*Symlink*"         # methods by name, in any class
```

Smoke tests need Docker and run against the installed `pen`, or the binary named by `PEN`. Rebuild before testing. To leave your installed binary alone, use `cli/install.sh --out DIR`, then set `PEN=/absolute/path/to/pen` when running the suite.

Run the classes affected by your change, and the full suite for larger changes:

| Change | Smoke test classes |
|---|---|
| Tools | `ToolsImageTests`, `ToolRecipeTests` |
| Agents | The tool classes above, plus `AgentTests` |
| .NET | The tool classes above, plus `DotNetTests` |
| `image/` | `BaseImageTests`, `BaseRuntimeTests` |

See [`cli/Pen.SmokeTests`](cli/Pen.SmokeTests) for the other classes. Filters go after `--`. Each class uses its own throwaway projects and, for config checks, a throwaway `HOME`, so classes run independently. The tests don't edit your existing `~/.config/pen`; a first run may create it.

Each check's description is its test's display name, so `--filter-display-name "refuses*"` picks checks by description, and `--output detailed` lists each one as it passes. Checks that need credentials are skipped without them, and the slow ones (trait `Category=Slow`) are skipped unless `SMOKE_SLOW=1`; `--filter-trait "Category=Slow"` selects them.

To narrow unit tests or run the CLI without installing (from `cli/`):

```sh
dotnet test --filter-class Pen.Tests.CliTests
dotnet test --filter-method "*Name*"
dotnet run --project Pen/Pen.csproj -- <args>
```

Optional smoke checks:

- `SMOKE_SLOW=1` also runs the slow checks, such as `pen upgrade`.
- `CLAUDE_CODE_OAUTH_TOKEN` and `OPENAI_API_KEY` enable agent-prompt checks, which are skipped without them. The suite writes them only to a throwaway config file (mode 600) and removes it on exit.
- [`tests/pty-run.py`](tests/pty-run.py) runs commands on a real pseudo-terminal for signal and hangup checks; see its header for options.

Before merging, run the suite on macOS arm64 and Linux amd64, with both rootful and rootless Docker on Linux. macOS testing so far uses OrbStack; results from other engines are welcome.

## Where to make changes

| Path | Purpose |
|---|---|
| `cli/Pen/` | Host CLI (.NET 11, Native AOT) |
| `cli/Pen.Tests/` | Unit tests (xunit v3 on Microsoft.Testing.Platform) |
| `cli/Pen.SmokeTests/` | Smoke tests against the installed pen (xunit v3, run with `dotnet run`) |
| `cli/Pen.TestSupport/` | Helpers both test projects share (`TempDir`, `Git`) |
| `compose.yaml` | Agent, proxy and optional Docker-in-Docker services |
| `image/` | Base image and entrypoint |
| `proxy/` | Squid proxy config and default domain allowlist |
| `templates/` | Initial user config and agent home seed |
| `tools/` | Tool and agent recipes: `tools/<name>/tool.toml` and `install.sh` |
| `tests/` | Helpers for the smoke tests (`pty-run.py`) |

**Container files and templates are embedded in the CLI.** The installed pen never reads this checkout. Reinstall after changing `compose.yaml`, `image/`, `proxy/`, `templates/` or `tools/`, just as you would after a CLI change. Changes under `image/` trigger an image rebuild on the next session.

See [Entry points in AGENTS.md](AGENTS.md#entry-points) for command dispatch, configuration approval, session locking and host Git safeguards.

Keep code AOT/trim-safe: use source-generated serialization rather than reflection-based APIs. Native calls belong in `Native.cs`. Tests access internals through `InternalsVisibleTo`; use the `TempDir` and `Git` helpers in `Pen.TestSupport`.

### Add a tool or agent

Create `tools/<name>/` with two files:

- `tool.toml` defines the default and accepted versions, allowed domains and environment variables.
- `install.sh` receives versions as separate arguments and runs as root during the image build. Recipes must work independently.

Rebuild the CLI and try `pen --tool <name>`. Set `kind = "agent"` for an agent: `pen tools` lists it separately, and `pen <name>` selects it automatically. Set `hidden = true` for test-only recipes such as `pen-test` and `pen-test-b`.

For npm-distributed agents, use `/opt/pen/lib/npm-agent.sh`. It installs with the base image's Node into `/opt/pen/<command>`, then links a native binary or wraps a JavaScript entry in `/usr/local/bin`. It also enforces the oldest supported release and verifies pinned versions. See the [helper's header](image/npm-agent.sh) for flags and the existing agent recipes for examples.

Keep agent-specific setup, including auto-update switches and managed config, in the recipe's `install.sh`. Changing the shared helper rebuilds the base and reinstalls all agents on next use.

Update the [tools reference](docs/tools.md) and run the relevant smoke classes under rootful and rootless Docker.

## Code style

Use the C# conventions in [AGENTS.md](AGENTS.md#c-engineering-guidelines). The rules below apply to `cli/Pen/`, `cli/Pen.SmokeTests/` and `cli/Pen.TestSupport/`; unit tests in `cli/Pen.Tests/` have looser formatting rules.

The build enforces formatting and style through [`.editorconfig`](.editorconfig) and treats warnings as errors. `SourceStyleTests` also checks line length, restricts native imports to `Platform/Native.cs`, and keeps `RepoGuard` from calling `HostGit` or `Proc`.

In the host CLI, [`BannedSymbols.txt`](cli/Pen/BannedSymbols.txt) restricts APIs to their owners. Use `Proc` for processes and task waits, `HostEnv` for the environment and working directory, `Out` for console output, `Retry` for delays, and the supplied `TimeProvider` for the clock. Pass the recipe catalog from `Program`. The analyzer's owner exemptions live in `.editorconfig`; test projects aren't covered.

- **Files:** one main top-level type per file, named after the type (small records and enums used with it may live alongside it), in a folder matching its namespace. A small type used only by one class may be nested in it.
- **Static or instance:** static for stateless helpers (`Allowlist.IsValidDomain`, `Out.Field`, `Native`, `Proc`); instances wherever there's context or accumulated state.
- **Commands:** each subcommand is a sealed `…Command` class in `Pen.Commands`, as is run, pen's default command (`RunCommand`, which hands the session to `SessionRunner` in `Pen.Sessions`). Each gets its context and dependencies through its constructor and has one `Run(...)` returning the exit code. Code that needs the host, project, user config and project config takes one `ProjectContext`.
- **Collected results:** a check or planner that collects results keeps them in its own fields and returns a result object, instead of taking lists to fill in. A step that runs inside a check may record into a collector its owner passes in, so what it did is kept if it stops part way (`ProtectedFileRestore` records into `CheckChanges` this way).
- **Dependencies:** whatever production code would otherwise get by default (`Answers`, `RecipeCatalog`, stdin) is passed explicitly, through constructors. No optional parameters as test seams.
- **Signatures:** at most about 5 parameters, or a parameter object. Records with more than 3 optional members use `init` properties instead of positional optionals. Return a result type rather than a tuple whenever the meaning of its parts isn't obvious.
- **Statements:** one statement per line, and an embedded statement goes on its own line. A multi-line body has braces. No one-line `try { } catch { }`, nested ternaries or conditional collection spreads (`[.. (c ? new[] { … } : [])]`): use `if`, `yield` or a list.
- **Line length:** at most 120 characters. Raw strings are exempt (help text, multi-line messages, scripts), as is, in `cli/Pen.SmokeTests/` only, a line that holds nothing but one string literal (a smoke check's description must stay one literal, and a few are longer than a line).
- **Method and property names:** methods are verbs (`FindUnapproved`, `SaveWith`), or `Is`/`Has`/`Try` predicates. Properties are nouns.
- **Local and parameter names:** words (`result`, `dindLock`). Single letters are fine only for short lambda parameters and loop indices.
- **Field names:** private instance fields are `_camelCase`; constants and static readonly fields are `PascalCase`.
- **Doc comments:** use one plain sentence for `<summary>`. Add `<returns>` when null, false or another result needs explanation. Put intent, security constraints and platform quirks in `<remarks>` or a short inline comment.

## Build and update options

```sh
cli/install.sh                   # build and install for this machine
cli/install.sh --out DIR         # build only; leave the binary in DIR
cli/install.sh --container --out DIR  # build a Linux binary in a container, even on macOS
```

Container builds target the host machine's architecture. To install on another machine, build there or copy a binary built for the same OS and architecture to its `~/.local/bin/pen`.

The first session builds `pen:latest` from the embedded files. Tool recipes build on that base in `pen-tools:<hash>` images. `pen upgrade` rebuilds the base with a new ID, then rebuilds configured tools and the current project's approved tools and Dockerfile. Other tool combinations rebuild on their next use. Logins and config persist.

To update a source installation, pull this repo and reinstall:

```sh
git pull
cli/install.sh
```

## Continuous integration

[`.github/workflows/ci.yml`](.github/workflows/ci.yml) runs `dotnet build` and `dotnet test` on macOS and Linux, then `cli/install.sh` and the smoke suite on Linux with rootful Docker and fish. The CI smoke run uses no agent credentials and leaves the slow checks disabled. Run macOS and rootless Docker smoke tests locally as described above.

Pull requests skip the build, unit tests and smoke tests when the entire PR diff touches only Markdown under `docs/`, `README.md` or `CONTRIBUTING.md`, counting both paths of a rename. All other PRs, pushes to `main`, manual runs and release runs use the full CI workflow. The `CI result` check reports the docs-only skip or the test outcomes and is the check to require for merging.

## Releasing

Only a pushed `vX.Y.Z` tag releases; other pushes never do.

1. Bump `<Version>` in `cli/Pen/Pen.csproj` and commit.
2. Tag and push: `git tag vX.Y.Z && git push origin main vX.Y.Z`.

[`.github/workflows/release.yml`](.github/workflows/release.yml) then checks that the tag matches `<Version>`, runs CI, builds Native AOT binaries natively for `osx-arm64`, `osx-x64`, `linux-x64` and `linux-arm64` with `cli/install.sh --out` (Linux in its Azure Linux container, so glibc 2.34+), checks each one's architecture and `pen --version`, and publishes `pen-X.Y.Z-<rid>.tar.gz` archives, `SHA256SUMS` and build provenance attestations to a GitHub release with generated notes. Last, it renders the formula with [`packaging/homebrew/render-formula.sh`](packaging/homebrew/render-formula.sh) and commits it to `Formula/pen.rb` in [`mmonteleone/homebrew-tap`](https://github.com/mmonteleone/homebrew-tap). A version with a `-` suffix (`0.2.0-rc.1`) is published as a prerelease and doesn't touch the tap.

One-time setup: create the `mmonteleone/homebrew-tap` repo with a default branch; create a fine-grained token with Contents read and write on that repo only; in this repo's settings add an environment named `release`, limit its deployments to `v*` tags, and store the token there as `HOMEBREW_TAP_TOKEN`.

Releases distribute the CLI only. Each binary embeds the Dockerfile and supporting files; Docker builds the base image on the user's machine. Binaries aren't signed with a Developer ID or notarized; Homebrew doesn't quarantine what it installs, but a binary downloaded in a browser needs `xattr -d com.apple.quarantine pen`.

When switching to Homebrew from a source installation, remove `~/.local/bin/pen` or put Homebrew earlier on your `PATH`. Config, logins and project volumes don't depend on the installation method.
