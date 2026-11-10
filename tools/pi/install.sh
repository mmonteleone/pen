#!/bin/bash
# Installs Pi at the version given (latest, or an exact release such as 1.5.2) with Pen's
# private npm, into /opt/pen/pi. npm checks each package against the registry's integrity
# hash, and any failure fails the build.
#
# No install scripts run. /usr/local/bin/pi runs the package's bundle (dist/bundle/cli.js)
# on Pen's Node, so a session's own node (the node tool) doesn't affect it.
set -euo pipefail

PACKAGE=@earendil-works/pi-coding-agent
PREFIX=/opt/pen/pi
NODE_BIN=/opt/pen/node/bin

[ $# -eq 1 ] || { echo "pi: one version at a time" >&2; exit 1; }
v=$1
[[ $v =~ ^(latest|[0-9]+\.[0-9]+\.[0-9]+)$ ]] || { echo "pi: \"$v\" isn't latest or a release (1.5.2)" >&2; exit 1; }
[ -x "$NODE_BIN/node" ] && [ -x "$NODE_BIN/npm" ] || { echo "pi: Pen's Node ($NODE_BIN) isn't in the base image" >&2; exit 1; }

export PATH="$NODE_BIN:$PATH" NPM_CONFIG_UPDATE_NOTIFIER=false
npm install -g --prefix "$PREFIX" --no-fund --no-audit "$PACKAGE@$v"
npm cache clean --force
rm -rf /root/.npm /root/.cache

bundle="$PREFIX/lib/node_modules/$PACKAGE/dist/bundle/cli.js"
[ -f "$bundle" ] || { echo "pi: $bundle is missing" >&2; exit 1; }
printf '#!/bin/sh\nexec %s/node %s "$@"\n' "$NODE_BIN" "$bundle" >/usr/local/bin/pi
chmod 0755 /usr/local/bin/pi

# A throwaway HOME and TMPDIR (Node's compile cache goes there), so the check leaves
# nothing in the image.
check_home=$(mktemp -d /root/pen-check.XXXXXX)
mkdir "$check_home/home" "$check_home/tmp"
installed=$(env HOME="$check_home/home" TMPDIR="$check_home/tmp" PI_SKIP_VERSION_CHECK=1 PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin pi --version)
rm -rf "$check_home"
installed=${installed##* }
[[ $installed =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "pi: pi --version gives \"$installed\"" >&2; exit 1; }
[ "$v" = latest ] || [ "$installed" = "$v" ] || { echo "pi: installed $installed, not $v" >&2; exit 1; }
echo "pi $installed"
