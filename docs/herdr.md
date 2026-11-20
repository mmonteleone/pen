# Running Pen in herdr

[herdr](https://herdr.dev) runs agents in terminal panes with a state sidebar. These notes come from tests with herdr 0.9.5 on macOS with OrbStack.

## Set up herdr

Remove the sidebar's ahead/behind count in `~/.config/herdr/config.toml`:

```toml
[ui.sidebar.spaces]
rows = [["state_icon", "workspace"], ["branch"]]
```

The count runs host `git rev-list` when refs change, including after agent commits, while [`.git` is untrusted](limitations.md#while-a-session-runs). With these rows, no Git ran in our tests. The branch name stays, because herdr reads it directly from `.git`.

## Start an agent

```sh
HERDR_AGENT=claude pen claude    # or codex, opencode, pi
```

The hint lets herdr recognize the agent behind the foreground `pen` process. It may still show the agent as idle; detection of working, blocked and done states hasn't been checked. Inspect it with `herdr agent get <pane>` and `herdr agent explain <pane>`.

`herdr agent start --kind claude` types `claude` into the shell, so the agent runs on the host, outside Pen. To route it through Pen, define a host shell function:

```sh
claude() { HERDR_AGENT=claude command pen claude "$@"; }
```

Without the hint, herdr times out while the session keeps running.

## Exit the agent before closing its pane

**Exit the agent before closing its pane or stopping herdr.** herdr sends a hangup, then kills `pen` about a second later. In our tests, Pen removed the agent container and checked Git, but session files and possibly the proxy remained. The warnings disappear with the pane, and `pen ps` shows nothing.

If this happens, run `pen true` in the project before any host Git to finish cleanup. Look for `….pen-quarantine` files in the project and the Git directory, because Pen doesn't repeat quarantine notices.

## Other notes

- Multiple panes work, including one per worktree. Create and remove worktrees between sessions, because herdr's worktree actions run host Git.
- Detaching (`ctrl+b q`) or closing the attached terminal leaves sessions running. Reattaching finds them again.
- Splitting a pane that's running Pen opens the new pane in `~/.local/state/pen`. Split from a shell or pass `--cwd`.
- A stopped agent can leave bracketed-paste mode on and garble what herdr types next. Run `reset` in that pane.

## Keep herdr's socket out of the sandbox

herdr's API can run host commands. Pen doesn't pass `HERDR_SOCKET_PATH`, `HERDR_BIN_PATH` or `SSH_AUTH_SOCK` into the sandbox, and our test session had no `HERDR_*` variables or `~/.config/herdr`. Don't add these variables to `[env]` or put herdr's config directory inside the mounted project. herdr's agent integrations aren't needed for state tracking, so leave them out of the [home seed](usage.md#agent-config-the-home-seed).
