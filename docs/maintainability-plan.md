# Pen maintainability and C# approachability plan

This plan covers five changes: give shared behavior clear owners; express the session lifecycle through focused collaborators; make external dependencies explicit; follow familiar C# API contracts; and represent command and configuration distinctions in types. The intended result is that a contributor can understand a command by reading its handler and a small number of collaborators with clear contracts.

The assessment is based on the current source and tests. Implementation has not started, and the test suites have not been rerun for this planning document. The proposed names describe ownership decisions; signatures can be refined during each implementation step without changing those decisions.

## Working approach

Keep the existing executable project, manual constructor composition, synchronous command execution, Native AOT support, and real filesystem tests. Use static methods for pure transformations and instances for external operations and stateful work. Introduce interfaces at boundaries where substituting an external operation makes an actual behavioral test possible. Keep construction in `Program` or a small application factory called from it, with dependencies passed explicitly.

Preserve CLI syntax, output streams, diagnostic text, exit codes, prompts, configuration syntax, embedded resources, image identities, trust records, and session files during structural changes. If implementation discovers a defect, record and fix it in a distinct change with a regression test. Keep extraction commits separate from API or behavioral changes so reviewers can compare the moved code directly.

Prefer constructors and method signatures that explain what an operation needs. Keep the repository's approximate five-parameter limit; a growing constructor is a reason to reconsider ownership. Parameter objects should represent related data. A container of arbitrary services would obscure dependencies again.

Do not use file length as an acceptance test. The important question is whether changing one policy, transport operation, or command presentation requires understanding unrelated responsibilities.

## Contracts every phase must preserve

| Area | Contract |
|---|---|
| Startup | Help, version, tools, and default-domain listings run before host/config/project initialization; `ps` and stale cleanup need only the host. Preserve the current handling of an unavailable starting directory. |
| Project configuration | Load `.pen/config.toml` only for commands that need it. A broken or hostile project config must continue to leave signing and cleanup usable. |
| Trust | Parsed project settings remain untrusted. Syntax validation and immutable types do not grant authority. Keep approval rules for domains, tools and their domains, Docker, owner selection, and project layers. |
| Egress | Deny wins over every allow. A user's `*` covers a project's `*`, never its separately named domains. Named allows retain their private-address behavior in open mode. |
| Tool provenance | User tools, explicitly requested tools, implied agents, and approved project tools retain their existing approval rules. Live updates use the session's recorded tool domains. |
| Live ACL updates | Hold `acl.lock` from config reads through reload. Truncate the existing mounted files in place, verify the proxy sees every written file, and only then reload Squid. Preserve retry limits and older-session behavior. |
| Lock lifetimes | Hold project/worktree locks across the full session including teardown and checking; preserve signing and cleanup lock scopes, dind exclusivity, and reverse disposal order. |
| Shared baselines | Keep both module-baseline observations and narrowing before session publication. Preserve inherited protected-file baselines and adopted-file rules across open sessions, including crashed ones. |
| Project layers | Build only from the retained private copy whose hash is approved. An existing image tag never substitutes for validating the build context. |
| Masks | Preserve native path/type/inode checks, the late revalidation immediately before writing masks, and cleanup when session-file creation fails. |
| Session publication | Write `compose.yaml` last. During cleanup, remove it before deleting the records every open session must have. Preserve the session lock and record deletion ordering. |
| Teardown | Attempt to stop every writer. Establish absence through successful removal or a successful daemon query before checking Git; a daemon error establishes nothing. Retain files for retry when required. |
| Restoration and checks | Put protected files back before dependent scans. Keep the only good copies when restoration is incomplete. A failed check forbids subsequent host Git calls. |
| Recovery | Use the same teardown path for live and abandoned sessions. Defer signing reminders until every recovered session's check has finished successfully. |
| Native and Git boundaries | Keep native calls in `Native.cs`, direct security-sensitive filesystem inspection, and host Git execution through `HostGit`. Preserve the rule that repository inspection never runs Git. |
| Terminal behavior | Keep signal timing and escalation, stream selection, control-character escaping, and output's nonthrowing behavior after terminal loss. Credentials stay out of command-line values and generated diagnostics. |

Before editing an area, map these contracts to its existing tests. Add coverage only for a missing observable contract or a newly introduced boundary; preserve existing attack, process, filesystem, and smoke scenarios.

## 1. Give shared behavior clear owners

The main entry point is `Commands/AllowCommand.cs`. It currently owns configuration writes, project preparation, live proxy updates, blocked-log queries, and presentation. `DenyCommand` reaches into it for shared operations. Several commands also reach into `Session` for project runtime initialization and recovery.

Move behavior into the following owners, retaining the existing policy and order of operations during the first extraction.

| Owner | Responsibility | Existing source |
|---|---|---|
| `Configuration/ConfigFileEditor` | Read the target, call the syntax-preserving editor, choose permissions, and write through temp file plus rename. | `AllowCommand.Append`, `ChooseMode`, `ReplaceFile` |
| `Configuration/ConfigEdit` | Transform TOML text and verify the intended semantic change. Keep this pure. | Existing `ConfigEdit` |
| `Projects/ProjectRuntime` | Ensure runtime directories and record the main checkout path. | `Session.EnsureRuntimeDir`, `WriteProjectDir`, project-dir constant |
| `Proxy/EgressPolicyResolver` | Resolve startup, current-config listing, and live-session policies through explicitly named entry points. | Session preparation and allow listing/update policy assembly |
| `Proxy/SessionAclUpdater` | Apply a policy to one session under its ACL lock, verify the proxy's view, and reload it. | `AllowCommand.Apply` and its ACL helpers |
| `Proxy/BlockedDomainQuery` | Read retained logs and classify/order blocked hosts using the current effective policy. | `FindBlockedAcross` and the query portion of `ListBlocked` |

Keep the resolver's entry points separate: startup uses the tools selected for that session; a listing considers currently approved configured tools; a live update uses recorded tool domains. Sharing the final merge transformation is useful. Substituting one source of tool domains for another would change authority.

Keep `AllowCommand` and `DenyCommand` as readable, separate handlers. Each resolves the target, arranges recovery, edits its own list, reports the change, and applies it to applicable running sessions. Allow explicitly records the domains the user requested as approved, including domains already present; deny requires no approval. Shared formatting can move to a small `EgressMessages` helper if both handlers use it. Command-specific messages stay with their command.

Resolve project/global targeting once into an `EgressTarget` value. A project target contains a project; a global target permits execution outside a project and may retain a resolved current project for recovery. This replaces the repeated `Project?` plus `bool global` combination and its null-forgiving accesses without losing current global behavior. Do not introduce a new locking or concurrency scheme for config edits during this extraction.

Keep per-session failures separate from the batch outcome. Updating multiple sessions must continue after one fails, report each failure, and return a failing exit code if any update fails. An internal `AclUpdateResult` can carry compatibility notices and expected failures; only the command decides the final exit code. Lock-wait messages may be emitted through the injected output boundary while the update is running.

Completion criteria: deny has no dependency on allow; commands have no direct Squid reload or ACL-file protocol implementation; each policy entry point makes its tool-domain source explicit; and the original partial-failure and compatibility behavior remains covered.

## 2. Express the session lifecycle through focused collaborators

Keep one visible orchestration method showing the lifecycle and the lifetime of its locks and signal handling. Extract substantial responsibilities from `Sessions/Session.cs` in small steps.

| Owner | Responsibility |
|---|---|
| `Commands/RunCommand` | Convert the run invocation and project settings into the effective `SessionRequest`, preserving Docker OR semantics and port provenance. Delegate execution to the runner. |
| `Sessions/SessionRunner` | Coordinate the session, acquire and retain locks, arrange recovery, manage the signal scope, publish the session, run the agent, and end it. This replaces the current `Session` entry point. |
| `Sessions/SessionPreparer` | Check the repo, select tools and implied agents, require trust, prepare protection/masking/module baselines, ensure images, and resolve credentials and egress. Its name must acknowledge that preparation performs I/O and may build images. |
| `Sessions/SessionFiles` | Create the session directory and write protected copies, records, environment, masks, and compose files; handle partial-write cleanup and final record deletion. |
| `Sessions/AgentRunner` | Construct and run the interactive agent process, pass secrets through environment values, coordinate the child with `SessionSignals`, and manage proxy-log trimming. |
| `Sessions/SessionTeardown` | Remove writers, run/report the Git check, stop the stack, report blocked hosts, and determine retained files and reminder eligibility. |
| `Sessions/SessionRecovery` | Find abandoned sessions under their locks, invoke the same teardown service, aggregate results, defer reminders, and expire ended directories. Available to signing, cleanup, and egress commands independently. |

`SessionRegistry` continues to own discovery and project/worktree locking. `SessionDir` continues to describe and read the on-disk session. `SessionCompose` continues to generate the override. Preserve these existing useful boundaries rather than moving their implementation into the new collaborators.

Extract runtime initialization and teardown/recovery first, since their existing static entry points already reveal separate callers. Move file generation next. Extract preparation and agent execution last, then make the remaining orchestration explicit as `SessionRunner`. This order allows the original `Session` to serve as a temporary forwarding facade, removed once production and test callers have migrated.

Keep this startup order visible: ensure runtime; acquire project/worktree locks; finish abandoned sessions; prepare and approve; acquire/check dind exclusivity; choose ports and create volumes; install signal handling; issue warnings and claim the init hint; create and lock the session directory; write files with late mask validation and final baseline narrowing; create and run the child; tear down; release lifetimes in reverse order.

Preserve the distinctions in failure handling. A refusal before publication differs from a published session that needs recovery. Failed writer removal differs from a Git check that ran but could not finish. Incomplete restoration differs from a completed restore followed by a scan failure. Carry those facts in named results and keep current retry and retention decisions intact. Exception boundaries must move with the operations they currently protect; a broad new catch around the entire lifecycle could change recovery behavior.

`SessionTeardown` returns reminder eligibility without running reminder Git calls itself. `SessionRecovery` decides when batch checks permit reminders; the single-session path makes that decision after its teardown. Retain the current meanings of `SessionEndResult` while extracting, and refine its naming only after callers are separated.

Move warnings and summaries with their relevant policy or formatter, while the runner controls when they are printed. Keep the case probes and init-hint timing inside the existing signal scope. The runner remains responsible for keeping that scope alive through teardown, even though the agent runner coordinates the actual child.

Completion criteria: the lifecycle is readable in one method; sign, clean, and egress operations can request runtime setup/recovery without constructing a running-session object; normal exit and recovery share teardown; and process/signal tests still exercise the real operating-system behavior.

## 3. Make external dependencies explicit

Introduce boundaries in the collaborators extracted above, then migrate the remaining command and application orchestration that uses the same external facilities. Keep low-level pure helpers static and keep security-sensitive filesystem operations concrete.

| Boundary | Proposed approach | Behavioral tests enabled |
|---|---|---|
| Docker daemon operations | An `IDockerClient` implemented using the existing daemon query/removal logic; expose operations with domain meaning such as remove writer and list volume users. | Failed removal, successful absence query, daemon failure, and attempts to remove both writers. |
| Compose execution | A `ComposeClient` owns session compose arguments, environment scrubbing, execution, and proxy commands. Give the ACL updater a narrow interface for reading ACLs/reloading the proxy. | A stale proxy view retries; a failed/mismatching read never reloads; a successful match permits reload. |
| Noninteractive child processes | An `IProcessRunner` accepts structured start information for run/capture operations. Its production implementation retains `Proc`'s no-inherited-handles behavior and startup-error translation. | Arguments and environment are passed literally; startup errors and signal-derived status survive the adapter. |
| Interactive agent execution | Substitute `IAgentRunner` in lifecycle tests. The concrete runner continues to own actual `Process` interaction and signal coordination. | Lifecycle failure decisions can be tested directly while existing real-child tests verify signals and escalation. |
| Image preparation | An `ISessionImageBuilder` hides the base/tools/project image sequence from session preparation; the concrete implementation retains streaming build output and context handling. | Preparation uses the selected canonical tools and stops when image preparation fails. |
| User output | An `IUserOutput` implements the existing prefix, stream, sanitization, prompt, and nonthrowing write contracts using injected writers. Retain pure `Out` formatting functions or move them to an explicitly named pure formatter. | Commands and recovery can be exercised without replacing process-wide console writers; terminal loss does not interrupt teardown. |
| Input and terminal state | Continue passing `Answers` or explicit input. Capture terminal facts where the current timing requires them, and make confirmation use supplied terminal state consistently. | Interactive and noninteractive approval/echo behavior is deterministic. |
| Environment | Read process environment at startup or at explicit process-launch boundaries. Pass only relevant facts, such as host shell, into business orchestration. Preserve the child environment construction and scrub rules. | Host settings and secret propagation can be tested without changing global environment values. |
| Time and retry delay | Use `TimeProvider` for timestamps and monotonic elapsed time. Pass an explicit delay operation into bounded retry code while preserving its current limits. | Retention boundaries, ACL timeout, and visibility retries can be tested without real sleeps. |
| Recipe catalog | Pass `RecipeCatalog` to command parsing and configuration parsing as well as existing consumers. Resolve the embedded catalog at composition. | Custom recipes exercise the same parsing route as production. |

Higher-level orchestration depends on the relevant domain boundary; concrete adapters may use the shared process runner underneath. Avoid making every command depend on both layers. Keep streaming builds and interactive children in their specialized adapters instead of forcing them through captured-text operations. Preserve exit status, signal information, environment inheritance, standard-input closure, and stream draining wherever applicable.

Production defaults belong in composition. Do not add optional parameters that silently obtain real process state when a test omits a dependency. If a captured value could become stale during a long image build, capture it at the same logical boundary as today rather than taking a universal snapshot at program startup.

Keep real `FileLock`, `Native`, inode, symlink, permission, and directory-enumeration coverage. An in-memory filesystem would not demonstrate Pen's actual security properties. Retain a representative set of shell-shim tests to verify the adapters' exact Docker/Compose command construction; move pure policy and orchestration tests away from `FakeDocker` and global console replacement as they migrate. Process/signal tests may still require serialization for genuine process-wide constraints.

Completion criteria: command and session orchestration can be tested without global console or `PATH` changes; direct process/environment/time access is concentrated in named adapters and composition; and the production adapter contract is covered with real-process and smoke tests.

## 4. Follow familiar C# API contracts

Start with the narrow, concrete convention violations in `ToolSpec.TryParse`, `ToolSpec.TryCanonicalize`, and `HidePattern.TryParse`. Change them to return `bool`, with outputs for the parsed value and an optional diagnostic. For nullable reference outputs, use nullable-flow annotations so success establishes a non-null value. Failure must set the parsed output to its documented empty/default value and supply the same error text as today.

For example, the tool parser becomes `bool TryParse(string spec, RecipeCatalog catalog, out ToolSpec tool, out string? error)`, with the appropriate failure annotation on `error`. Apply the same convention to canonicalization and hide-pattern parsing. Keep the existing throwing entry points where invalid input represents a command/configuration refusal; they convert the diagnostic into the existing `PenException` and exit-code behavior.

Update production callers and tests together for each API family. Cover valid/default versions, preservation of exact version spelling, unknown tools, version syntax restrictions, incompatible versions, canonical order/deduplication, and hide-pattern failures. Parsing and canonicalization remain pure transformations. Preserve build-time version revalidation in `ToolsImage`; a parsed value is not a reason to remove that independent boundary check.

Audit adjacent APIs for the same surprises, but distinguish parsers from intentionally nullable lookup methods. Change names only where they misstate a contract. Prefer names that reveal side effects, such as ensuring a directory, recovering sessions, or formatting a warning. Preserve expected failure handling through `PenException`; keep unexpected bugs visible through the existing outer guard. Do not replace the application with a generic result framework to solve three parser signatures.

As result objects are touched, expose read-only snapshots rather than mutable accumulator lists. Stateful check/planner collectors can retain their mutable fields internally so partial progress survives failures. Results should describe the outcome without allowing their consumer to modify the collector's history. Preserve existing meanings of null, false, and partial results until the relevant caller migration is complete.

Completion criteria: `TryParse` and `TryCanonicalize` use Boolean success, nullable flow matches the documented result, names reveal significant side effects, and a contributor can understand expected versus unexpected failure from the API and its immediate caller.

## 5. Represent important distinctions in types

### Command invocations

Replace the all-command `Command` record with an abstract `CliInvocation` and sealed variants. Keep these parsing results separate from the existing command handler classes and from the effective `SessionRequest`.

| Invocation | Data carried |
|---|---|
| `RunInvocation` | Command arguments and the explicitly requested Docker, port, and tool options. |
| `AllowDomainsInvocation`, `DenyDomainsInvocation` | Nonempty domain lists and project/global scope. |
| `ListBlockedInvocation`, `ListDeniedInvocation`, `ListDefaultDomainsInvocation` | The requested listing; unrelated mutation flags are absent. |
| `TrustInvocation` | One `TrustMode`: review, approve all, or list. |
| `CleanInvocation`, `CleanStaleInvocation` | Separate operations with different initialization requirements. |
| `SignInvocation` | Optional base revision. |
| `UpgradeInvocation` | Cache behavior. |
| `PsInvocation`, `ToolsInvocation`, `InitInvocation` | Their operation identity. |
| `BuiltinInvocation` | The validated System.CommandLine parse/action for help or version. |

Keep `CommandBoundary`, the System.CommandLine tree, validators, aliases, help formatting, and token pass-through behavior. Change the conversion of an already validated parse first. A temporary legacy conversion is acceptable within the migration, but remove it when dispatch and tests use variants. `CommandKind` may remain private to tree construction if useful; it should no longer select the meaning of a general-purpose `Args` property in application code.

Keep the staged dispatch in `Program`: zero-initialization invocations first; host-only invocations next; user-config operations next; project operations last. Construct project configuration only at the existing per-command boundaries. Use explicit pattern dispatch and manual construction. Preserve unknown-option handling, help precedence, aliases, response-file/directive disabling, and the distinction between an implicit empty shell invocation and `pen run` requiring a command.

Validate the parser migration against the existing behavioral cases. Update assertions to inspect the new variants and their relevant data; retain literal CLI inputs, diagnostics, and exit expectations. Add dispatch regression coverage only where the existing suite lacks an initialization-tier or malformed-project-config case.

### Configuration models and ownership

Separate `UserConfiguration` and `ProjectConfiguration`, with these owned fields:

| Model | Fields |
|---|---|
| `UserConfiguration` | Allow, deny, tools, hide, GitHub tokens, and environment settings. |
| `ProjectConfiguration` | Allow, deny, tools, hide, replacements, GitHub owner, Docker setting, and ports. |

Use sealed types with constructor/factory-controlled creation and read-only snapshots. Copy collections before exposing them; an `IReadOnlyList` over an aliased mutable list alone is insufficient. Preserve ordering for environment entries, replacements, ports, and diagnostics, and preserve dictionary comparison rules for owner tokens. Shared empty instances become safe only after their contents cannot be mutated. Avoid generated string representations that would reveal tokens or environment secrets; the same rule applies to prepared-session objects carrying credentials.

Share validation helpers for the four common list settings. Direct properties on each configuration keep consumers easy to read; introducing a base class is unnecessary for sharing parsing code. Preserve `bool?` for the project Docker setting while absent and explicit false remain distinct in parsing/editing contracts. Keep small semantic entries such as `FileReplacement(Target, Source)` where they clarify a `KeyValuePair`'s meaning.

Split the current `Config` responsibilities into `ConfigParser` for TOML validation and model construction, `ConfigLoader` for bounded regular-file reads and legacy-file checks, and `UserConfigInitializer` for first-run config/seed creation. Expose distinct `ParseUser`/`ParseProject` and `LoadUser`/`LoadProject` entry points. Pass the recipe catalog to parsing explicitly. Keep syntax-tree validation before deserialization, source-generated Tomlyn metadata, key validation in input order, bounded native reads, reserved-environment checks, and current messages with their file/line context.

Update `ProjectContext` to hold the two different configuration types. Migrate consumers in manageable groups: parsing/editing and templates; trust and GitHub selection; tool/image selection; egress; session preparation and warnings; remaining commands. Keep authority explicit at the existing trust gates. Avoid a universal merged configuration that loses the distinction between user settings, untrusted project requests, and effective approved values.

Keep `ConfigEdit` syntax-preserving. Its semantic post-edit comparison must cover every field of the appropriate model and compare collection contents, not collection reference identity or default record equality. The current `IsOnlyAdded` enumerates settings but omits `Hide` and `Replace`; identify completion of that comparison as an explicit corrective change with regression coverage, separate from mechanical extraction. Preserve comments/newlines and file permissions, and change nothing except the selected domain list.

Use test factories or TOML parsing to construct configurations in tests without publicly mutable production properties. Do not mechanically rewrite every existing test to assert the implementation of the new types. Preserve tests of actual parsing, policy, and filesystem behavior.

Completion criteria: handlers receive only relevant options; argument lists have explicit meanings; user credentials cannot be represented in project configuration; parsed settings cannot be mutated through a consumer; and no old catch-all command/config model remains in production dispatch.

## Delivery sequence

Each row is a reviewable milestone and may require several small commits. Finish its build, unit, AOT, and relevant smoke checks before proceeding. Temporary forwarding entry points exist only to keep each milestone buildable and are removed by the milestone that replaces their last caller.

| Step | Deliverable | Dependency and review boundary |
|---|---|---|
| 0 | Record baseline checks and map existing tests to the contract table; identify genuine coverage gaps. | Start from a clean worktree and record SDK, installed binary, Docker engine/mode, and skipped credential/slow checks. Existing passing results can serve as the baseline when recorded and applicable. |
| 1 | Boolean parser contracts and explicit catalog propagation. | Self-contained API migration before moving parsing into new owners. |
| 2 | Extract `ProjectRuntime`, `ConfigFileEditor`, and egress/ACL/query ownership. | Move behavior first; keep command output, trust, and reload order unchanged. |
| 3 | Introduce process, Docker/Compose, output, clock/delay, and image boundaries in extracted services. | Migrate consumers incrementally, retaining real adapter tests. Avoid mixing dependency changes with session-order changes. |
| 4 | Immutable configuration snapshots and parser/loader/initializer separation. | Remove mutability first while keeping a short-lived compatibility surface where needed. Verify semantic editing separately, including hide/replacement comparison. |
| 5 | Distinct user/project configuration and typed `ProjectContext`; migrate all consumers. | Build after each consumer group; remove the compatibility surface before finishing. |
| 6 | Typed invocations and explicit staged dispatch, including egress scope. | Preserve grammar/validators; replace parse-result conversion and application dispatch. |
| 7 | Extract shared teardown and recovery; migrate non-session commands. | Preserve writer-removal, restoration, retention, and batch reminder behavior before changing startup structure. |
| 8 | Extract session files, preparation, and agent execution; introduce `RunCommand` and `SessionRunner`. | Preserve the visible startup sequence, lock lifetimes, and signal scope. Remove the old `Session` facade. |
| 9 | Remove obsolete helpers/forwarders, finish adapter consumer migration, update contributor guidance, and run release-level verification. | Completion audit against all five workstreams and the contract table. |

If a milestone becomes hard to review, split by owner or consumer group. Keep the sequence serial where it shares public internal types; simultaneous config, parser, and lifecycle rewrites would make regression analysis difficult.

## Verification strategy

For each implementation milestone, run `dotnet build` and the complete unit suite from `cli/`, with warnings as errors. Run the AOT installer to a dedicated temporary output directory and point smoke tests to that exact executable using `PEN`; smoke tests must exercise the newly embedded resources and binary. The existing installer supports `cli/install.sh --out DIR`. Record commands and outcomes in each change description, including skips and platform limitations.

| Change area | Unit coverage to retain/use | Relevant smoke classes |
|---|---|---|
| Parsing and dispatch | `CliTests`, `ToolsTests`, `HidePatternTests`, `RecipeTests` | `CliDispatchTests`, `ConfigFileTests`, `AgentTests` where implied-agent behavior is touched |
| Config models/loading/editing | `ConfigTests`, `GitHubTests`, `SeedTests`, `InitCommandTests`, `ProjectTemplateTests`, `TrustGateTests` | `ConfigFileTests`, `HomeSeedTests`, `GitHubTokenTests`, `ProjectLayerTests` |
| Egress ownership/adapters | `AllowCommandTests`, `DenyCommandTests`, `AllowlistTests`, `TrustGateTests` | `AllowCommandTests`, `AllowlistMergeTests`, `ProxyNetworkTests` |
| Runtime and locks | `RuntimeDirTests`, `SessionLockTests`, `LockAndProcessTests`, `CleanStaleTests`, `InitHintTests` | `SessionLockTests`, `CleanTests`, `CleanStaleTests`, `LinkedWorktreeTests` |
| Session preparation/files | `MaskSessionTests`, `MaskPlannerTests`, `ModuleBaselineTests`, `ProtectedFilesTests`, `ToolsImageTests`, `PortTests`, `TrustGateTests` | `SessionTests`, `HiddenFileTests`, `ChangedHiddenFileTests`, `ProjectLayerTests`, `PortForwardingTests`, `DockerInDockerTests` |
| Teardown and recovery | `SessionEndTests`, `GitCheckTests`, `ConfigWorktreeQuarantineTests`, `ModuleBaselineTests`, `ProtectedFilesTests` | `SessionEndQuarantineTests`, `WorktreeConfigQuarantineTests`, `PenSignTests`, `ReadOnlyGitDirTests`, `BlockedListingTests` |
| Signals and process adapters | `SessionSignalsTests`, `DockerTests`, `LockAndProcessTests`, `OutputSanitizingTests` | `SessionSignalTests`, `ImageBuildInterruptTests`, `SessionTests` |

Select additional classes from actual touched behavior rather than treating this table as exhaustive. Run the full smoke suite after egress extraction/adapters, after the command/config migration, after lifecycle extraction, and before the final handoff. Before merging the completed work, verify macOS arm64 and Linux amd64, with rootful and rootless Docker on Linux, as CONTRIBUTING requires. Report unavailable platforms as unverified rather than implying local tests cover them.

Use focused failure tests for the new boundaries: two writer-removal attempts even if one fails; absence only after a successful query; no Git check before writer absence; no deletion of protected copies after incomplete restoration; no reminder after any failed recovered check; no reload before the proxy sees all ACL contents; and no new project tool domains entering a live session. Use real locks and subprocess tests for concurrency and signal contracts. Keep existing adversarial repository and masking scenarios unchanged through the structural migrations.

Adapter assertions may check literal argument/environment construction because that is their contract. Higher-level tests should assert decisions, ordering required for safety, retained data, output, and exit status rather than every internal helper invocation. Preserve the existing distinction between expected command errors and unexpected programming errors.

## Documentation and completion audit

Update `AGENTS.md` and CONTRIBUTING with each ownership migration, including the new `RunCommand` convention and typed configuration/invocation APIs. Add a concise architecture entry point that explains how to add a command, where configuration is validated versus approved, how a session starts/ends/recovers, and how to run targeted checks. Keep detailed attack/race explanations next to their implementation contracts and tests.

The work is complete when deny no longer calls allow; runtime/recovery no longer depend on a running-session class; the visible session coordinator documents and preserves its ordering; external operations are explicit at orchestration boundaries; parser success uses normal Boolean contracts; command invocations carry relevant data only; user/project configuration is distinct and immutable; temporary compatibility code is gone; AOT and the required tests pass; and verification records state the platforms, Docker modes, and skipped checks actually exercised.
