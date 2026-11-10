# Security

If you find a way around Pen's sandbox, please report it privately through the repository's **Security → Report a vulnerability** page on GitHub. Please don't open a public issue or pull request for it.

## What to include

Tell us what you ran, what you expected Pen to stop, and what happened. A small repo or script that reproduces it helps most. Include your OS, Docker engine and version, and whether Docker is rootful or rootless.

This is a personal project; replies may take a few days. There are no releases yet, so fixes land on `main`.

## What to report

Ways an agent in a `pen` session, or a repo opened with `pen`, can:

- reach host files outside the project and the mounts `pen` documents;
- reach the network other than through the proxy and the allowlist;
- make host git run its code in a way the session-end check misses or doesn't warn about;
- get `.pen/` settings (domains, tools, Docker, a Dockerfile, another owner's token) used without your approval;
- read credentials it wasn't given, such as another owner's GitHub token or your signing key.

Bugs in `pen`'s own host-side handling, such as unsafe file operations, races or prompts that show something other than what gets approved, count too.

## Known limits and upstream bugs

- The [documented limitations](docs/limitations.md) include uploads to allowed domains, host git during a session, the case-insensitive `.git/CONFIG` gap and Docker-in-Docker's privileged daemon. Please report anything that makes these worse than documented.
- Container escapes through bugs in Docker, the kernel or the container runtime. Report those upstream. Please report Pen settings that make an escape easier here too.
- What an agent does with the permissions you gave it, such as pushing with a write-capable token.
