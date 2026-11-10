#!/bin/bash
# Installs OpenCode at the version given (latest, or an exact release such as 1.19.0)
# with Pen's private npm, into /opt/pen/opencode. npm checks each package against the
# registry's integrity hash, and any failure fails the build.
#
# Only the package's own install script runs: it puts the native binary for this platform
# in place of its bin stub. /usr/local/bin/opencode is a symlink to that binary, which
# doesn't use Pen's Node at run time.
set -euo pipefail

PACKAGE=opencode-ai
PREFIX=/opt/pen/opencode
NODE_BIN=/opt/pen/node/bin

[ $# -eq 1 ] || { echo "opencode: one version at a time" >&2; exit 1; }
v=$1
[[ $v =~ ^(latest|[0-9]+\.[0-9]+\.[0-9]+)$ ]] || { echo "opencode: \"$v\" isn't latest or a release (1.19.0)" >&2; exit 1; }
[ -x "$NODE_BIN/npm" ] || { echo "opencode: Pen's npm ($NODE_BIN/npm) isn't in the base image" >&2; exit 1; }

# A throwaway HOME and TMPDIR for the install and the check: npm's cache, Node's compile
# cache and what OpenCode writes on its first run (its config, data and temp dirs) go
# there, so they leave nothing in the image.
check_home=$(mktemp -d /root/pen-check.XXXXXX)
mkdir "$check_home/home" "$check_home/tmp"
run() { env HOME="$check_home/home" TMPDIR="$check_home/tmp" OPENCODE_DISABLE_AUTOUPDATE=1 NPM_CONFIG_UPDATE_NOTIFIER=false "$@"; }

run PATH="$NODE_BIN:$PATH" npm install -g --prefix "$PREFIX" --no-fund --no-audit --allow-scripts="$PACKAGE" "$PACKAGE@$v"

binary="$PREFIX/lib/node_modules/$PACKAGE/bin/opencode.exe"
# Releases before 1.15.1 name the binary bin/opencode.
[ -e "$binary" ] || { echo "opencode: releases before 1.15.1 aren't supported (no bin/opencode.exe)" >&2; exit 1; }
[ -x "$binary" ] || { echo "opencode: $binary isn't executable (did the package's install script run?)" >&2; exit 1; }
ln -sf "$binary" /usr/local/bin/opencode

installed=$(run PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin opencode --version)
rm -rf "$check_home"
installed=${installed##* }
[[ $installed =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "opencode: opencode --version gives \"$installed\"" >&2; exit 1; }
[ "$v" = latest ] || [ "$installed" = "$v" ] || { echo "opencode: installed $installed, not $v" >&2; exit 1; }
echo "opencode $installed"
