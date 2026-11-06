#!/bin/bash
# pen entrypoint. One script, run twice:
#   1. as root (with only CHOWN, SETUID, SETGID, SETPCAP): create the runtime
#      user from PEN_UID/PEN_GID, fix ownership of the volume mount points, then
#      drop to that user with every capability cleared and re-run this script
#      under tini;
#      Under rootless Docker (PEN_UID=0), the host user *is* container root:
#      no user is created, and the agent stays UID 0 with every capability
#      cleared and the securebits locked so UID 0 never regains any;
#   2. as agent: copy the home seed into $HOME, merge the seed's mcpServers
#      into ~/.claude.json, then exec "$@".
set -euo pipefail

die() { echo "pen-entrypoint: $*" >&2; exit 1; }

if [ "$(id -u)" = 0 ] && [ "${PEN_PHASE:-}" != agent ]; then
  [ -n "${PEN_UID:-}" ] && [ -n "${PEN_GID:-}" ] || die "PEN_UID and PEN_GID must be set"
  [[ "$PEN_UID" =~ ^[0-9]+$ && "$PEN_GID" =~ ^[0-9]+$ ]] || die "PEN_UID/PEN_GID must be numeric"
  export HOME=/home/agent USER=agent LOGNAME=agent PEN_PHASE=agent

  if [ "$PEN_UID" = 0 ]; then
    # Rootless Docker only: UID 0 here must be an unprivileged host user
    # through a user namespace. Without one (identity map), it's real root.
    read -r inside outside count </proc/self/uid_map
    [ "$inside $outside $count" != "0 0 4294967295" ] \
      || die "refusing to run the agent as root (PEN_UID=0) outside rootless Docker"
    [ "$PEN_GID" = 0 ] || die "under rootless Docker PEN_GID must be 0 too"
    # Claude Code refuses skip-permissions as root unless told it's sandboxed.
    export IS_SANDBOX=1
    # noroot: UID 0 gets no capabilities from execve; locked, so it can't be
    # undone. With the bounding set empty and no-new-privileges, the agent
    # has no capabilities at all, ever. The root filesystem is read-only
    # (compose override), so UID 0 can't change the image's files either.
    exec setpriv --reuid=0 --regid=0 --clear-groups \
      --securebits=+noroot,+noroot_locked,+no_setuid_fixup,+no_setuid_fixup_locked,+keep_caps_locked \
      --bounding-set=-all --inh-caps=-all \
      /usr/bin/tini -s -g -- "$0" "$@"
  fi

  # Reuse an existing group with this GID (on the Mac, GID 20 is Ubuntu's
  # `dialout`); otherwise create `agent`.
  if ! getent group "$PEN_GID" >/dev/null; then
    groupadd -K GID_MIN=1 -g "$PEN_GID" agent
  fi
  if existing=$(getent passwd "$PEN_UID"); then
    die "UID $PEN_UID is already taken in the image by '${existing%%:*}'"
  fi
  # -M: the home dir is a volume, don't create it. UID_MIN=1: the Mac's 501
  # is below Ubuntu's default range.
  useradd -M -K UID_MIN=1 -u "$PEN_UID" -g "$PEN_GID" -d /home/agent -s /bin/bash agent

  # New volumes start out root-owned (the image has nothing at these paths).
  # Not recursive: a new volume is empty, and an existing one is already ours.
  for dir in /home/agent "${NUGET_PACKAGES:-}"; do
    [ -n "$dir" ] && [ -d "$dir" ] || continue
    if [ "$(stat -c %u:%g "$dir")" != "$PEN_UID:$PEN_GID" ]; then
      chown "$PEN_UID:$PEN_GID" "$dir"
    fi
  done

  # Drop to agent with an empty bounding set and no inheritable caps.
  # no-new-privileges (compose) stops it getting any back. tini becomes PID 1
  # as agent, so no root process is left in the container.
  exec setpriv --reuid="$PEN_UID" --regid="$PEN_GID" --init-groups \
    --bounding-set=-all --inh-caps=-all \
    /usr/bin/tini -s -g -- "$0" "$@"
fi

# Agent phase.
warn() { echo "pen-entrypoint: $*" >&2; }

# The home seed (~/.config/pen/home on the host, read-only here). cp
# --update=older copies what $HOME lacks or has an older copy of, and stamps
# copies with the current time, so the newest edit wins: a seed edit reaches
# every project's next session, and an agent's edit survives until the seed's
# copy is edited again. Skipped: .claude.json (it holds the login; merged
# below) and other projects' .claude/projects/<slug>/ (Claude's per-project
# memory), so one project never sees another's.
seed=/etc/pen/seed
copy_entries() { # copy_entries <from dir> <to dir> <names to skip…>
  local from=$1 to=$2 e name skip; shift 2
  for e in "$from"/*; do
    name=${e##*/}
    for skip; do [ "$name" = "$skip" ] && continue 2; done
    cp -r --update=older -- "$e" "$to/" || warn "couldn't copy ${e#"$seed"/} from the home seed"
  done
}
if [ -d "$seed" ]; then
  shopt -s dotglob nullglob
  copy_entries "$seed" "$HOME" .claude.json .claude
  if [ -d "$seed/.claude" ]; then
    mkdir -p "$HOME/.claude"
    copy_entries "$seed/.claude" "$HOME/.claude" projects
    # Claude names a project's folder by its path, with every character that
    # isn't alphanumeric replaced by '-'.
    slug=$(LC_ALL=C sed 's/[^A-Za-z0-9]/-/g' <<<"$PROJECT_DIR")
    if [ -d "$seed/.claude/projects/$slug" ]; then
      mkdir -p "$HOME/.claude/projects"
      cp -r --update=older -- "$seed/.claude/projects/$slug" "$HOME/.claude/projects/" \
        || warn "couldn't copy .claude/projects/$slug from the home seed"
    fi
  fi
  shopt -u dotglob nullglob

  # ~/.claude.json: copied only when missing (before any login). After that,
  # the seed's mcpServers are merged in (seed entries win, servers added in
  # the sandbox stay), rewriting the file only when that changes something.
  if [ -f "$seed/.claude.json" ]; then
    if [ ! -e "$HOME/.claude.json" ]; then
      cp -- "$seed/.claude.json" "$HOME/.claude.json" && chmod 600 "$HOME/.claude.json" \
        || warn "couldn't copy .claude.json from the home seed"
    elif ! jq -e 'type == "object"' "$seed/.claude.json" >/dev/null 2>&1; then
      warn "the home seed's .claude.json isn't a JSON object: not merging its mcpServers"
    elif ! jq -e --slurpfile s "$seed/.claude.json" \
        '(.mcpServers // {}) == ((.mcpServers // {}) + ($s[0].mcpServers // {}))' \
        "$HOME/.claude.json" >/dev/null 2>&1; then
      # mktemp makes it 600, like the file it replaces.
      if tmp=$(mktemp "$HOME/.claude.json.pen.XXXXXX") \
          && jq --slurpfile s "$seed/.claude.json" \
            '.mcpServers = ((.mcpServers // {}) + ($s[0].mcpServers // {}))' \
            "$HOME/.claude.json" >"$tmp" \
          && mv -f -- "$tmp" "$HOME/.claude.json"; then
        :
      else
        rm -f -- "${tmp:-}"
        warn "couldn't merge the home seed's mcpServers into ~/.claude.json"
      fi
    fi
  fi
fi

[ "$#" -gt 0 ] || set -- bash -l
exec "$@"
