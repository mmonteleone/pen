# Contributing to pen

This covers building, testing and releasing pen itself. For using pen, see [README.md](README.md).

## Prerequisites

- Docker with Compose v2, as in the [README](README.md#install). Any engine works on macOS;
  OrbStack is the one that's tested.
- **macOS:** the .NET 11 SDK (version pinned in `cli/global.json`; prereleases allowed) and the
  Xcode command-line tools (`xcode-select --install`).
- **Linux:** nothing else. `cli/install.sh` builds inside a .NET SDK container. The default
  build image is Azure Linux 3.0, whose binary runs on glibc 2.34+ (Ubuntu 22.04+, Debian 12+).
  Set `PEN_BUILD_IMAGE` to override it. Install a host .NET 11 SDK only if you want to run
  `dotnet build`/`dotnet test` outside a container.

## Repo layout

| Path | What it is |
|---|---|
| `cli/Pen/` | The host CLI: .NET 11, Native AOT |
| `cli/Pen.Tests/` | Unit tests (xunit v3 on Microsoft.Testing.Platform) |
| `compose.yaml` | The session's containers: agent, proxy, optional Docker-in-Docker |
| `image/` | The base image: Dockerfile, entrypoint, codex managed config |
| `proxy/` | squid config and the default domain allowlist |
| `templates/` | What the first run copies into `~/.config/pen/` (config, home seed) |
| `tests/` | Container-level smoke tests and helpers |

`compose.yaml`, `image/`, `proxy/` and `templates/` are **embedded in the CLI binary**. The
installed pen never reads this repo, so changes to those files take effect only after you
rebuild and reinstall the CLI.

## Build and unit tests

All `dotnet` commands run from `cli/`:

```
cd cli
dotnet build                                    # warnings are errors
dotnet test                                     # unit tests
dotnet test --filter-class Pen.Tests.CliTests   # one class
dotnet test --filter-method "*Name*"            # matching methods
dotnet run --project Pen/Pen.csproj -- <args>   # run the CLI without installing
```

Code must stay AOT/trim-safe: no reflection-based JSON serialization and so on. Native calls
belong in `Native.cs`. Tests reach internals via `InternalsVisibleTo`. Use the `TempDir` and
`Git` helpers in `Pen.Tests`.

## Running a dev build

```
cli/install.sh                 # AOT-publish and install ~/.local/bin/pen
cli/install.sh --out DIR       # build only, leaving the binary in DIR
cli/install.sh --container     # build in a .NET SDK container even on macOS (Linux binary; use with --out)
```

If you also have pen from Homebrew, whichever comes first on your `PATH` wins. Check with
`command -v pen` and `pen --version`.

A source build makes its own base image. It builds `pen:latest` from the embedded `image/`
files, labelled with their hash, instead of pulling from ghcr.io. When you change anything under
`image/` and reinstall, the next session notices the hash mismatch and rebuilds
(`Image.EnsureCurrent`). `pen upgrade` forces a rebuild with fresh agents.

## Standing in for a release

Until releases are published ([see below](#releasing-planned)), a source build is how you get
pen. These steps leave you set up the way `brew install` and the first ghcr.io pull would.

1. **The binary** (instead of `brew install mmonteleone/tap/pen`):

   ```
   cli/install.sh                 # installs ~/.local/bin/pen; make sure that's on your PATH
   pen --version
   ```

   For another machine, build the binary for it and copy it to `~/.local/bin/pen` there:
   `cli/install.sh --out DIR` on that machine, or `cli/install.sh --container --out DIR` on a Mac
   for a Linux machine of the same architecture.

2. **The base image** (instead of pulling `ghcr.io/mmonteleone/pen`):

   ```
   pen upgrade                    # builds pen:latest and pulls the proxy and Docker-in-Docker images
   ```

   This works from any directory, and the build takes a few minutes. You can skip it, and the
   first session builds the image instead. Optionally, to have the image under its release name
   too, run `docker tag pen:latest ghcr.io/mmonteleone/pen:0.1.0` (the version
   `pen --version` prints, before the `+`). pen itself only uses `pen:latest` for now.

3. **Updating** (instead of `brew upgrade pen`): `git pull && cli/install.sh`. If `image/`
   changed, the next session rebuilds `pen:latest` automatically. `pen upgrade` works as the
   README describes and refreshes the agents.

When the tap goes live, delete `~/.local/bin/pen` (or put Homebrew's bin earlier on your
`PATH`) and run `brew install mmonteleone/tap/pen`. Your config, logins and project volumes
don't depend on how pen was installed.

## Smoke tests

`tests/smoke.sh` runs end-to-end checks against the **installed** pen (or the binary named in
`PEN=…`). It needs Docker, and it uses throwaway projects and a throwaway `HOME` for any check
that depends on config, so your own `~/.config/pen` is never changed.

```
tests/smoke.sh                 # all checks
SMOKE_SLOW=1 tests/smoke.sh    # also runs pen upgrade (~1 min)
```

Optional credentials enable the agent-prompt checks: `CLAUDE_CODE_OAUTH_TOKEN` and
`OPENAI_API_KEY`. They are written only to a throwaway config file (mode 600) and removed on exit.

Before merging, run the suite on macOS (arm64; OrbStack so far) and on Linux Docker (amd64), both
rootful and rootless. Runs on other macOS engines, such as Docker Desktop and Colima, are
welcome.

Helpers:

- `tests/pty-run.py` runs a command on a real pseudo-terminal, for signal and hangup checks (see
  its header for options).
- `tests/dev-session.sh` is a legacy session runner that doesn't use the CLI.

## Architecture

[AGENTS.md](AGENTS.md) describes the internals: the command flow, sessions and locking,
network isolation, how pen protects the host's git, GitHub tokens and config. Read it before
changing `Session`, `RepoGuard` or anything that touches the host repo.

## Releasing (planned)

None of this is automated yet. Meanwhile, see
[Standing in for a release](#standing-in-for-a-release). The intended flow:

1. Bump `<Version>` in `cli/Pen/Pen.csproj` and tag `vX.Y.Z`.
2. CI publishes AOT binaries for `osx-arm64`, `osx-x64`, `linux-x64` and `linux-arm64` to
   GitHub Releases.
3. CI builds the base image from `image/` and pushes `ghcr.io/mmonteleone/pen:X.Y.Z`
   (multi-arch). Release binaries pull the tag that matches their version.
4. CI updates the formula in [`mmonteleone/homebrew-tap`](https://github.com/mmonteleone/homebrew-tap).
