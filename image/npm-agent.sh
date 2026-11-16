#!/bin/bash
# Installs an agent distributed through npm, for that agent recipe's install.sh, which
# runs it as root during the tools image build:
#
#   /opt/pen/lib/npm-agent.sh <command> <package> (--native <path> | --node <path>)
#       --since <release> [--allow-scripts] [--version-word first|last] -- <version>
#
# <version> is latest or an exact release (2.1.300). The package goes into
# /opt/pen/<command> with the base's npm (/usr/local/bin), which checks each package against
# the registry's integrity hash; any failure fails the build. Only with --allow-scripts does
# the package's own install script run (npm runs none by default). /usr/local/bin/<command>
# is then a symlink to the native binary at <path> in the package (--native), which doesn't
# use Node at run time, or an sh wrapper running the JavaScript entry at <path> on
# /usr/local/bin/node (--node), by absolute path so a node earlier on a session's PATH (nvm
# or volta in the home volume) doesn't affect it. A package without <path> is older than the
# oldest supported release, <release>. Last, <command> --version must print the release
# installed (its first or last word, last by default), and a pinned version must match it.
# Variables the recipe exports (an auto-updater's off switch) reach npm and that check.
#
# It's in image/, so changing it changes Embedded.ImageHash: pen:latest rebuilds and gets a
# new ID, and every tools image's tag, which covers that ID (ToolsImage.Tag), changes, so the
# agents are reinstalled with it on next use.
set -euo pipefail

NODE=/usr/local/bin/node
NPM=/usr/local/bin/npm
usage() { echo "npm-agent.sh: $*" >&2; exit 1; }

[ $# -ge 2 ] || usage "usage: npm-agent.sh <command> <package> (--native <path> | --node <path>) --since <release> [--allow-scripts] [--version-word first|last] -- <version>"
cmd=$1 package=$2
shift 2
[[ $cmd =~ ^[a-z][a-z0-9-]*$ ]] || usage "\"$cmd\" isn't a command name"
mode= entry= since= scripts=() word=last
while [ $# -gt 0 ]; do
  case $1 in
    --native|--node) [ -z "$mode" ] && [ $# -ge 2 ] || usage "one of --native or --node, with a path"; mode=${1#--} entry=$2; shift 2 ;;
    --since) [ $# -ge 2 ] || usage "--since needs a release"; since=$2; shift 2 ;;
    --allow-scripts) scripts=(--allow-scripts="$package"); shift ;;
    --version-word) [[ ${2-} =~ ^(first|last)$ ]] || usage "--version-word is first or last"; word=$2; shift 2 ;;
    --) shift; break ;;
    *) usage "unknown option \"$1\"" ;;
  esac
done
[ -n "$mode" ] && [ -n "$since" ] || usage "needs --native or --node, and --since"
# The package and path reach npm, a symlink and a wrapper script: an npm name (optionally
# @scope/), and a relative path inside the package, of names that start with a letter, digit
# or _ (so no "..", no leading -) and hold only letters, digits and . _ -.
[[ $package =~ ^(@[a-z0-9][a-z0-9._-]*/)?[a-z0-9][a-z0-9._-]*$ && $package != *..* ]] || usage "\"$package\" isn't an npm package name"
[[ $entry =~ ^[A-Za-z0-9_][A-Za-z0-9._-]*(/[A-Za-z0-9_][A-Za-z0-9._-]*)*$ && $entry != *..* ]] || usage "\"$entry\" isn't a path inside the package"

PREFIX=/opt/pen/$cmd
[ $# -eq 1 ] || { echo "$cmd: one version at a time" >&2; exit 1; }
v=$1
[[ $v =~ ^(latest|[0-9]+\.[0-9]+\.[0-9]+)$ ]] || { echo "$cmd: \"$v\" isn't latest or a release (x.y.z)" >&2; exit 1; }
[ -x "$NODE" ] && [ -x "$NPM" ] || { echo "$cmd: the base image has no $NODE and $NPM" >&2; exit 1; }

# A throwaway HOME and TMPDIR for the install and the check: npm's cache, Node's compile
# cache and what the agent writes on its first run go there, so they leave nothing in the
# image. HOME isn't under TMPDIR or /tmp: Codex won't set up its PATH helpers in a temporary
# dir, and warns.
check_home=$(mktemp -d /root/pen-check.XXXXXX)
trap 'rm -rf "$check_home"' EXIT
mkdir "$check_home/home" "$check_home/tmp"
run() { env HOME="$check_home/home" TMPDIR="$check_home/tmp" NPM_CONFIG_UPDATE_NOTIFIER=false "$@"; }

run "$NODE" "$NPM" install -g --prefix "$PREFIX" --no-fund --no-audit "${scripts[@]}" "$package@$v"

target="$PREFIX/lib/node_modules/$package/$entry"
if [ "$mode" = native ]; then
  [ -e "$target" ] || { echo "$cmd: releases before $since aren't supported (no $entry)" >&2; exit 1; }
  [ -x "$target" ] || { echo "$cmd: $target isn't executable (did the package's install script run?)" >&2; exit 1; }
  ln -sf "$target" "/usr/local/bin/$cmd"
else
  [ -f "$target" ] || { echo "$cmd: releases before $since aren't supported (no $entry)" >&2; exit 1; }
  # %q leaves these validated paths as they are, and would escape anything sh treats specially.
  printf '#!/bin/sh\nexec %q %q "$@"\n' "$NODE" "$target" >"/usr/local/bin/$cmd"
  chmod 0755 "/usr/local/bin/$cmd"
fi

installed=$(run PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin "$cmd" --version)
if [ "$word" = first ]; then installed=${installed%% *}; else installed=${installed##* }; fi
[[ $installed =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "$cmd: $cmd --version gives \"$installed\"" >&2; exit 1; }
[ "$v" = latest ] || [ "$installed" = "$v" ] || { echo "$cmd: installed $installed, not $v" >&2; exit 1; }
echo "$cmd $installed"
