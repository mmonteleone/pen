#!/bin/bash
# Installs Claude Code at the version given (latest, or an exact release such as 2.1.300)
# with Pen's private npm, into /opt/pen/claude. npm checks each package against the
# registry's integrity hash, and any failure fails the build.
#
# Only the package's own install script runs: it puts the native binary for this platform
# in place of its bin stub. /usr/local/bin/claude is a symlink to that binary, which
# doesn't use Pen's Node at run time.
set -euo pipefail

PACKAGE=@anthropic-ai/claude-code
PREFIX=/opt/pen/claude
NODE_BIN=/opt/pen/node/bin

[ $# -eq 1 ] || { echo "claude: one version at a time" >&2; exit 1; }
v=$1
[[ $v =~ ^(latest|[0-9]+\.[0-9]+\.[0-9]+)$ ]] || { echo "claude: \"$v\" isn't latest or a release (2.1.300)" >&2; exit 1; }
[ -x "$NODE_BIN/npm" ] || { echo "claude: Pen's npm ($NODE_BIN/npm) isn't in the base image" >&2; exit 1; }

# A throwaway HOME and TMPDIR for the install and the check: npm's cache, Node's compile
# cache and what Claude Code writes on its first run go there, so they leave nothing in
# the image.
check_home=$(mktemp -d /root/pen-check.XXXXXX)
mkdir "$check_home/home" "$check_home/tmp"
run() { env HOME="$check_home/home" TMPDIR="$check_home/tmp" NPM_CONFIG_UPDATE_NOTIFIER=false DISABLE_AUTOUPDATER=1 CLAUDE_CODE_DISABLE_AUTOUPDATE=1 "$@"; }

run PATH="$NODE_BIN:$PATH" npm install -g --prefix "$PREFIX" --no-fund --no-audit --allow-scripts="$PACKAGE" "$PACKAGE@$v"

binary="$PREFIX/lib/node_modules/$PACKAGE/bin/claude.exe"
# Releases before 2.1.113 have a JavaScript CLI (cli.js) instead of the native binary.
[ -e "$binary" ] || { echo "claude: releases before 2.1.113 aren't supported (no bin/claude.exe)" >&2; exit 1; }
[ -x "$binary" ] || { echo "claude: $binary isn't executable (did the package's install script run?)" >&2; exit 1; }
ln -sf "$binary" /usr/local/bin/claude

installed=$(run PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin claude --version)
rm -rf "$check_home"
installed=${installed%% *}
[[ $installed =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "claude: claude --version gives \"$installed\"" >&2; exit 1; }
[ "$v" = latest ] || [ "$installed" = "$v" ] || { echo "claude: installed $installed, not $v" >&2; exit 1; }
echo "claude $installed"
