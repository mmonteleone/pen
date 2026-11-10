# Limitations

Pen tries to limit damage from mistakes and prompt injection. It doesn't protect against a deliberate container escape. Its git checks cover known ways to make host git run code; there may be others.

The everyday precautions:

- **Keep host git out of the repo while sessions run**, including every linked worktree, IDE git integration and git-aware shell prompt. Wait before using `pen sign` too.
- **Keep backups.** The project is mounted read-write. An agent can delete files, uncommitted work and unpushed commits.
- **Review before running things on the host.** Build scripts, `.envrc`, agent settings and editor config can run commands with your host credentials. Check ignored files too.
- **An allowed domain can carry data out.** A prompt injection can supply an attacker's upload credentials. A write-capable GitHub token also allows writes; Pen doesn't reduce its permissions.
- **Enable Docker-in-Docker only when needed.** It lets the agent undo the git protections and plant hooks that Pen won't detect.

The details below explain where these limits come from and what the warnings mean. For setup, see the [README](../README.md) and [usage reference](usage.md).

## Project files host tools act on

The agent can edit project files that your host tools later act on. Pen doesn't check or protect these. Review build scripts, `package.json` scripts and `.envrc`, along with:

- **Claude settings:** `.claude/settings.json` and `.claude/settings.local.json` can set hooks, `env` and helpers such as `apiKeyHelper`. Host Claude Code uses them once you've trusted the folder or a parent. `claude -p` and the SDK use them without asking, even in an untrusted folder.
- **MCP servers:** `.mcp.json` names commands to run. Claude Code asks before connecting, but a trusted project's settings can approve them and skip the question. `claude -p` and the SDK connect without asking.
- **Editor config:** `.vscode/tasks.json` runs commands, including on folder open if you've allowed automatic tasks (`task.allowAutomaticTasks`). `.vscode/settings.json` and `launch.json` can name programs too.
- **Git LFS:** `.lfsconfig` can redirect uploads and downloads to an agent-chosen server, unless your git config overrides it. A host push can upload LFS objects there and ask your credential helper for that server's credentials. An `ssh://` URL uses your SSH client.
- **Submodules:** `.gitmodules` controls URLs and paths used by host `git submodule` commands. Review its changes before running them.

Codex's `.codex/`, OpenCode's `opencode.json` and `.opencode/`, and other tools' project settings deserve the same review. Include ignored files such as `.claude/settings.local.json`: `git diff` won't show them.

<details>
<summary>Settings Git LFS reads from .lfsconfig</summary>

Unless overridden by git config: `lfs.url`, `lfs.pushurl`, `remote.<name>.lfsurl`, `lfs.<url>.access`, `lfs.gitprotocol`, `lfs.fetchinclude`, `lfs.fetchexclude`, `lfs.allowincompletepush`, `lfs.skipdownloaderrors` and `lfs.locksverify`. These don't name programs to run; the risk is redirecting transfers and connections.

</details>

## Network and credentials

The allowlist limits destinations, not what the agent sends. AI providers, GitHub and package registries accept uploads with credentials that an attacker can supply. Some toolchains add broad destinations: Go needs `storage.googleapis.com`, which allows uploads through signed URLs. Sites accepting anonymous writes need no credentials at all.

The session allowlist doesn't apply when building a [`.pen/Dockerfile`](usage.md#project-settings). Its build steps reach the network directly, which is part of what you approve.

Use [read-only GitHub tokens](usage.md#github), scoped to the repos you need. Pen passes tokens through without checking their permissions. Logins or keys you add inside the sandbox carry their usual permissions too.

<details>
<summary>Older Docker Engines can leak DNS queries</summary>

External names shouldn't resolve inside the sandbox; the proxy resolves them. Older Docker Engines forwarded DNS from internal networks when the host resolver used a loopback address such as systemd-resolved's `127.0.0.53`. That lets DNS queries carry data out (CVE-2024-29018).

The fix is in 23.0.11 and later 23.0 releases, 25.0.5 and later 25.0 releases, and 26.0 onward, but no 24.x release. Docker Desktop was never affected. The smoke suite checks that external names don't resolve.

</details>

## While a session runs

`.git` is untrusted until every session in the repo ends. Keep host git out of the main checkout and all linked worktrees. IDE integrations, git-aware shell prompts and [herdr's ahead/behind count](usage.md#running-in-herdr) run git too.

Some files can't be protected in advance. For example, a `.git/commondir` file redirects git to another directory's config and hooks. Mounting an empty one would break git, so an agent can create one pointing into the project. The next host `git status` can then run planted code. The session-end check moves it aside; the [host settings below](#host-precautions) don't prevent it.

`pen sign` refuses while a session runs in its own directory, but allows one in another worktree. Its git calls disable hooks and `core.fsmonitor`; the agent can still change the commits and other data it reads. Wait for all sessions to end before signing.

## At session start

Pen warns about two gaps without blocking the session:

- **Case-insensitive filesystems** (macOS's default). Writing `.git/CONFIG` can change `.git/config` despite its read-only mount. Pen probes the git directory and warns if names are case-insensitive or the check fails. It restores the file at session end, but host git would read the changed version until then. Directory mounts (`.git/hooks`, `.pen/`) resisted alternate spellings in smoke tests on OrbStack; this hasn't been tested on Docker Desktop.
- **`core.hooksPath` inside the repo.** Hooks outside `.git/hooks`, such as Husky's `.husky`, are ordinary files the agent can change. Pen follows system, global and repo settings and includes, and warns if the path is writable from the sandbox or can't be resolved. Check those hooks before using host git. Mounting them read-only wouldn't protect the project files they might call.

The hooks warning covers the main repo only. Submodules can have the same problem without a warning. Pen's own git calls disable hooks.

## At session end

Read the warnings before using host git. Pen restores protected files, moves known hazards aside as `….pen-quarantine`, and reports paused git operations for you to inspect.

### Protected files and quarantine

Pen saves the individually mounted files at session start: `config`, `config.worktree`, included config files, `commondir` and worktree `.git` files, including submodule equivalents. Each must be a regular file no larger than 1 MB. At session end, changed versions are quarantined and the saved versions restored. This includes host edits such as `git config`, so make those between sessions.

These comparisons cover single files only. The contents of `hooks` and `.pen/` rely on directory mounts and aren't compared. A linked worktree also mounts the main checkout's `HEAD` and `index` read-only; they aren't saved or compared because they can change legitimately.

Pen also quarantines `config.worktree` files in the git dir, worktree entries and submodule repos that weren't there at the start, or first appeared while another session could write them. These can contain commands. This includes the file `git worktree add` copies when `extensions.worktreeConfig` is on; the warning explains how to restore it. `pen sign` has no session baseline, so it leaves these files alone without warning.

### Paused git operations

At session end and during `pen sign`, Pen warns about paused rebases, `git am`, cherry-picks and reverts in the git dir, worktree entries and submodule repos. They may be yours, so they stay in place. The sandbox could have written them too. Inspect them before continuing or skipping a step on the host.

Warnings list rebase `exec` lines, custom merge strategies (which git runs as programs), and `git apply` options. A short warning doesn't mean an operation is safe: a pick can bring in code, and a reword opens text in your editor.

Some state files can't wait. `git-prompt.sh` can put their contents into a prompt that the shell expands (bash's `PROMPT_COMMAND` mode or zsh's `PROMPT_SUBST`). Pen quarantines `msgnum`, `end`, `next`, `last` and `head-name` containing text git wouldn't write, and `pen sign` stops if it finds them. Ordinary rebase state stays. Tests cover `git-prompt.sh` in bash and zsh; other prompts, including starship and oh-my-zsh, may read other files and haven't been tested.

### When a check or restore fails

If a protected file can't be restored, Pen keeps its saved copies, refuses new sessions and prints a recovery command. **Run that command as printed, on one line.** It may fix directory permissions and run `pen true` to retry. Running `chmod` separately could let a git-aware prompt execute planted code before the retry.

If the git check can't finish, Pen ends the session, prints **"git wasn't checked: don't run git here"** with the reason, and runs no host git itself. Inspect `.git` by hand before using git. This warning isn't remembered: a later session accepts the submodule repos it finds as its baseline, unless saved files are still waiting to be restored.

<details>
<summary>Unreadable directories and filenames that aren't valid UTF-8</summary>

A directory or symlink with a name that isn't valid UTF-8 can't be checked for nested repos. Pen refuses it at start; at session end it reports that git wasn't checked instead of moving it. Plain files with such names are allowed in the project. Under `.git/modules` and `.git/worktrees`, any such name is refused, including plain files.

A directory that can be entered but not listed (`chmod 311`) is refused at start. At session end Pen adds `u+rx`, scans it and reports the change. A project directory that can't be entered at all (`chmod 000`, or a root-owned database directory) is allowed because host git can't enter it either. Pen can't see a repo hidden there. Inspect it before fixing its mode and using git.

The git dir is stricter: unreadable directories under `.git/modules` are refused at start and quarantined at end. An unreadable `.git` is refused at start and reported as "wasn't checked" at end.

</details>

## Docker-in-Docker and the git protections

With `--docker`, the agent controls a privileged daemon. An inner container using `--privileged --pid=host` and `nsenter` can undo Pen's read-only mounts and write `.git/config`, `.git/hooks` and `.pen/` on the host.

The session-end check restores the protected single files. **It doesn't check hooks.** A hook planted this way can run on the host without warning. Read `.git/hooks` and submodule `hooks` directories under `.git/modules` before using host git.

Changed `.pen/` settings still went through approval in tests with `allow` and `docker`; `github_owner` and Dockerfiles also require approval. Ports don't. Approvals live outside the project, but don't rely on that here: whether a privileged inner container can reach them through the Docker VM hasn't been tested.

## Terminal output

Pen strips control characters from its own messages, but the agent's output reaches your terminal directly. Pen doesn't reset the terminal afterwards. An agent can leave conceal mode or the alternate screen on, hiding the warnings printed next.

## Host precautions

These settings reduce accidental exposure to nested repos. They don't make host git safe during a session.

```sh
git config --global safe.bareRepository explicit
```

This stops git from discovering a bare repo just because a directory looks like one. It doesn't stop an ordinary nested `.git` or a planted `.git/commondir`. Wait until the session ends before entering a new subdirectory containing a repo; Pen moves nested repos aside at session end.

In VS Code, set `git.autoRepositoryDetection` to `false` or `openEditors` to stop subfolder scans. `openEditors` still discovers repos around files you open. Both settings still allow git in the repo you've opened, so close that repo during sessions too.
