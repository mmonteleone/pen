#!/usr/bin/env bash
# Interactive sandbox session without the CLI (stand-in until chunk 7).
# Uses a throwaway scratch project, NOT a real repo: the .git protections
# don't exist yet (chunk 15).
#
#   tests/dev-session.sh [cmd…]     e.g. tests/dev-session.sh claude
#
# The scratch project and its volumes persist between runs (so logins stick):
#   PEN_DEV_DIR (default ~/.pen-dev), volumes pen-dev_home / pen-dev_nuget.
# On exit it prints the domains the proxy denied, to finish the allowlist.
set -euo pipefail

REPO=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
export PROJECT_DIR=${PEN_DEV_DIR:-$HOME/.pen-dev}
mkdir -p "$PROJECT_DIR"
PROJECT_DIR=$(cd "$PROJECT_DIR" && pwd -P)
[ -d "$PROJECT_DIR/.git" ] || git -C "$PROJECT_DIR" init -q
export PEN_PROJECT=pen-dev PEN_IMAGE=${PEN_IMAGE:-pen:latest}
export PEN_UID=$(id -u) PEN_GID=$(id -g)
export NUGET_PACKAGES=${NUGET_PACKAGES:-$HOME/.nuget/packages}

session=$(mktemp -d "${TMPDIR:-/tmp}/pen-dev-session.XXXXXX")
chmod 700 "$session"
cp "$REPO/compose.yaml" "$REPO/proxy/squid.conf" "$REPO/proxy/allowlist.txt" "$session/"
: >"$session/proxy.log"; chmod 666 "$session/proxy.log"
name="$PEN_PROJECT-$(basename "$session" | tr 'A-Z.' 'a-z-')"
docker volume create "${PEN_PROJECT}_home" >/dev/null
docker volume create "${PEN_PROJECT}_nuget" >/dev/null

dc() { docker compose --progress quiet -f "$session/compose.yaml" -p "$name" "$@"; }
finish() {
  docker rm -f "$name-agent" >/dev/null 2>&1 || true
  dc down -t 1 >/dev/null 2>&1 || true
  denied=$(awk '$3 ~ /^TCP_DENIED/ { u = $5; sub(/^[a-z]+:\/\//, "", u); sub(/[:\/].*$/, "", u); print u }' "$session/proxy.log" | sort -u | tr '\n' ' ')
  [ -z "$denied" ] || echo "blocked: $denied(full log: $session/proxy.log)"
}
trap finish EXIT

tty=(); [ -t 0 ] || tty=(-T)
dc run --rm "${tty[@]}" --name "$name-agent" agent "$@"
