#!/bin/bash
# Installs the newest CodeGraph release of the major version given (1) with npm, into its
# global prefix (/usr/local), without running package scripts: it needs none, and
# bundles its own runtime as a platform package. npm checks each package against the
# registry's integrity hash, and any failure fails the build.
set -euo pipefail

PACKAGE=@colbymchenry/codegraph

[ $# -eq 1 ] || { echo "codegraph: one version at a time" >&2; exit 1; }
v=$1
[[ $v =~ ^[1-9][0-9]*$ ]] || { echo "codegraph: \"$v\" isn't a major version" >&2; exit 1; }
command -v npm >/dev/null || { echo "codegraph: npm isn't in the base image" >&2; exit 1; }

export NPM_CONFIG_UPDATE_NOTIFIER=false
npm install -g --ignore-scripts --no-fund --no-audit "$PACKAGE@$v"
npm cache clean --force
rm -rf /root/.npm /root/.cache

installed=$(codegraph --version)
[[ $installed =~ ^$v\.[0-9]+\.[0-9]+$ ]] || { echo "codegraph: installed \"$installed\", not a $v.x release" >&2; exit 1; }
echo "codegraph $installed"
