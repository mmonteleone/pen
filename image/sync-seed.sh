#!/bin/bash
# Seed files are user-owned policy. Files absent from the seed stay in the
# volume, so logins, history and runtime state survive. Removing a seed file
# stops managing it; it does not delete the sandbox copy.
set -euo pipefail
seed=$1 home=$2 project=$3
warn() { echo "pen-entrypoint: $*" >&2; }
[ -d "$seed" ] || exit 0
slug=$(LC_ALL=C sed 's/[^A-Za-z0-9]/-/g' <<<"$project")

shopt -s dotglob nullglob
# The home volume is agent-writable: a target that's a symlink is never
# followed, so the sync can't write outside $HOME. A seed directory whose
# target is anything but a real directory is skipped with a warning.
copy_tree() {
  local from=$1 to=$2 rel=$3 e name path
  if [ -L "$to" ] || { [ -e "$to" ] && [ ! -d "$to" ]; }; then
    warn "skipping $rel from the home seed: it's a directory there but not in this sandbox's home"
    return 0
  fi
  if ! mkdir -p -- "$to"; then
    warn "couldn't create $rel from the home seed"
    return 0
  fi
  for e in "$from"/*; do
    name=${e##*/}
    path=${rel:+$rel/}$name
    case "$path" in
      .claude.json | .claude/.credentials.json | .codex/auth.json | \
      .local/share/opencode/auth.json | .pi/agent/auth.json | .docker) continue ;;
      .claude/projects/*) [[ "$path" = ".claude/projects/$slug" || "$path" = ".claude/projects/$slug/"* ]] || continue ;;
    esac
    if [ -d "$e" ]; then
      copy_tree "$e" "$to/$name" "$path"
    else
      if [ -d "$to/$name" ] && [ ! -L "$to/$name" ]; then
        warn "skipping $path from the home seed: it's a directory in this sandbox's home"
        continue
      fi
      # Replaced rather than written through, so a symlink there isn't followed.
      { rm -f -- "$to/$name" && cp -- "$e" "$to/$name"; } || warn "couldn't copy $path from the home seed"
    fi
  done
}
copy_tree "$seed" "$home" ""

# Claude mixes user MCP settings with login/runtime state in one file.
# The seed owns the entire mcpServers map; every other field stays local.
if [ -f "$seed/.claude.json" ]; then
  cfg=$home/.claude.json
  if ! jq -e 'type == "object" and ((.mcpServers // {}) | type == "object")' "$seed/.claude.json" >/dev/null 2>&1; then
    warn "the home seed's .claude.json must contain an object with an mcpServers object"
  elif [ -e "$cfg" ] && ! jq -e 'type == "object"' "$cfg" >/dev/null 2>&1; then
    warn "~/.claude.json isn't a JSON object: keeping it unchanged"
  elif [ -e "$cfg" ] && jq -e --slurpfile s "$seed/.claude.json" \
      '.mcpServers == ($s[0].mcpServers // {})' "$cfg" >/dev/null 2>&1; then
    :
  else
    if tmp=$(mktemp "$cfg.pen.XXXXXX") \
        && { [ -e "$cfg" ] && cat -- "$cfg" || echo '{}'; } \
          | jq --slurpfile s "$seed/.claude.json" '.mcpServers = ($s[0].mcpServers // {})' >"$tmp" \
        && mv -f -- "$tmp" "$cfg"; then
      :
    else
      rm -f -- "${tmp:-}"
      warn "couldn't apply the home seed's mcpServers to ~/.claude.json"
    fi
  fi
fi
