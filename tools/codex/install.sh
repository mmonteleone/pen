#!/bin/bash
# Installs Codex at the version given (latest, or an exact release such as 0.167.0) with
# Pen's private npm, into /opt/pen/codex. npm checks each package against the registry's
# integrity hash, and any failure fails the build.
#
# No install scripts run: the native binary comes in this platform's optional package.
# /usr/local/bin/codex runs the package's launcher (bin/codex.js) on Pen's Node, so a
# session's own node (the node tool) doesn't affect it. Codex's update check is off in
# /etc/codex/managed_config.toml, since only a rebuilt image upgrades it.
set -euo pipefail

PACKAGE=@openai/codex
PREFIX=/opt/pen/codex
NODE_BIN=/opt/pen/node/bin

[ $# -eq 1 ] || { echo "codex: one version at a time" >&2; exit 1; }
v=$1
[[ $v =~ ^(latest|[0-9]+\.[0-9]+\.[0-9]+)$ ]] || { echo "codex: \"$v\" isn't latest or a release (0.167.0)" >&2; exit 1; }
[ -x "$NODE_BIN/node" ] && [ -x "$NODE_BIN/npm" ] || { echo "codex: Pen's Node ($NODE_BIN) isn't in the base image" >&2; exit 1; }

# A throwaway HOME and TMPDIR for the install and the check: npm's cache, Node's compile
# cache and what Codex writes on its first run go there, so they leave nothing in the
# image. HOME isn't under TMPDIR or /tmp: Codex won't set up its PATH helpers in a
# temporary dir, and warns.
check_home=$(mktemp -d /root/pen-check.XXXXXX)
mkdir "$check_home/home" "$check_home/tmp"
run() { env HOME="$check_home/home" TMPDIR="$check_home/tmp" NPM_CONFIG_UPDATE_NOTIFIER=false "$@"; }

run PATH="$NODE_BIN:$PATH" npm install -g --prefix "$PREFIX" --no-fund --no-audit "$PACKAGE@$v"

launcher="$PREFIX/lib/node_modules/$PACKAGE/bin/codex.js"
[ -f "$launcher" ] || { echo "codex: releases before 0.1.2504211509 aren't supported (no bin/codex.js)" >&2; exit 1; }
printf '#!/bin/sh\nexec %s/node %s "$@"\n' "$NODE_BIN" "$launcher" >/usr/local/bin/codex
chmod 0755 /usr/local/bin/codex

install -d -m 0755 /etc/codex
cat >/etc/codex/managed_config.toml <<'EOF'
# Managed Codex config for the Pen image. Codex is installed in a system path
# and upgraded only by rebuilding the image, so don't check for updates.
check_for_update_on_startup = false
EOF
chmod 0644 /etc/codex/managed_config.toml

installed=$(run PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin codex --version)
rm -rf "$check_home"
installed=${installed##* }
[[ $installed =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "codex: codex --version gives \"$installed\"" >&2; exit 1; }
[ "$v" = latest ] || [ "$installed" = "$v" ] || { echo "codex: installed $installed, not $v" >&2; exit 1; }
echo "codex $installed"
