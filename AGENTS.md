# Working on Pen

Pen's host CLI lives in `cli/Pen/` and ships as a .NET Native AOT binary. See [CONTRIBUTING.md](CONTRIBUTING.md) for the repository map, test commands, recipe workflow and C# style. See [usage](docs/usage.md) and [limitations](docs/limitations.md) for the behavior Pen promises users.

## Verifying changes

- Run `dotnet build` and `dotnet test` from `cli/` for code changes. The SDK is pinned in `cli/global.json` and warnings fail the build.
- Smoke tests need Docker and run against the installed binary or the binary named by `PEN`. Rebuild before running affected classes with `dotnet run --project Pen.SmokeTests -- --filter-class "*CliDispatchTests"` from `cli/`. Run the full suite for larger changes. `cli/install.sh --out DIR` builds a test binary without replacing the installed one.
- The CLI embeds `compose.yaml`, `image/`, `proxy/`, `templates/` and `tools/`. Rebuild the CLI to test edits to any of them. Changes under `image/` trigger a base image rebuild on the next session.

## Entry points

- **Commands.** Add a sealed `CliInvocation` variant, define the syntax in `CommandTree`, map the parse in `CommandParser`, and dispatch a sealed `…Command` from `Program` at the earliest tier that has its dependencies. Commands that can work with broken user or project config should keep working. Preserve `CommandBoundary`'s pass-through of agent arguments.
- **Configuration.** `ConfigLoader`, `TomlReader` and `ConfigParser` validate input. `TrustGate` and `TrustStore` govern what agent-writable `.pen/` may request. Check both paths when adding a setting and keep secrets in user config. Build a project Dockerfile only from the private `LayerContext` copy whose hash was approved.
- **Sessions.** Start with `SessionRunner` for lifecycle order and `SessionRegistry` for locks. Sessions hold project and worktree locks shared, cleaning takes the project lock exclusive, and signing takes the project lock shared and its worktree lock exclusive. Normal exits and abandoned sessions use the same `SessionTeardown` path. `compose.yaml` marks a session open. Write it last and remove it before deleting recovery records. Keep session files for retry when container removal or protected-file restoration cannot finish.
- **Host Git.** `RepoGuard` inspects files directly and must never invoke Git. Before checking Git state at session end, teardown must remove the agent and dind containers or confirm their absence through Docker. Restore protected files and complete the checks before Pen runs host Git against the repo. See [Git precautions and recovery](docs/limitations.md#at-session-end).
- **Egress.** `EgressPolicyResolver` serves session startup, listings and live updates. Keep deny rules ahead of allowances and use each running session's recorded tool domains when updating its ACL. `SessionAclUpdater` holds the session ACL lock through verifying that the proxy sees the new files and reloading Squid.

## C# engineering guidelines

### Writing readable C#

- Use descriptive names and guard clauses so readers can follow the main path. Use expression-bodied members and switch expressions when the whole operation is easy to read at a glance. Use statements for multi-step decisions and avoid nested ternaries or long chains that hide intermediate steps.
- Prefer standard .NET APIs. Use LINQ for straightforward transformations and a loop when it makes branching, early exits, or side effects clearer. Avoid repeated enumeration and unnecessary copies. Measure before adding optimizations that make the code harder to understand.
- Use Pen's idioms: file-scoped namespaces, `var` wherever legal, predefined type names, `is null` and `is not null`, type patterns, collection expressions, and target-typed `new`. Use primary constructors where they make dependencies easy to see. Follow the [contributor style guide](CONTRIBUTING.md#code-style) for formatting, including the 120-column limit and its exceptions.
- Keep related behavior together. Extract a method when its name helps explain the caller or it owns a distinct operation. Prefer composition and explicit constructor dependencies. Add an interface only when a second production implementation needs it. Give each main type its own file in the folder matching its namespace.
- Use records where value equality is meaningful and prefer immutable data. Return read-only snapshots from collectors and keep mutable lists private. Use `Try…` for expected failures where a Boolean and output suffice, with `NotNullWhen` or `MaybeNullWhen` as appropriate. Use the null-forgiving operator only when the invariant is established.
- Dispose resources you own with `using` or an explicit lifetime. Handle exceptions where the code can recover or add useful context, and preserve failures that callers need to act on.
- Write short comments that explain intent, invariants, security constraints, or platform quirks. Document a private helper's parameters or return value when they carry a constraint.
- Test observable behavior with focused inputs and expected outcomes, including relevant failure paths and boundaries. Use the shared `TempDir` and `Git` helpers in `Pen.TestSupport`. Follow existing test seams and keep production code straightforward.

### Pen's runtime conventions

- Keep the CLI AOT- and trim-safe. Use source-generated serialization and put native imports in `Platform/Native.cs`.
- Keep CLI code synchronous. Handle cancellation through POSIX signals in `SessionSignals`. Use `Proc` for process calls and task waits, `HostEnv` for the environment and working directory, `Out` for console output, `Retry` for delays, and the `TimeProvider` supplied by `Program` for the clock. `Program` loads and passes the embedded recipe catalog. See `cli/Pen/BannedSymbols.txt` and `.editorconfig` for the enforced API boundaries.

## Prose

Use “Pen” in prose and `pen` for the command. Leave Markdown prose soft-wrapped.
