#!/bin/bash
# Installs the newest CodeGraph release of the major version given (1) with the base's
# npm (/usr/local/bin), into /opt/pen/codegraph, without running package scripts: it
# needs none. npm checks each package against the registry's integrity hash, and any
# failure fails the build.
#
# The package's own bin (npm-shim.js) needs a Node on PATH and falls back to downloading
# from GitHub when the platform package is missing. The platform package
# (@colbymchenry/codegraph-linux-<arch>) bundles its own Node and app, and its
# bin/codegraph is a sh launcher that resolves symlinks to find them, so it runs
# standalone: /usr/local/bin/codegraph is a symlink to it, with no shim and no use of
# the base's Node at run time.
set -euo pipefail

PACKAGE=@colbymchenry/codegraph
PREFIX=/opt/pen/codegraph
NODE=/usr/local/bin/node
NPM=/usr/local/bin/npm

[ $# -eq 1 ] || { echo "codegraph: one version at a time" >&2; exit 1; }
v=$1
[[ $v =~ ^[1-9][0-9]*$ ]] || { echo "codegraph: \"$v\" isn't a major version" >&2; exit 1; }
[ -x "$NODE" ] && [ -x "$NPM" ] || { echo "codegraph: the base image has no $NODE and $NPM" >&2; exit 1; }

# A throwaway HOME and TMPDIR for the install and the check: npm's cache, Node's compile
# cache and what CodeGraph writes on its first run go there, so they leave nothing in
# the image.
check_home=$(mktemp -d /root/pen-check.XXXXXX)
mkdir "$check_home/home" "$check_home/tmp"
run() { env HOME="$check_home/home" TMPDIR="$check_home/tmp" NPM_CONFIG_UPDATE_NOTIFIER=false "$@"; }

run "$NODE" "$NPM" install -g --prefix "$PREFIX" --ignore-scripts --no-fund --no-audit "$PACKAGE@$v"

main="$PREFIX/lib/node_modules/$PACKAGE"
platform="$PACKAGE-$(run "$NODE" -p 'process.platform + "-" + process.arch')"
launcher=$(run "$NODE" -p 'require.resolve(process.argv[1] + "/bin/codegraph", { paths: [process.argv[2]] })' "$platform" "$main") \
  || { echo "codegraph: $platform didn't install" >&2; exit 1; }
[ -x "$launcher" ] || { echo "codegraph: $launcher isn't executable" >&2; exit 1; }
ln -sf "$launcher" /usr/local/bin/codegraph

installed=$(run PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin codegraph --version)
rm -rf "$check_home"
[[ $installed =~ ^$v\.[0-9]+\.[0-9]+$ ]] || { echo "codegraph: installed \"$installed\", not a $v.x release" >&2; exit 1; }
echo "codegraph $installed"
