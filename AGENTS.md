# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

`pen` runs AI coding agents (Claude Code, Codex, OpenCode, Pi) in a Docker container scoped to one project directory, with egress limited to a domain allowlist and read-only GitHub access. The host CLI is a .NET 11 Native AOT binary (`cli/Pen`); the container side is `compose.yaml`, `image/` (Dockerfile + entrypoint), `proxy/` (squid + default allowlist) and `templates/` (first-run user config and home seed). See README.md for user-facing behavior (written for the planned Homebrew/ghcr.io distribution) and CONTRIBUTING.md for the full build/test/release workflow.

## Commands

Full details in CONTRIBUTING.md. The essentials (.NET commands run from `cli/`; SDK pinned in `cli/global.json`, xunit v3 on Microsoft.Testing.Platform):

```
cd cli && dotnet build         # warnings are errors
cd cli && dotnet test          # unit tests; --filter-class Pen.Tests.CliTests / --filter-method "*Name*"
cli/install.sh                 # AOT-publish and install ~/.local/bin/pen
tests/smoke.sh                 # end-to-end checks against the *installed* pen; needs Docker
```

## Architecture

**Embedded resources.** `Pen.csproj` embeds `compose.yaml`, `image/**`, `proxy/**`, `templates/**` under their repo-relative paths (`Embedded.cs`). The installed binary never reads this repo, so **changes to any of those files only take effect after rebuilding/reinstalling the CLI**. `Embedded.ImageHash()` hashes `image/`; `Image.EnsureCurrent()` rebuilds `pen:latest` when the label doesn't match, so editing `image/` triggers a rebuild on next session. (Planned: release builds will pull a version-pinned `ghcr.io/mmonteleone/pen` image instead and tag it `pen:latest`, with `pen upgrade` reinstalling agents in a local layer; source builds keep building locally; CONTRIBUTING.md "Standing in for a release" covers the interim setup.)

**Flow** (`Program.cs`): `Cli.Parse` → `Config.EnsureUserConfig` (creates `~/.config/pen/` from `templates/`) → `Project.Resolve` (cwd real path; key `pen-<dir>-<hash>` names volumes, project image and runtime dir) → command (`Session`, `AllowCommand`, `Sign`, `Reset`). User-facing errors are `PenException` (message + exit code).

**Sessions** (`Session.cs`): each session is its own compose project `<key>-<id>` with a dir under the project's runtime dir holding the generated `compose.yaml`, `mounts.yaml` override, `squid.conf`, `allowlist.txt`, `session.env`, lock and `proxy.log`. Presence of `compose.yaml` marks a session open. Locking: sessions hold `project.lock` shared; `sign`/`reset` take it exclusive; a dind lock allows one `--docker` session per project. Sessions whose pen died are cleaned up by the next run (`FinishDeadSessions`). Signals: SIGINT/SIGQUIT go to the agent; SIGHUP/SIGTERM trigger the normal end path (`SessionSignals`). Volumes (`_home`, `_nuget`, `_dind`) are external and shared by all sessions of a project.

**Isolation**: the agent is on an `internal` network; only the squid proxy has egress. `pen allow` rewrites running sessions' allowlists live. The entrypoint (`image/entrypoint.sh`) runs twice: as root with minimal caps to create the user from `PEN_UID/PEN_GID`, then as the agent with all caps dropped; it copies the home seed (`~/.config/pen/home`, mounted read-only) into `$HOME`. Under rootless Docker the agent is UID 0 with no capabilities on a read-only root fs (`Docker.IsRootless`).

**Protecting the host's git** (`RepoGuard`, `RepoScan`, `GitCheck`, `GitConfigFile`): pen refuses linked worktrees, submodules, nested repos, symlinked `.git`, `commondir`. It mounts `.git/config`, `.git/hooks` and `.pen/` read-only over themselves (via `mounts.yaml`), and checks `.git` at session end before running any host git. `RepoGuard` reads files directly and never runs git — keep it that way, since the agent may have planted git config/hooks.

**GitHub**: read-only fine-grained PAT per owner from `[github]` in user `config.toml`, chosen by `origin` owner (`GitHub.cs`). `pen sign` (`Sign.cs`) re-creates unpushed commits on the host with the user's signature.

**Config** (`Config.cs`): `~/.config/pen/` and a project's `.pen/` each hold a `config.toml` (TOML via Tomlyn). Parsing validates the TOML syntax tree first (`SyntaxParser.Parse(…, validate: true)`, errors with line numbers), then reads the untyped `TomlTable` through the source-generated `TomlContext` (never reflection-based `TomlSerializer` overloads: AOT) and applies pen's own key checks. User `config.toml` (secrets allowed): `allow`, `[github]` tokens per owner, `[env]`. Project `.pen/config.toml`: `allow`, `github_owner`, `docker` — no secrets. Keys belong to one file or the other, except `allow`, whose lists are appended (defaults, user, project); if a key ever becomes valid in both, the convention is project scalars override and lists append. `pen allow` edits the `allow` array in place (`ConfigEdit`: insertions at syntax-tree offsets, then a re-parse that must show only the new domains added, written via temp file + rename keeping the mode). A pre-TOML `config` file in either dir is an error, not silently ignored. An optional `.pen/Dockerfile` builds a per-project image `FROM pen:latest`.

## Conventions

- Code must stay AOT/trim-safe (`PublishAot`, `IsAotCompatible`): no reflection-based JSON serialization etc. Native calls live in `Native.cs`.
- Tests access internals via `InternalsVisibleTo`; use the `TempDir` and `Git` helpers in `Pen.Tests`.
- Code comments sometimes reference `PLAN.md` sections ("CLI", "Protecting the host"); that file was removed from the tree but is in git history (`git log -- PLAN.md`).
