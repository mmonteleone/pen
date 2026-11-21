# Git precautions and recovery

Pen lets an agent change Git history and repository data while keeping config and hooks read-only. Some Git files can't be protected in advance, so Pen also checks the repository when a session ends. These checks cover known hazards, not every way Git or another host tool could run code.

For the normal commit, review and signing workflow, see [Git in the sandbox](usage.md#git-in-the-sandbox). For the wider sandbox boundaries, see [limitations](limitations.md).

- [While a session runs](#while-a-session-runs)
- [At session start](#at-session-start)
- [At session end](#at-session-end)
- [When a check or restore fails](#when-a-check-or-restore-fails)
- [Host precautions](#host-precautions)

## While a session runs

`.git` is untrusted until every session in the repo ends. Keep host Git out of the main checkout and all linked worktrees. IDE integrations, Git-aware shell prompts and [herdr's ahead/behind count](herdr.md#set-up-herdr) run git too.

Some files can't be protected in advance. For example, a `.git/commondir` file redirects git to another directory's config and hooks. Mounting an empty one would break git, so an agent can create one pointing into the project. The next host `git status` can then run planted code. The session-end check moves it aside; the [host settings below](#host-precautions) don't prevent it.

`pen sign` refuses while a session runs in its own directory, but allows one in another worktree. Its git calls disable hooks and `core.fsmonitor`; the agent can still change the commits and other data it reads. Wait for all sessions to end before signing.

Sessions see each other only when they're started through the same path. On macOS, a project opened through its `/System/Volumes/Data` firmlink path (or on Linux, through a bind mount) gets a different project key than through its usual path, so sessions started through the two paths don't share locks, protected-file baselines or submodule repo lists, and neither allows for what the other's agent could write. Always start Pen in a project through the same path.

## At session start

Pen warns about two gaps without blocking the session:

- **Case-insensitive filesystems** (macOS's default). Writing `.git/CONFIG` can change `.git/config` despite its read-only mount. Pen probes the Git directory and warns if names are case-insensitive or the check fails. It restores the file at session end, but host Git would read the changed version until then. Directory mounts (`.git/hooks`, `.pen/`) resisted alternate spellings in integration tests on OrbStack; this hasn't been tested on Docker Desktop.
- **`core.hooksPath` inside the repo.** Hooks outside `.git/hooks`, such as Husky's `.husky`, are ordinary files the agent can change. Pen follows system, global and repo settings and includes, and warns if the path is writable from the sandbox or can't be resolved. Check those hooks before using host Git. Mounting them read-only wouldn't protect the project files they might call.

The hooks warning covers the main repo only. Submodules can have the same problem without a warning. Pen's own git calls disable hooks.

## At session end

Read the warnings before using host Git. Pen restores protected files, moves known hazards aside as `….pen-quarantine`, and reports paused git operations for you to inspect.

### Protected files and quarantine

Pen saves the individually mounted files at session start: `config`, `config.worktree`, included config files, `commondir` and worktree `.git` files, including submodule equivalents. Each must be a regular file no larger than 1 MB. At session end, changed versions are quarantined and the saved versions restored. This includes host edits such as `git config`, so make those between sessions.

Pen refuses Git config includes it can't protect, and names the config file and path involved. This applies to includes of any form or condition that:

- name a writable directory, which the agent could replace with a file;
- pass through a symlink the agent can repoint, even if reached through a symlink outside the project;
- use `..` after a directory the agent could replace with a symlink, redirecting Git to an unprotected file.

A `..` out of the including file's own directory or an ancestor, such as `../.gitconfig` in `.git/config`, is allowed.

These comparisons cover single files only. The contents of `hooks` and `.pen/` rely on directory mounts and aren't compared. A linked worktree also mounts the main checkout's `HEAD` and `index` read-only; they aren't saved or compared because they can change legitimately.

### Nested repositories and symlinks

Pen scans the whole Git directory for unexpected repositories: a nested `.git`, or a directory Git would treat as a Git directory outside the repo's own layout, its submodules and their worktree entries. Running Git there could load an agent's config and hooks. A committed symlink can lead you there after a checkout, even if its target didn't exist before.

Nothing in `.git` is skipped, including `objects`, `lfs/objects`, `annex/objects` and quarantine directories. Unexpected repos are refused at startup and moved aside at session end; a quarantined `.git` is scanned too. Every directory is listed once, so large LFS or git-annex stores slow startup and teardown. Tests measured a little over a second per 50,000 directories.

Project symlinks that lead to directories inside `.git` are refused at startup and **removed**, rather than renamed, at session end. Renaming a symlink wouldn't change where it leads. Pen also rejects links it can't resolve safely, including inaccessible paths and targets that aren't valid UTF-8. It prints an `ln -s` command to recreate a removed link when the target can be represented exactly.

<details>
<summary>Symlink resolution and ref names</summary>

The symlink check includes targets that don't exist yet but would lead into `.git` once directories are created. It expands intermediate symlinks as the kernel does: `x/../../.git/foo` counts when `x` leads to a missing `a/b`. Directories are compared by device and inode, so alternate paths such as macOS's `/System/Volumes/Data/…` still count.

Branch and tag names can also create a Git directory layout under `.git/refs` or `.git/logs`: for example, remote branches named `refs/x` and `objects/y` beside `origin/HEAD`. Pen refuses to start and suggests renaming those refs. At session end, it moves that directory's `HEAD` aside and explains the cause.

</details>

Two gaps remain:

- **Symlinks outside the project and `.git`.** Pen doesn't check their targets for repos. On rootless Linux, for example, the agent can put a repo in a project volume under `~/.local/share/docker/volumes/<key>_*/_data` and link to it. Check where a symlink leads before running Git through it. `find . -type l -exec ls -l {} +` lists project symlinks.
- **Repo layouts in tracked files.** A branch can contain `foo/HEAD`, `foo/objects/`, `foo/refs/` and `foo/config`. Those files arrive only when checked out, and Git run in `foo` may use them as a bare repo. Set `safe.bareRepository = explicit` as described in [host precautions](#host-precautions).

### New submodule repositories and worktree config

New submodule repos under `.git/modules` or a worktree's `modules` are moved aside at session end: their config and hooks were writable. With concurrent sessions, Pen trusts only submodule repos present at the start of **every** open session. It won't restore config from a baseline another agent could already have changed.

Pen refuses to start if an open session has no submodule baseline, and names the session to exit. This can happen with sessions started by an older Pen, before the directory became a repo, or with a different common Git directory.

New `config.worktree` files are quarantined too, including files that first appeared while another session could write them. They can contain commands, even when created by `git worktree add` with `extensions.worktreeConfig` enabled. The warning explains how to restore them. `pen sign` has no session baseline and leaves these files alone without warning.

### Paused git operations

At session end and during `pen sign`, Pen warns about paused rebases, `git am`, cherry-picks and reverts in the Git directory, worktree entries and submodule repos. They may be yours, so they stay in place. The sandbox could have written them too. Inspect them before continuing or skipping a step on the host.

Warnings list rebase `exec` lines, custom merge strategies (which git runs as programs), and `git apply` options. A short warning doesn't mean an operation is safe: a pick can bring in code, and a reword opens text in your editor.

Some state files can't wait. `git-prompt.sh` can put their contents into a prompt that the shell expands (bash's `PROMPT_COMMAND` mode or zsh's `PROMPT_SUBST`). Pen quarantines `msgnum`, `end`, `next`, `last` and `head-name` containing text git wouldn't write, and `pen sign` stops if it finds them. Ordinary rebase state stays. Tests cover `git-prompt.sh` in bash and zsh; other prompts, including starship and oh-my-zsh, may read other files and haven't been tested.

### When a check or restore fails

If a protected file can't be restored, Pen keeps its saved copies, refuses new sessions and prints a recovery command. **Run that command as printed, on one line.** It may fix directory permissions and run `pen true` to retry. Running `chmod` separately could let a Git-aware prompt execute planted code before the retry.

If the Git check can't finish, Pen ends the session, prints **"git wasn't checked: don't run git here"** with the reason, and runs no host Git itself. Inspect `.git` by hand before using git. This warning isn't remembered: a later session accepts the submodule repos it finds as its baseline, other than any that a session still open didn't have at its start, unless saved files are still waiting to be restored.

<details>
<summary>Unreadable directories and filenames that aren't valid UTF-8</summary>

A directory or symlink with a name that isn't valid UTF-8 can't be checked for nested repos. Pen refuses it at start; at session end it reports that git wasn't checked instead of moving it. A branch whose name isn't valid UTF-8 before a `/` makes such a directory under `.git/refs/heads` (and `.git/logs/refs/heads`), so on Linux it refuses the start until you rename the branch; macOS doesn't allow these names. Plain files with such names are allowed in the project, and elsewhere in `.git`. Under `.git/modules` and `.git/worktrees`, any such name is refused, including plain files.

A directory that can be entered but not listed (`chmod 311`) is refused at start. At session end Pen adds `u+rx`, scans it and reports the change. A project directory that can't be entered at all (`chmod 000`, or a root-owned database directory) is allowed because host Git can't enter it either. Pen can't see a repo hidden there. Inspect it before fixing its mode and using git.

Directories inside `.git` are treated the same way, except under `.git/modules`, which is stricter: unreadable directories there are refused at start and quarantined at end. An unreadable `.git` is refused at start and reported as "wasn't checked" at end.

</details>

## Host precautions

These settings reduce accidental exposure to nested repos. They don't make host git safe during a session.

```sh
git config --global safe.bareRepository explicit
```

This stops git from discovering a bare repo just because a directory looks like one. It doesn't stop an ordinary nested `.git` or a planted `.git/commondir`. Wait until the session ends before entering a new subdirectory containing a repo; Pen moves nested repos aside at session end.

In VS Code, set `git.autoRepositoryDetection` to `false` or `openEditors` to stop subfolder scans. `openEditors` still discovers repos around files you open. Both settings still allow git in the repo you've opened, so close that repo during sessions too.
