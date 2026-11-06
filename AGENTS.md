# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

`pen` runs AI coding agents (Claude Code, Codex, OpenCode, Pi) in a Docker container scoped to one project directory, with egress limited to a domain allowlist and read-only GitHub access. The host CLI is a .NET 11 Native AOT binary (`cli/Pen`); the container side is `compose.yaml`, `image/` (Dockerfile + entrypoint), `proxy/` (squid + default allowlist) and `templates/` (first-run user config and home seed). See README.md for user-facing behavior; Design.md has the original requirements.

## Commands

All .NET commands run from `cli/` (SDK pinned in `cli/global.json`: 11.0 prerelease, tests on Microsoft.Testing.Platform, xunit v3).

```
cd cli
dotnet build                                   # warnings are errors
dotnet test                                    # unit tests (Pen.Tests)
dotnet test --filter-class Pen.Tests.CliTests # one class (xunit v3 filters; also --filter-method "*Name*")
dotnet run --project Pen/Pen.csproj -- <args>  # run the CLI without installing
```

Installing and container-level testing:

```
cli/install.sh                 # AOT-publish and install ~/.local/bin/pen (Linux: builds in a container)
cli/install.sh --out DIR       # build only
tests/smoke.sh                 # end-to-end checks against the *installed* pen; needs Docker/OrbStack
SMOKE_SLOW=1 tests/smoke.sh    # also exercises pen upgrade
```

`tests/smoke.sh` is run on both macOS/OrbStack (arm64) and Linux Docker (amd64, rootful and rootless); it uses throwaway projects/HOME for config-dependent checks. Optional `CLAUDE_CODE_OAUTH_TOKEN` / `OPENAI_API_KEY` enable agent-prompt checks. `tests/pty-run.py` drives commands on a real PTY for signal/hangup checks. `tests/dev-session.sh` is a legacy CLI-less session runner.

## Architecture

**Embedded resources.** `Pen.csproj` embeds `compose.yaml`, `image/**`, `proxy/**`, `templates/**` under their repo-relative paths (`Embedded.cs`). The installed binary never reads this repo, so **changes to any of those files only take effect after rebuilding/reinstalling the CLI**. `Embedded.ImageHash()` hashes `image/`; `Image.EnsureCurrent()` rebuilds `pen:latest` when the label doesn't match, so editing `image/` triggers a rebuild on next session.

**Flow** (`Program.cs`): `Cli.Parse` → `Config.EnsureUserConfig` (creates `~/.config/pen/` from `templates/`) → `Project.Resolve` (cwd real path; key `pen-<dir>-<hash>` names volumes, project image and runtime dir) → command (`Session`, `AllowCommand`, `Sign`, `Reset`). User-facing errors are `PenException` (message + exit code).

**Sessions** (`Session.cs`): each session is its own compose project `<key>-<id>` with a dir under the project's runtime dir holding the generated `compose.yaml`, `mounts.yaml` override, `squid.conf`, `allowlist.txt`, `session.env`, lock and `proxy.log`. Presence of `compose.yaml` marks a session open. Locking: sessions hold `project.lock` shared; `sign`/`reset` take it exclusive; a dind lock allows one `--docker` session per project. Sessions whose pen died are cleaned up by the next run (`FinishDeadSessions`). Signals: SIGINT/SIGQUIT go to the agent; SIGHUP/SIGTERM trigger the normal end path (`SessionSignals`). Volumes (`_home`, `_nuget`, `_dind`) are external and shared by all sessions of a project.

**Isolation**: the agent is on an `internal` network; only the squid proxy has egress. `pen allow` rewrites running sessions' allowlists live. The entrypoint (`image/entrypoint.sh`) runs twice: as root with minimal caps to create the user from `PEN_UID/PEN_GID`, then as the agent with all caps dropped; it copies the home seed (`~/.config/pen/home`, mounted read-only) into `$HOME`. Under rootless Docker the agent is UID 0 with no capabilities on a read-only root fs (`Docker.IsRootless`).

**Protecting the host's git** (`RepoGuard`, `RepoScan`, `GitCheck`, `GitConfigFile`): pen refuses linked worktrees, submodules, nested repos, symlinked `.git`, `commondir`. It mounts `.git/config`, `.git/hooks` and `.pen/` read-only over themselves (via `mounts.yaml`), and checks `.git` at session end before running any host git. `RepoGuard` reads files directly and never runs git — keep it that way, since the agent may have planted git config/hooks.

**GitHub**: read-only fine-grained PAT per owner from `github.<owner>` in user config, chosen by `origin` owner (`GitHub.cs`). `pen sign` (`Sign.cs`) re-creates unpushed commits on the host with the user's signature.

**Config** (`Config.cs`): `key = value` files. User config (`~/.config/pen/config`, secrets allowed: `github.*`, `env.*`, `allow`) vs project `.pen/config` (`allow`, `github_owner`, `docker` only — no secrets). An optional `.pen/Dockerfile` builds a per-project image `FROM pen:latest`.

## Conventions

- Code must stay AOT/trim-safe (`PublishAot`, `IsAotCompatible`): no reflection-based JSON serialization etc. Native calls live in `Native.cs`.
- Tests access internals via `InternalsVisibleTo`; use the `TempDir` and `Git` helpers in `Pen.Tests`.
- Code comments sometimes reference `PLAN.md` sections ("CLI", "Protecting the host"); that file was removed from the tree but is in git history (`git log -- PLAN.md`).
