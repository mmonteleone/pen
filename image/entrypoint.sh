#!/bin/bash
# Pen entrypoint. One script, run twice:
#   1. as root (with only CHOWN, SETUID, SETGID, SETPCAP): create the runtime
#      user from PEN_UID/PEN_GID, fix ownership of the volume mount points, then
#      drop to that user with every capability cleared and re-run this script
#      under tini;
#      Under rootless Docker (PEN_UID=0), the host user *is* container root:
#      no user is created, and the agent stays UID 0 with every capability
#      cleared and the securebits locked so UID 0 never regains any;
#   2. as agent: copy the home seed into $HOME, merge the seed's mcpServers
#      into ~/.claude.json, in docker sessions point ~/.docker/config.json's
#      proxies at this session's proxy, then exec "$@".
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
    # Java takes user.home from passwd, where root's home is /root on the
    # read-only root filesystem, so Maven, Gradle and the rest couldn't write
    # their caches. (The other tools go by $HOME, and passwd can't be changed
    # here.) A JAVA_TOOL_OPTIONS from the user's [env] comes after, so its own
    # -Duser.home wins. Every JVM prints this setting to stderr when it starts.
    export JAVA_TOOL_OPTIONS="-Duser.home=$HOME${JAVA_TOOL_OPTIONS:+ $JAVA_TOOL_OPTIONS}"
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
# Seed policy and Docker runtime settings have separate ownership.
/usr/local/lib/pen/sync-seed.sh /etc/pen/seed "$HOME" "$PROJECT_DIR"
/usr/local/lib/pen/docker-proxy.sh

[ "$#" -gt 0 ] || set -- bash -l
exec "$@"
