#!/bin/bash
# pen entrypoint. One script, run twice:
#   1. as root (with only CHOWN, SETUID, SETGID, SETPCAP): create the runtime
#      user from PEN_UID/PEN_GID, fix ownership of the volume mount points, then
#      drop to that user with every capability cleared and re-run this script
#      under tini;
#   2. as agent: exec "$@".
set -euo pipefail

die() { echo "pen-entrypoint: $*" >&2; exit 1; }

if [ "$(id -u)" = 0 ]; then
  [ -n "${PEN_UID:-}" ] && [ -n "${PEN_GID:-}" ] || die "PEN_UID and PEN_GID must be set"
  [[ "$PEN_UID" =~ ^[0-9]+$ && "$PEN_GID" =~ ^[0-9]+$ ]] || die "PEN_UID/PEN_GID must be numeric"
  [ "$PEN_UID" != 0 ] || die "refusing to run the agent as root (PEN_UID=0)"

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

  export HOME=/home/agent USER=agent LOGNAME=agent
  # Drop to agent with an empty bounding set and no inheritable caps.
  # no-new-privileges (compose) stops it getting any back. tini becomes PID 1
  # as agent, so no root process is left in the container.
  exec setpriv --reuid="$PEN_UID" --regid="$PEN_GID" --init-groups \
    --bounding-set=-all --inh-caps=-all \
    /usr/bin/tini -s -g -- "$0" "$@"
fi

# Agent phase.
[ "$#" -gt 0 ] || set -- bash -l
exec "$@"
