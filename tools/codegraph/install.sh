#!/bin/bash
# Installs the newest CodeGraph release of the major version given (1) with Pen's
# private npm, into /opt/pen/codegraph, without running package scripts: it needs none.
# npm checks each package against the registry's integrity hash, and any failure fails
# the build.
#
# The package's own bin (npm-shim.js) needs a Node on PATH and falls back to downloading
# from GitHub when the platform package is missing. The platform package
# (@colbymchenry/codegraph-linux-<arch>) bundles its own Node and app, and its
# bin/codegraph is a sh launcher that resolves symlinks to find them, so it runs
# standalone: /usr/local/bin/codegraph is a symlink to it, with no shim and no use of
# Pen's Node at run time.
set -euo pipefail

PACKAGE=@colbymchenry/codegraph
PREFIX=/opt/pen/codegraph
NODE_BIN=/opt/pen/node/bin

[ $# -eq 1 ] || { echo "codegraph: one version at a time" >&2; exit 1; }
v=$1
[[ $v =~ ^[1-9][0-9]*$ ]] || { echo "codegraph: \"$v\" isn't a major version" >&2; exit 1; }
[ -x "$NODE_BIN/npm" ] || { echo "codegraph: Pen's npm ($NODE_BIN/npm) isn't in the base image" >&2; exit 1; }

export PATH="$NODE_BIN:$PATH" NPM_CONFIG_UPDATE_NOTIFIER=false
npm install -g --prefix "$PREFIX" --ignore-scripts --no-fund --no-audit "$PACKAGE@$v"
npm cache clean --force
rm -rf /root/.npm /root/.cache

main="$PREFIX/lib/node_modules/$PACKAGE"
platform="$PACKAGE-$(node -p 'process.platform + "-" + process.arch')"
launcher=$(node -p 'require.resolve(process.argv[1] + "/bin/codegraph", { paths: [process.argv[2]] })' "$platform" "$main") \
  || { echo "codegraph: $platform didn't install" >&2; exit 1; }
[ -x "$launcher" ] || { echo "codegraph: $launcher isn't executable" >&2; exit 1; }
ln -sf "$launcher" /usr/local/bin/codegraph

installed=$(env PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin codegraph --version)
[[ $installed =~ ^$v\.[0-9]+\.[0-9]+$ ]] || { echo "codegraph: installed \"$installed\", not a $v.x release" >&2; exit 1; }
echo "codegraph $installed"
