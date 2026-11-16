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

### Open egress

`pen allow '*'` allows any domain name, which gives up the destination limit: the main defence against data being sent somewhere unexpected. Anything the agent can read can then be uploaded to any host that accepts it. Use it only for work where that is acceptable, and prefer naming domains.

In open mode `deny` is best-effort: it blocks the names you list, but any other name for the same service (a CNAME, a mirror, a CDN domain) gets through.

Blocking private destinations is best-effort too. Squid keeps a resolved answer for about a minute, so a DNS rebind to a private address is caught only once that answer expires. A domain allowed by name still reaches private addresses, even in open mode. Raw IPs stay blocked either way.

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

Sessions see each other only when they're started through the same path. On macOS, a project opened through its `/System/Volumes/Data` firmlink path (or on Linux, through a bind mount) gets a different project key than through its usual path, so sessions started through the two paths don't share locks, protected-file baselines or submodule repo lists, and neither allows for what the other's agent could write. Always start Pen in a project through the same path.

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

The whole git dir is walked too, for repos where git doesn't keep any: a `.git` anywhere inside it, or a directory git would take for a git dir other than the repo's own, its submodule repos and their worktree entries. Git run from such a directory would use that repo's config and hooks, and it can get there without your choosing to: through a symlink committed on a branch, which arrives when you check the branch out, or one that leads nowhere until a later commit creates the directory it names. So nothing in `.git` is skipped, including the object stores (`objects`, `lfs/objects`, `annex/objects`) and `….pen-quarantine` directories. Pen refuses these repos at start and moves them aside at session end. When it moves a `.git` directory aside, it also walks what's inside before the check ends. The walk lists every directory once, so a git dir with very many Git LFS or git-annex objects makes the start and end of each session slower: a little over a second per 50,000 directories.

A symlink in the project that leads to a directory in `.git`, whatever its name (a `.git` symlink included), is refused at start and removed at session end rather than renamed, since under any name it would lead to the same place. This also applies to a symlink that leads nowhere yet but would lead into `.git` once a directory it names is created, including through another such symlink on the way (Pen puts that one's target in its place, as the kernel does, so `x/../../.git/foo` counts when `x` leads to a missing `a/b`). A symlink Pen can't follow, because its target isn't valid UTF-8 or a directory on the way can't be searched, is refused and removed the same way, with a message saying Pen can't tell where it leads. Pen compares directories by device and inode, so a path spelled differently (macOS's `/System/Volumes/Data/…`, say) still counts. The warning prints the `ln -s` command that recreates the symlink, or says to recreate it by hand when no command can name its target exactly. This is a second line of defence: the walk of `.git` finds what such a symlink leads to.

Branch and tag names can lay out a directory under `.git/refs` or `.git/logs` as a git dir (for example, remote branches named `refs/x` and `objects/y` beside `origin/HEAD`), and git does take it for one. Pen refuses these at start like any other, suggesting you rename those refs. At session end it moves the directory's `HEAD` aside and says that ref names made it.

Pen only checks what a symlink in the project leads to inside `.git`. One that leads to a directory outside both the project and `.git` that the sandbox can also write isn't checked. For example, under rootless Docker on Linux the project's volumes (`~/.local/share/docker/volumes/<key>_*/_data`) are owned by you, so the agent could put a repo in one and a symlink to it in the project. Don't run git through such a symlink, and check where the project's symlinks lead (`find . -type l -exec ls -l {} +`) before running git in a directory you reached through one.

The agent's branches can also carry a whole repo layout in tracked files, such as `foo/HEAD`, `foo/objects/…`, `foo/refs/…` and `foo/config`. Nothing of it is in `.git`, and it arrives in the project only when you check the branch out; git run in `foo` after that would take it for a bare repo and use its config, which can run commands. Setting `safe.bareRepository = explicit` in your global git config (see [Host precautions](#host-precautions)) stops git from using it.

Submodule repos in `.git/modules` (or a worktree's own `modules`) that weren't there at session start are moved aside at session end, since their config and hooks were writable from the sandbox. When other sessions are open in the project, a session takes as its own only the submodule repos that every one of them had at its own start, so one another session's agent could have made is moved aside at its end too, and its config is never put back from a saved copy. Pen refuses to start while a session is open that has no record of its submodule repos (one started by an older version of Pen, before the directory was a git repo, or in a repo with another common git dir), naming it: exit it first.

Pen also quarantines `config.worktree` files in the git dir, worktree entries and submodule repos that weren't there at the start, or first appeared while another session could write them. These can contain commands. This includes the file `git worktree add` copies when `extensions.worktreeConfig` is on; the warning explains how to restore it. `pen sign` has no session baseline, so it leaves these files alone without warning.

### Paused git operations

At session end and during `pen sign`, Pen warns about paused rebases, `git am`, cherry-picks and reverts in the git dir, worktree entries and submodule repos. They may be yours, so they stay in place. The sandbox could have written them too. Inspect them before continuing or skipping a step on the host.

Warnings list rebase `exec` lines, custom merge strategies (which git runs as programs), and `git apply` options. A short warning doesn't mean an operation is safe: a pick can bring in code, and a reword opens text in your editor.

Some state files can't wait. `git-prompt.sh` can put their contents into a prompt that the shell expands (bash's `PROMPT_COMMAND` mode or zsh's `PROMPT_SUBST`). Pen quarantines `msgnum`, `end`, `next`, `last` and `head-name` containing text git wouldn't write, and `pen sign` stops if it finds them. Ordinary rebase state stays. Tests cover `git-prompt.sh` in bash and zsh; other prompts, including starship and oh-my-zsh, may read other files and haven't been tested.

### When a check or restore fails

If a protected file can't be restored, Pen keeps its saved copies, refuses new sessions and prints a recovery command. **Run that command as printed, on one line.** It may fix directory permissions and run `pen true` to retry. Running `chmod` separately could let a git-aware prompt execute planted code before the retry.

If the git check can't finish, Pen ends the session, prints **"git wasn't checked: don't run git here"** with the reason, and runs no host git itself. Inspect `.git` by hand before using git. This warning isn't remembered: a later session accepts the submodule repos it finds as its baseline, other than any that a session still open didn't have at its start, unless saved files are still waiting to be restored.

<details>
<summary>Unreadable directories and filenames that aren't valid UTF-8</summary>

A directory or symlink with a name that isn't valid UTF-8 can't be checked for nested repos. Pen refuses it at start; at session end it reports that git wasn't checked instead of moving it. A branch whose name isn't valid UTF-8 before a `/` makes such a directory under `.git/refs/heads` (and `.git/logs/refs/heads`), so on Linux it refuses the start until you rename the branch; macOS doesn't allow these names. Plain files with such names are allowed in the project, and elsewhere in `.git`. Under `.git/modules` and `.git/worktrees`, any such name is refused, including plain files.

A directory that can be entered but not listed (`chmod 311`) is refused at start. At session end Pen adds `u+rx`, scans it and reports the change. A project directory that can't be entered at all (`chmod 000`, or a root-owned database directory) is allowed because host git can't enter it either. Pen can't see a repo hidden there. Inspect it before fixing its mode and using git.

Directories inside `.git` are treated the same way, except under `.git/modules`, which is stricter: unreadable directories there are refused at start and quarantined at end. An unreadable `.git` is refused at start and reported as "wasn't checked" at end.

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
