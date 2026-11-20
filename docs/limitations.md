# Limitations

Pen limits an agent's access to your machine and network. It doesn't make the agent's work safe to run, prevent it from destroying the mounted project, or protect against a deliberate container escape. Use a VM if you need a separate kernel.

## Before you start

- **Keep backups.** The checkout is mounted read-write. An agent can delete files, uncommitted work and unpushed commits.
- **Keep host Git out of the repo until every session ends**, including linked worktrees, IDE Git integrations and Git-aware shell prompts.
- **Give the sandbox only the credentials it needs.** Tokens and logins retain their usual permissions. Allowed domains can receive uploads.
- **Use file hiding with care.** It has filesystem gaps and doesn't remove secrets from Git history.
- **Enable Docker-in-Docker only when needed.** Its privileged daemon can undo Git and file-hiding protections.
- **Review changes before using host tools.** Build scripts, agent settings and editor config can run commands with your host credentials. Include ignored files in that review.

See [usage](usage.md) for configuration. The boundaries below apply even when a session finishes without warnings.

- [Git during and after a session](#while-a-session-runs)
- [Project files host tools act on](#project-files-host-tools-act-on)
- [Network and credentials](#network-and-credentials)
- [Hidden and replaced files](#hidden-and-replaced-files)
- [Docker-in-Docker](#docker-in-docker-and-the-git-protections)
- [Terminal output](#terminal-output)

## While a session runs

Treat `.git` as untrusted until every session in the repo has ended. An agent can create files that redirect host Git to config or hooks it controls. Even `git status`, an IDE scan or a shell prompt can then run code on the host.

Use a consistent path when starting Pen. Different paths to the same project, such as a macOS firmlink or Linux bind mount, can produce separate session locks and protection records. See [concurrent sessions and path aliases](git-safety.md#while-a-session-runs).

## At session start

Pen warns about Git config mounts on case-insensitive filesystems and hooks stored in writable project directories, such as `.husky/`. Those hooks need review before host use. Submodules can have writable hooks without a warning. See [startup checks](git-safety.md#at-session-start).

## At session end

Pen stops the agent and its Docker-in-Docker daemon before restoring protected Git files and checking for known hazards. It moves suspicious files aside as `….pen-quarantine` and warns about paused Git operations. Read the warnings before using host Git; a completed check doesn't replace reviewing the agent's work.

If Pen was killed, run `pen true` in the checkout to finish recovery before using host Git. If it prints a recovery command, follow that advice and keep the session files: they may hold the only good copies of protected files. A warning that **"git wasn't checked: don't run git here"** requires manual inspection; starting another session doesn't establish that the repo is safe.

See [Git precautions and recovery](git-safety.md) for what Pen checks, what it leaves in place, and how to handle failures.

## Host precautions

Set `git config --global safe.bareRepository explicit` to stop Git from discovering bare repos implicitly. In VS Code, disable automatic repository detection and close the repo's Git integration during sessions. These settings reduce accidental exposure; they don't make host Git safe while agents can write to the repo. See [host setup details](git-safety.md#host-precautions).

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

### Matching by name

The proxy matches the hostname in an HTTPS `CONNECT` or plain-HTTP request. It doesn't intercept TLS or inspect the traffic inside a connection. An agent can request an allowed name, then send a different TLS SNI or HTTP `Host`; a shared CDN or load balancer may still answer. Existing connections can also remain usable. [`deny`](usage.md#denying-domains) blocks requests for a name, not every way of reaching the service.

### Open egress

`pen allow '*'` allows any domain name, which gives up the destination limit: the main defence against data being sent somewhere unexpected. Anything the agent can read can then be uploaded to any host that accepts it. Use it only for work where that is acceptable, and prefer naming domains.

In open mode `deny` is weaker still: it blocks the names you list, but any other name for the same service (a CNAME, a mirror, a CDN domain) gets through, besides the [ways past a name](#matching-by-name) above.

Blocking private destinations is best-effort too: it goes by the address Squid resolves for the name, which it keeps for about a minute. Testing with DNS answers that change between lookups (rebinding to a private address) found no way past it, but that isn't a guarantee. A domain allowed by name still reaches private addresses, even in open mode. Raw IPs stay blocked either way.

The session allowlist doesn't apply when building a [`.pen/Dockerfile`](usage.md#project-settings). Its build steps reach the network directly, which is part of what you approve.

Use [read-only GitHub tokens](usage.md#github), scoped to the repos you need. Pen passes tokens through without checking their permissions. Logins or keys you add inside the sandbox carry their usual permissions too.

<details>
<summary>Older Docker Engines can leak DNS queries</summary>

External names shouldn't resolve inside the sandbox; the proxy resolves them. Older Docker Engines forwarded DNS from internal networks when the host resolver used a loopback address such as systemd-resolved's `127.0.0.53`. That lets DNS queries carry data out (CVE-2024-29018).

The fix is in 23.0.11 and later 23.0 releases, 25.0.5 and later 25.0 releases, and 26.0 onward, but no 24.x release. Docker Desktop was never affected. The smoke suite checks that external names don't resolve.

</details>

## Hidden and replaced files

[`hide` and `[replace]`](usage.md#hiding-files-from-the-agent) change only what the sandbox sees, with read-only mounts. They're best-effort, and these gaps remain:

- **Case-insensitive filesystems.** Where the filesystem ignores case (macOS's default), another spelling of a hidden file's name (`.ENV` for `.env`) or of a replaced file's target can reach the host's real file past its single-file mount. Pen probes `.pen/` and warns when the filesystem isn't known to be case-sensitive. Hiding the directory resists other spellings. On macOS, any APFS volume, even a case-sensitive one, also reaches a file under another Unicode normalization spelling of its name (precomposed NFC or decomposed NFD), and Pen warns when a hidden or replaced file's name isn't ASCII.
- **`--docker`.** Hidden and replaced files aren't enforced: the agent controls a privileged daemon that can undo the mounts. Pen says so at session start.
- **Tracked files.** A hidden file that git tracks is still in git history, which the agent can read. `git commit -a` in the sandbox would commit the empty or replacement content in its place.
- **Matches are taken at session start.** A file created later isn't hidden, and a replacement is a copy as of the start. More than 1,000 hidden files, hidden directories and replacements in total make the session refuse to start, so an agent that creates enough matching files can stop later sessions from starting.
- **Between Pen's last check and the container starting.** Pen re-checks every hidden and replaced path just before it writes the session's files, and refuses to start if one changed. A change after that, by you or by a concurrent session's agent, can still make Docker create an empty file or directory in the project where a path was removed, or leave a hidden file visible if a parent directory was renamed. Keep the project tree still while a session starts.
- **Extra hard links.** Pen warns when a hidden file has another name, which the agent can read. It doesn't warn about a hard link to a file inside a hidden directory.
- **Anchored patterns.** Renaming a parent directory between sessions dodges a pattern anchored to it (`/config/prod.yaml`). Prefer name patterns (`prod.yaml`) or hide the directory.
- **Symlinked directories.** An anchored literal pattern whose path goes through a symlinked directory hides nothing. Pen warns and doesn't follow the symlink: hide the path it leads to.

## Docker-in-Docker and the git protections

With `--docker`, the agent controls a privileged daemon, and `hide` and `[replace]` aren't enforced (see [Hidden and replaced files](#hidden-and-replaced-files)). An inner container using `--privileged --pid=host` and `nsenter` can undo Pen's read-only mounts and write `.git/config`, `.git/hooks` and `.pen/` on the host.

The session-end check restores the protected single files. **It doesn't check hooks.** A hook planted this way can run on the host without warning. Read `.git/hooks` and submodule `hooks` directories under `.git/modules` before using host git.

Project settings that expand access still require approval, and approvals live outside the project. Whether a privileged inner container can reach those records through the Docker VM hasn't been tested.

## Terminal output

Pen strips control characters from its own messages, but the agent's output reaches your terminal directly. Pen doesn't reset the terminal afterwards. An agent can leave conceal mode or the alternate screen on, hiding the warnings printed next.
