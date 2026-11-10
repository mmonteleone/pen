#!/bin/bash
# Installs Claude Code at the version given (latest, or an exact release such as 2.1.300)
# with the base's helper for agents from npm (/opt/pen/lib/npm-agent.sh), into
# /opt/pen/claude. Only the package's own install script runs: it puts the native binary
# for this platform in place of its bin stub, and /usr/local/bin/claude is a symlink to it.
# Releases before 2.1.113 have a JavaScript CLI (cli.js) instead of that binary.
set -euo pipefail
export DISABLE_AUTOUPDATER=1 CLAUDE_CODE_DISABLE_AUTOUPDATE=1
exec /opt/pen/lib/npm-agent.sh claude @anthropic-ai/claude-code --allow-scripts \
  --native bin/claude.exe --since 2.1.113 --version-word first -- "$@"
