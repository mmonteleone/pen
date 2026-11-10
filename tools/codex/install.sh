#!/bin/bash
# Installs Codex at the version given (latest, or an exact release such as 0.167.0) with
# the base's helper for agents from npm (/opt/pen/lib/npm-agent.sh), into /opt/pen/codex.
# No install scripts run: the native binary comes in this platform's optional package.
# /usr/local/bin/codex runs the package's launcher on Pen's Node. Codex's update check is
# off in /etc/codex/managed_config.toml, since only a rebuilt image upgrades it.
set -euo pipefail

install -d -m 0755 /etc/codex
cat >/etc/codex/managed_config.toml <<'EOF'
# Managed Codex config for the Pen image. Codex is installed in a system path
# and upgraded only by rebuilding the image, so don't check for updates.
check_for_update_on_startup = false
EOF
chmod 0644 /etc/codex/managed_config.toml

exec /opt/pen/lib/npm-agent.sh codex @openai/codex \
  --node bin/codex.js --since 0.1.2504211509 -- "$@"
