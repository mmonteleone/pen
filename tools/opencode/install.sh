#!/bin/bash
# Installs OpenCode at the version given (latest, or an exact release such as 1.19.0) with
# the base's helper for agents from npm (/opt/pen/lib/npm-agent.sh), into /opt/pen/opencode.
# Only the package's own install script runs: it puts the native binary for this platform
# in place of its bin stub, and /usr/local/bin/opencode is a symlink to it. Releases before
# 1.15.1 name the binary bin/opencode.
set -euo pipefail
export OPENCODE_DISABLE_AUTOUPDATE=1
exec /opt/pen/lib/npm-agent.sh opencode opencode-ai --allow-scripts \
  --native bin/opencode.exe --since 1.15.1 -- "$@"
