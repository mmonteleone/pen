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
| `image/` | Base image, entrypoint and managed agent config |
| `proxy/` | Squid proxy config and default domain allowlist |
| `templates/` | Initial user config and agent home seed |
| `tools/` | Tool recipes: `tools/<name>/tool.toml` and `install.sh` |
| `tests/` | Container smoke tests and helpers |

**Container files and templates are embedded in the CLI.** The installed pen never reads this checkout. Reinstall after changing `compose.yaml`, `image/`, `proxy/`, `templates/` or `tools/`, just as you would after a CLI change. Changes under `image/` trigger an image rebuild on the next session.

[AGENTS.md](AGENTS.md) explains command flow, sessions, locking and git protection. Read it before changing session handling or anything that touches the host repo.

Keep code AOT/trim-safe: use source-generated serialization rather than reflection-based APIs. Native calls belong in `Native.cs`. Tests access internals through `InternalsVisibleTo`; use the existing `TempDir` and `Git` helpers.

Tool recipes pair a `tool.toml` (default version, accepted versions, domains and environment) with an `install.sh` that receives versions as separate arguments and runs as root during the image build. Recipes must work independently. To add one, create `tools/<name>/` with those two files, reinstall and try `pen --tool <name>`. `hidden = true` keeps a recipe out of listings, as for `pen-test` and `pen-test-b`, which the tests use. When adding or changing one, update the [tools reference](docs/usage.md#tools) and run smoke section 28 under rootful and rootless Docker.

## Build and update options

```sh
cli/install.sh                   # build and install for this machine
cli/install.sh --out DIR         # build only; leave the binary in DIR
cli/install.sh --container --out DIR  # build a Linux binary in a container, even on macOS
```

Container builds target the host machine's architecture. To install on another machine, build there or copy a binary built for the same OS and architecture to its `~/.local/bin/pen`.

The first session builds `pen:latest` locally from the embedded Dockerfile and supporting files. Run `pen upgrade` to build it ahead of time or rebuild with fresh agents and configured tools. In a project, it also rebuilds approved project tools and the approved `.pen/Dockerfile`. Logins and config persist across image rebuilds.

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
