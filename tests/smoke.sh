#!/usr/bin/env bash
# Container smoke checks for pen, accumulated chunk by chunk. Run on both the
# Mac (OrbStack/arm64) and the server (Docker/amd64), against the installed pen
# (cli/install.sh). Exits non-zero on the first failure.
#
#   tests/smoke.sh                 all checks
#   SMOKE_SLOW=1 tests/smoke.sh    also `pen upgrade` (reinstalls the agents, ~1 min)
#
# Optional, for checks that need real credentials (values are only ever written
# to a throwaway config file, mode 600, removed on exit):
#   CLAUDE_CODE_OAUTH_TOKEN, OPENAI_API_KEY   trivial prompts in claude / codex
#
# Config-dependent checks run pen with a throwaway HOME, so your own
# ~/.config/pen is never changed. Everything else uses your real HOME (pen
# creates ~/.config/pen/ on its first run, as it would anyway).
set -euo pipefail

REPO=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
IMAGE=pen:latest
PEN=${PEN:-pen}

# --- output -------------------------------------------------------------------
pass() { printf '  \033[32mok\033[0m   %s\n' "$*"; }
fail() { printf '  \033[31mFAIL\033[0m %s\n' "$*" >&2; exit 1; }
note() { printf '  \033[33mnote\033[0m %s\n' "$*"; }
section() { printf '\n\033[1m%s\033[0m\n' "$*"; }
not() { ! "$@"; }
check() { # check <description> <command…>
  local desc=$1; shift
  if "$@"; then pass "$desc"; else fail "$desc"; fi
}
contains() { grep -qF -- "$1" <<<"$2"; } # contains <needle> <haystack>

command -v "$PEN" >/dev/null || fail "pen not found on PATH: run cli/install.sh"
command -v python3 >/dev/null || fail "python3 is needed on the host (pty and lock checks)"

# --- fixture project ------------------------------------------------------------
# Under $HOME so the "only the project path exists under /Users" check means
# something. Removed (with its volumes, containers and runtime dir) on exit.
REAL_HOME=$HOME
PROJECT_DIR=$(cd "$(mktemp -d "$HOME/.pen-smoke.XXXXXX")" && pwd -P)
SCRATCH=$(mktemp -d "${TMPDIR:-/tmp}/pen-smoke.XXXXXX")
FAKE_HOME=$SCRATCH/home
mkdir -p "$FAKE_HOME"
export NUGET_PACKAGES=${NUGET_PACKAGES:-$HOME/.nuget/packages}
PEN_UID=$(id -u) PEN_GID=$(id -g)
if command -v sha256sum >/dev/null; then sha() { sha256sum; }; else sha() { shasum -a 256; }; fi
HASH=$(printf %s "$PROJECT_DIR" | sha | cut -c1-8)
KEY= RUNTIME=
EXTRA_DIRS=() # other fixture projects: removed on exit, with their keys' state

# forget <dir>: remove the state of any project key made for <dir>.
forget() {
  local h k
  h=$(printf %s "$(cd "$1" && pwd -P)" | sha | cut -c1-8)
  for k in "$REAL_HOME"/.local/state/pen/pen-*-"$h"; do
    [ -d "$k" ] || continue
    k=$(basename "$k")
    docker ps -aq --filter "name=^$k-" | xargs docker rm -f >/dev/null 2>&1 || true
    docker network ls -q --filter "name=^$k-" | xargs docker network rm >/dev/null 2>&1 || true
    docker volume rm "${k}_home" "${k}_nuget" >/dev/null 2>&1 || true
    rm -rf "$REAL_HOME/.local/state/pen/$k"
  done
}

cleanup() {
  cd /
  if [ -n "$KEY" ]; then
    docker ps -aq --filter "name=^$KEY-" | xargs docker rm -f >/dev/null 2>&1 || true
    docker network ls -q --filter "name=^$KEY-" | xargs docker network rm >/dev/null 2>&1 || true
    docker volume rm "${KEY}_home" "${KEY}_nuget" >/dev/null 2>&1 || true
    rm -rf "$REAL_HOME/.local/state/pen/$KEY"
  fi
  local d
  for d in ${EXTRA_DIRS[@]+"${EXTRA_DIRS[@]}"}; do [ -d "$d" ] && forget "$d"; rm -rf "$d"; done
  rm -rf "$PROJECT_DIR" "$SCRATCH"
}
trap cleanup EXIT

git -C "$PROJECT_DIR" init -q
git -C "$PROJECT_DIR" -c user.name=smoke -c user.email=smoke@example.invalid commit -q --allow-empty -m init
cd "$PROJECT_DIR"

# pen_run <cmd…>: one whole session without a TTY, like `pen run <cmd…> </dev/null`.
pen_run() { "$PEN" run "$@" </dev/null; }
# in_sandbox <bash script>: run a script in a fresh session, as agent.
in_sandbox() { pen_run bash -c "$1"; }
# fake_pen <args…>: pen with the throwaway HOME (its config in $FAKE_HOME/.config/pen).
fake_pen() { HOME=$FAKE_HOME DOCKER_CONFIG=${DOCKER_CONFIG:-$REAL_HOME/.docker} "$PEN" "$@" </dev/null; }
fake_config() { mkdir -p "$FAKE_HOME/.config/pen"; printf '%s\n' "$@" >"$FAKE_HOME/.config/pen/config"; chmod 600 "$FAKE_HOME/.config/pen/config"; }
# The newest session's dir (under the real HOME's runtime dir).
last_session() { ls -td "$RUNTIME"/sessions/*/ | head -1; }
containers() { docker ps -aq --filter "name=^$KEY-" | wc -l | tr -d ' '; }

# The first session builds pen:latest if it's missing or stale, and fixes the project key.
"$PEN" run true </dev/null 2>&1 | grep '^pen:' || true
RUNTIME=$(ls -d "$HOME"/.local/state/pen/pen-*-"$HASH")
KEY=$(basename "$RUNTIME")

# ==============================================================================
section "1. Base image"

host_arch=$(uname -m); case $host_arch in arm64) host_arch=aarch64 ;; esac
check "uname -m matches the host ($host_arch)" \
  test "$(docker run --rm --entrypoint uname "$IMAGE" -m)" = "$host_arch"
check "Ubuntu 24.04" \
  docker run --rm --entrypoint bash "$IMAGE" -c '. /etc/os-release && [ "$ID" = ubuntu ] && [ "$VERSION_ID" = 24.04 ]'
check "no users or groups with ID 1000–60000 in the image" \
  docker run --rm --entrypoint bash "$IMAGE" -c '! getent passwd | awk -F: "\$3>=1000 && \$3<=60000" | grep . && ! getent group | awk -F: "\$3>=1000 && \$3<=60000" | grep .'
check "nothing under /home/agent in the image" \
  docker run --rm --entrypoint bash "$IMAGE" -c '! test -e /home/agent'

sysgit=$(docker run --rm --entrypoint git "$IMAGE" config --system --list)
for want in \
  'credential.https://github.com.helper=!gh auth git-credential' \
  'url.https://github.com/.insteadof=git@github.com:' \
  'url.https://github.com/.insteadof=ssh://git@github.com/' \
  'safe.directory=*' \
  'core.excludesfile=/etc/pen/gitignore'; do
  check "system gitconfig: $want" grep -qxF -- "$want" <<<"$sysgit"
done
check "system gitignore lists .codegraph/" \
  docker run --rm --entrypoint grep "$IMAGE" -qx '.codegraph/' /etc/pen/gitignore
check "signing off via GIT_CONFIG_* (commit.gpgsign, tag.gpgsign = false)" \
  docker run --rm --entrypoint bash "$IMAGE" -c '[ "$(git config commit.gpgsign)" = false ] && [ "$(git config tag.gpgsign)" = false ]'
check "SSH remote rewritten to https" \
  docker run --rm --entrypoint bash "$IMAGE" -c '[ "$(git ls-remote --get-url git@github.com:octo/repo.git)" = https://github.com/octo/repo.git ]'
check "tools present (git gh rg fd jq curl less vim unzip make python3 uv node npm tini capsh docker 'docker compose' dotnet)" \
  docker run --rm --entrypoint bash "$IMAGE" -c 'for c in git gh rg fd jq curl less vim unzip make gcc python3 uv uvx node npm tini capsh setpriv docker dotnet; do command -v $c >/dev/null || { echo "missing $c" >&2; exit 1; }; done; docker compose version >/dev/null'

# ==============================================================================
section "2. Entrypoint and hardening (step 9)"

git -C "$PROJECT_DIR" config commit.gpgsign true
out=$(in_sandbox '
  echo "id=$(id -u):$(id -g)"
  echo "user=$(id -un) home=$HOME"
  grep -E "^(CapInh|CapPrm|CapEff|CapBnd|CapAmb|NoNewPrivs):" /proc/self/status
  capsh --print | grep -E "^Bounding set"
  command -v sudo >/dev/null && echo "sudo=present" || echo "sudo=absent"
  echo "pid1=$(cat /proc/1/comm) pid1uid=$(stat -c %u /proc/1)"
  echo "pids.max=$(cat /sys/fs/cgroup/pids.max 2>/dev/null)"
  echo "home_owner=$(stat -c %u:%g /home/agent)"
  echo "nuget_owner=$(stat -c %u:%g "$NUGET_PACKAGES")"
  test -e /var/run/docker.sock && echo "docker.sock=present" || echo "docker.sock=absent"
  printf "#!/bin/sh\necho tmp-exec-ok\n" >/tmp/x.sh && chmod +x /tmp/x.sh && /tmp/x.sh
  cd "$PROJECT_DIR" && echo "pwd=$PWD"
  git status >/dev/null 2>/tmp/gs.err && echo git-status-ok || cat /tmp/gs.err
  touch "$PROJECT_DIR/created-inside"
  echo "gpgsign_effective=$(git config commit.gpgsign)"
')
has() { grep -qxF -- "$1" <<<"$out"; }
check "runs as host UID/GID ($PEN_UID:$PEN_GID)" has "id=$PEN_UID:$PEN_GID"
check "user agent, HOME=/home/agent" has "user=agent home=/home/agent"
for cap in CapInh CapPrm CapEff CapBnd CapAmb; do
  check "$cap empty" has "$cap:	0000000000000000"
done
check "no_new_privs set" has "NoNewPrivs:	1"
check "capsh: empty bounding set" grep -qE '^Bounding set =$' <<<"$out"
check "no sudo" has "sudo=absent"
check "PID 1 is tini, running as agent (no root process left)" has "pid1=tini pid1uid=$PEN_UID"
check "pids_limit applied" has "pids.max=4096"
check "new home volume owned by runtime UID" has "home_owner=$PEN_UID:$PEN_GID"
check "new NuGet volume owned by runtime UID" has "nuget_owner=$PEN_UID:$PEN_GID"
check "no docker.sock" has "docker.sock=absent"
check "/tmp is exec" has "tmp-exec-ok"
check "working dir is the project path" has "pwd=$PROJECT_DIR"
check "git status: no dubious-ownership error" has "git-status-ok"
check "file created inside is owned by the host user" \
  test "$(stat -c %u "$PROJECT_DIR/created-inside" 2>/dev/null || stat -f %u "$PROJECT_DIR/created-inside")" = "$PEN_UID"
check "a repo with commit.gpgsign=true still has signing off" has "gpgsign_effective=false"
rm -f "$PROJECT_DIR/created-inside"
git -C "$PROJECT_DIR" config --unset commit.gpgsign || true

# Only the project path and the NuGet path exist under the host's home root
# (/Users on the Mac, /home on Linux, where /home/agent is the sandbox's own).
home_root=/$(echo "$HOME" | cut -d/ -f2)
out=$(in_sandbox "find $home_root \\( -path /home/agent -o -path \"\$PROJECT_DIR\" -o -path \"\$NUGET_PACKAGES\" \\) -prune -o -print")
stray=
while IFS= read -r p; do
  [ -z "$p" ] && continue
  case "$PROJECT_DIR/" in "$p"/*) continue ;; esac
  case "$NUGET_PACKAGES/" in "$p"/*) continue ;; esac
  [ "$p" = /home/agent ] || [ "$p" = "$PROJECT_DIR" ] || [ "$p" = "$NUGET_PACKAGES" ] && continue
  stray+="$p "
done <<<"$out"
check "under $home_root only the project and NuGet paths exist${stray:+ (stray: $stray)}" test -z "$stray"

out=$(in_sandbox 'echo hi >"$HOME/.persist"'; in_sandbox 'cat "$HOME/.persist"')
check "home volume persists across sessions" test "$out" = hi

# ==============================================================================
section "3. Proxy and network (step 2)"

code() { in_sandbox "curl -s -o /dev/null -w '%{http_code} %{http_connect}' -I $1"; }
check "github.com → 200 through the proxy" test "$(code https://github.com | cut -d' ' -f1)" = 200
check "example.com → 403 from the proxy" test "$(code https://example.com | cut -d' ' -f2)" = 403
check "plain http://example.com → 403 from the proxy" test "$(code http://example.com | cut -d' ' -f1)" = 403
gh_ip=$(dig +short github.com A 2>/dev/null | grep -E '^[0-9.]+$' | head -1 || true)
[ -n "$gh_ip" ] || gh_ip=$(python3 -c 'import socket; print(socket.gethostbyname("github.com"))')
check "bare IP ($gh_ip) → 403 from the proxy" test "$(code "https://$gh_ip" | cut -d' ' -f2)" = 403
check "--noproxy '*' fails (fail-closed)" \
  not pen_run curl -s -m 10 -o /dev/null --noproxy '*' https://github.com
check "port other than 443 refused for CONNECT" test "$(code https://github.com:8443 | cut -d' ' -f2)" = 403

if in_sandbox 'getent hosts example.com' >/dev/null 2>&1; then
  note "DNS: external names RESOLVE inside the sandbox → keep the DNS-tunneling limitation in the README"
else
  note "DNS: external names do NOT resolve inside the sandbox → drop the DNS limitation from the README"
fi

in_sandbox 'curl -s -o /dev/null -I https://example.com; curl -s -o /dev/null -I https://github.com' >/dev/null 2>&1 || true
log=$(last_session)proxy.log
check "proxy.log records the denial (TCP_DENIED example.com:443)" grep -qE 'TCP_DENIED[A-Z_]*/403 CONNECT example.com:443' "$log"
check "proxy.log records the allowed request" grep -q 'CONNECT github.com:443' "$log"
check "proxy.log has no healthcheck probes" not grep -q ' 127.0.0.1 ' "$log"

fails=0
for i in $(seq 10); do
  c=$(pen_run curl -s -o /dev/null -w '%{http_code}' -I https://github.com || true)
  [ "$c" = 200 ] || fails=$((fails + 1))
done
check "first request after start succeeds 10/10 (proxy healthcheck)" test "$fails" = 0

# ==============================================================================
section "4. Agents and CodeGraph"

out=$(in_sandbox 'for c in claude codex opencode pi codegraph; do printf "%s=" $c; timeout 30 $c --version </dev/null 2>&1 | tail -1; done')
for c in claude codex opencode pi codegraph; do
  check "$c --version works as agent ($(grep "^$c=" <<<"$out" | cut -d= -f2-))" grep -qE "^$c=.*[0-9]+\.[0-9]+" <<<"$out"
done
check "agents live in system paths, not \$HOME" \
  in_sandbox 'for c in claude codex opencode pi codegraph; do case $(readlink -f "$(command -v $c)") in /usr/local/*|/opt/*) ;; *) exit 1 ;; esac; done'
check "agent user can't write the agents' install dir" \
  in_sandbox '! touch /usr/local/lib/node_modules/.x 2>/dev/null'
check "auto-updaters off (env)" \
  in_sandbox '[ "$DISABLE_AUTOUPDATER" = 1 ] && [ "$OPENCODE_DISABLE_AUTOUPDATE" = 1 ] && [ "$PI_SKIP_VERSION_CHECK" = 1 ] && grep -q "check_for_update_on_startup = false" /etc/codex/managed_config.toml'

# Trivial prompts, only when credentials are in the environment (otherwise log
# in inside a session by hand). They reach the agent as env. lines in a
# throwaway config, the way users pass them.
prompt_check() { # prompt_check <agent> <env var> <cmd…>
  local agent=$1 var=$2; shift 2
  if [ -z "${!var:-}" ]; then note "$agent prompt: skipped (set $var to run it)"; return; fi
  fake_config "env.$var = ${!var}"
  local out
  out=$(fake_pen run timeout 180 "$@" 2>&1 || true)
  check "$agent answers a trivial prompt through the proxy" grep -q 4 <<<"$out"
  local log; log=$(ls -td "$FAKE_HOME"/.local/state/pen/"$KEY"/sessions/*/ | head -1)proxy.log
  if grep -q TCP_DENIED "$log"; then
    note "$agent: denied → $(grep TCP_DENIED "$log" | awk '{print $5}' | sort -u | tr '\n' ' ')"
  fi
}
# The fixture project's home volume is fresh, so this also shows
# env.CLAUDE_CODE_OAUTH_TOKEN starts Claude logged in.
prompt_check claude CLAUDE_CODE_OAUTH_TOKEN claude -p 'What is 2+2? Reply with just the digit.'
prompt_check codex OPENAI_API_KEY codex exec --skip-git-repo-check 'What is 2+2? Reply with just the digit.'

if [ "${SMOKE_SLOW:-}" = 1 ]; then
  v1=$(docker run --rm --entrypoint stat "$IMAGE" -c %Y /usr/local/lib/node_modules/@anthropic-ai/claude-code/package.json)
  "$PEN" upgrade >"$SCRATCH/upgrade.log" 2>&1 || { tail -20 "$SCRATCH/upgrade.log" >&2; fail "pen upgrade"; }
  v2=$(docker run --rm --entrypoint stat "$IMAGE" -c %Y /usr/local/lib/node_modules/@anthropic-ai/claude-code/package.json)
  check "pen upgrade reinstalls the agents (PEN_REFRESH), base image unchanged or not" test "$v1" != "$v2"
  check "pen upgrade pulls the proxy image" grep -q 'ubuntu/squid' "$SCRATCH/upgrade.log"
else
  note "pen upgrade check: skipped (SMOKE_SLOW=1 to run it)"
fi

# ==============================================================================
section "5. CLI: install, dispatch, directory rules"

PTY="python3 $REPO/tests/pty-run.py"
mode_of() { stat -c %a "$1" 2>/dev/null || stat -f %Lp "$1"; }
# wait_for <docker name regex>: until such a container is running (max 30 s).
wait_for() {
  local i
  for i in $(seq 300); do
    docker ps --format '{{.Names}}' | grep -qE "$1" && return 0
    sleep 0.1
  done
  return 1
}
has_all() { local h=$1 n; shift; for n; do contains "$n" "$h" || return 1; done; }
lock_free() { # lock_free <file> <exclusive|shared>: can another process take it now?
  python3 -c '
import fcntl, sys
f = open(sys.argv[1], "a")
try:
    fcntl.flock(f, (fcntl.LOCK_EX if sys.argv[2] == "exclusive" else fcntl.LOCK_SH) | fcntl.LOCK_NB)
except BlockingIOError:
    sys.exit(1)' "$1" "$2"
}

check "pen --version" bash -c "'$PEN' --version | grep -qE '^pen [0-9]'"
if command -v fish >/dev/null; then
  check "pen runs from fish" fish -c "'$PEN' --version" >/dev/null
else
  note "fish not installed: skipped the fish check"
fi
check "pen --help lists the subcommands" bash -c "'$PEN' --help | grep -q 'pen allow' && '$PEN' --help | grep -q 'pen upgrade'"
rc=0; "$PEN" --bogus >/dev/null 2>&1 || rc=$?
check "an unknown option is a usage error (exit 2)" test "$rc" = 2
rc=0; "$PEN" run >/dev/null 2>&1 || rc=$?
check "pen run without a command is a usage error (exit 2)" test "$rc" = 2
check "a not-yet-built subcommand says so" bash -c "'$PEN' sign 2>&1 | grep -q \"isn't implemented yet\""

refuse() { # refuse <dir> <expected message part>
  local out rc=0
  out=$(cd "$1" && "$PEN" run true </dev/null 2>&1) || rc=$?
  [ "$rc" != 0 ] && contains "$2" "$out"
}
check "refuses \$HOME" refuse "$HOME" "home directory"
check "refuses /" refuse / "refusing to run in /"
check "refuses an ancestor of \$HOME" refuse "$(dirname "$HOME")" "contains your home directory"
check "refuses ~/.config (contains ~/.config/pen)" refuse "$HOME/.config" "~/.config/pen"
mkdir -p "$PROJECT_DIR/sub/dir"
check "refuses a repo subdirectory, naming the root" refuse "$PROJECT_DIR/sub/dir" "inside the git repo at $PROJECT_DIR"
rmdir "$PROJECT_DIR/sub/dir" "$PROJECT_DIR/sub"
ln -s "$PROJECT_DIR" "$SCRATCH/link"
check "a symlinked path maps to the same project key" \
  bash -c "cd '$SCRATCH/link' && '$PEN' run true </dev/null >/dev/null 2>&1 && [ \$(ls '$RUNTIME/sessions' | wc -l) -ge 1 ] && [ \$(ls -d '$HOME'/.local/state/pen/pen-*-$HASH | wc -l) = 1 ]"

# ==============================================================================
section "6. Sessions"

rc=0; pen_run sh -c 'exit 7' 2>/dev/null || rc=$?
check "the command's exit code passes through" test "$rc" = 7
check "pen run ls | head works without a TTY" test "$("$PEN" run ls -a </dev/null 2>/dev/null | head -1)" = .
out=$($PTY -- "$PEN" run tty)
check "with a terminal, the agent gets one too" contains "/dev/pts/" "$out"
out=$($PTY -- "$PEN" claude --version)
check "pen claude runs claude" grep -qE '[0-9]+\.[0-9]+\.[0-9]+' <<<"$out"
out=$($PTY --send-on '\$ $' 'echo shell-$((6*7)); exit\r' --timeout 60 -- "$PEN")
check "pen with no arguments is an interactive shell" contains "shell-42" "$out"

pen_run bash -c 'sleep 8; curl -s -o /dev/null -w "after:%{http_code}" -I https://github.com' >"$SCRATCH/first.out" 2>&1 &
first=$!
wait_for "^$KEY-.*-agent$" || fail "first session didn't start"
check "a second session runs alongside the first" \
  test "$(in_sandbox 'curl -s -o /dev/null -w %{http_code} -I https://github.com' 2>/dev/null)" = 200
wait "$first" || true
check "the first still works after the second exits" grep -q 'after:200' "$SCRATCH/first.out"
check "docker ps is clean after the last session exits" test "$(containers)" = 0
check "no session network is left" test -z "$(docker network ls -q --filter "name=^$KEY-")"
s=$(last_session)
check "an ended session keeps only its proxy.log" test "$(ls -A "$s")" = proxy.log
check "runtime and session dirs are 700" bash -c "[ $(mode_of "$RUNTIME") = 700 ] && [ $(mode_of "$s") = 700 ] && [ $(mode_of "$RUNTIME/sessions") = 700 ]"
check "proxy.log is 666" test "$(mode_of "${s}proxy.log")" = 666
check "the project's volumes are named by its key" docker volume inspect "${KEY}_home" "${KEY}_nuget" >/dev/null
check "pen:latest carries its image hash" test -n "$(docker image inspect --format '{{ index .Config.Labels "pen.image-hash" }}' "$IMAGE")"

# ==============================================================================
section "7. Locks, dead sessions, signals"

"$PEN" run sleep 120 </dev/null >/dev/null 2>&1 &
p=$!
wait_for "^$KEY-.*-agent$" || fail "session didn't start"
check "project.lock is held shared: another shared lock is fine" lock_free "$RUNTIME/project.lock" shared
check "...and an exclusive one (pen sign/reset) is refused" not lock_free "$RUNTIME/project.lock" exclusive
sdir=$(ls -td "$RUNTIME"/sessions/*/ | head -1)
check "the running session's session.lock is held" not lock_free "${sdir}session.lock" exclusive
compose_pid=$(pgrep -P "$p" | head -1)
if command -v lsof >/dev/null; then
  check "the compose child holds no lock fds (lsof)" not bash -c "lsof -p $compose_pid 2>/dev/null | grep -q '\.lock'"
  check "while pen itself holds both locks" test "$(lsof -p "$p" 2>/dev/null | grep -c '\.lock$')" = 2
else
  note "lsof not installed: skipped the fd checks"
fi
kill -9 "$p"; wait "$p" 2>/dev/null || true
check "after a SIGKILL'd pen, its stack is still up" test "$(containers)" -ge 2
check "...and its session.lock is free" lock_free "${sdir}session.lock" exclusive
out=$(pen_run true 2>&1)
check "the next pen ends the dead session" contains "whose pen exited without ending it" "$out"
check "...taking its stack down" test "$(containers)" = 0
check "...and leaving only its proxy.log" test "$(ls -A "$sdir")" = proxy.log

"$PEN" run sleep 120 </dev/null >/dev/null 2>&1 &
p=$!
wait_for "^$KEY-.*-agent$" || fail "session didn't start"
kill -TERM "$p"
wait "$p" 2>/dev/null || true
check "SIGTERM: pen removes the container and tears down the stack" test "$(containers)" = 0

out=$($PTY --hangup-on READY -- "$PEN" run bash -c 'echo READY; sleep 60')
check "closing the terminal ends the session" contains "AFTER-HANGUP" "$out"
check "...within 10 s" test "$(awk '/^AFTER-HANGUP/ { print ($2 < 10) }' <<<"$out")" = 1
check "...with the container removed and the stack down" test "$(containers)" = 0

# (bash ends a loop whose child dies of the signal, so each signal gets its own loop.)
out=$($PTY --send-on READY1 '\x03' --send-on READY2 '\x1c' -- "$PEN" run bash -c 'trap "echo GOT-INT" INT; trap "echo GOT-QUIT" QUIT; wait_a_bit() { for i in $(seq 25); do sleep 0.2; done; }; echo READY1; wait_a_bit; echo READY2; wait_a_bit; echo DONE')
check "Ctrl-C reaches the agent, not pen" contains "GOT-INT" "$out"
check "Ctrl-\\ reaches the agent, not pen" contains "GOT-QUIT" "$out"
check "...and the session carries on to a normal exit" has_all "$out" DONE "EXIT 0"

# Ctrl-C during the automatic image build: make pen:latest stale (same image,
# wrong label), interrupt the rebuild, then put the real image back.
docker image tag "$IMAGE" pen:smoke-backup
echo 'FROM pen:smoke-backup' | docker build -q --label pen.image-hash=stale -t "$IMAGE" - >/dev/null
before=$(ls "$RUNTIME/sessions" | wc -l)
out=$($PTY --send-on 'rebuilding' '\x03' -- "$PEN" run true)
docker image tag pen:smoke-backup "$IMAGE" && docker rmi pen:smoke-backup >/dev/null
check "Ctrl-C during the image build aborts the launch" bash -c 'grep -q rebuilding <<<"$1" && ! grep -qE "EXIT 0|TIMEOUT" <<<"$1"' _ "$out"
check "...leaving no session dir" test "$(ls "$RUNTIME/sessions" | wc -l)" = "$before"
check "...and no lock held" lock_free "$RUNTIME/project.lock" exclusive

# ==============================================================================
section "8. Config file"

out=$(fake_pen run true 2>&1)
cfg=$FAKE_HOME/.config/pen/config
check "first run creates ~/.config/pen/config" test -f "$cfg"
check "...mode 600" test "$(mode_of "$cfg")" = 600
check "...with every key commented out" not grep -qvE '^[[:space:]]*(#|$)' "$cfg"
check "...and says so in one line" test "$(grep -c 'created ~/.config/pen/' <<<"$out")" = 1
sum=$(cksum <"$cfg")
out=$(fake_pen run true 2>&1)
check "a second run says nothing about config" not contains "created" "$out"
check "...and changes nothing" test "$(cksum <"$cfg")" = "$sum"

fake_config "# comment" "allw = example.com"
check "a misspelled key is reported with its file and line" \
  contains '~/.config/pen/config:2: unknown key "allw"' "$(fake_pen run true 2>&1 || true)"

secret="smoke-$$-$RANDOM-secret"
fake_config "env.SMOKE_SECRET = $secret"
fake_pen run bash -c 'sleep 4; echo "[$SMOKE_SECRET]"' >"$SCRATCH/env.out" 2>&1 &
p=$!
wait_for "^$KEY-.*-agent$" || fail "session didn't start"
check "an env. value never appears in any process's arguments" not bash -c "ps -axww -o args= | grep -v grep | grep -qF '$secret'"
wait "$p" || true
check "...and reaches the agent" grep -qF "[$secret]" "$SCRATCH/env.out"

mkdir -p .pen
echo "env.FOO = x" >.pen/config
check "an env. key in .pen/config is refused with its file and line" contains ".pen/config:1:" "$(pen_run true 2>&1 || true)"
echo "github.acme = x" >.pen/config
check "a github. key in .pen/config is refused with its file and line" contains ".pen/config:1:" "$(pen_run true 2>&1 || true)"
rm -rf .pen

# ==============================================================================
section "9. GitHub token"

owner=pen-smoke-owner
git remote add origin "git@github.com:$owner/repo.git"
fake_config "# none"
rm -f "$FAKE_HOME/.local/state/pen/$KEY/token-notice"
out=$(fake_pen run true 2>&1)
check "no token for the origin's owner: a notice with the line to add" contains "no GitHub token for '$owner': add \"github.$owner = github_pat_" "$out"
check "...only on the project's first session" not contains "no GitHub token" "$(fake_pen run true 2>&1)"
fake_config "github.$owner = ghp_smoke_fake_token"
out=$(fake_pen run bash -c 'echo "[$GH_TOKEN]"' 2>&1)
check "the owner's token reaches the agent as GH_TOKEN" contains "[ghp_smoke_fake_token]" "$out"
check "...with no notice" not contains "no GitHub token" "$out"
fake_config "# removed again"
check "removing the token later shows the notice again" contains "no GitHub token for '$owner'" "$(fake_pen run true 2>&1)"
mkdir -p .pen
echo "github_owner = other-owner" >.pen/config
fake_config "github.other-owner = ghp_other"
check "github_owner in .pen/config picks the owner" contains "[ghp_other]" "$(fake_pen run bash -c 'echo "[$GH_TOKEN]"' 2>&1)"
rm -rf .pen
fake_config "# none"
out=$(HOME=$FAKE_HOME DOCKER_CONFIG=${DOCKER_CONFIG:-$REAL_HOME/.docker} $PTY --timeout 60 -- "$PEN" run bash -c "git ls-remote https://github.com/$owner/private-$$; echo rc=\$?")
check "a private fetch with no token fails with an error instead of prompting" \
  bash -c 'grep -qE "rc=[1-9]" <<<"$1" && grep -q "EXIT 0" <<<"$1"' _ "$out"
git remote remove origin
note "real-token checks (gh repo view, clone, push 403, per-owner tokens) are manual: see PLAN.md Verification step 3"

# ==============================================================================
section "10. Allowlist merge and the blocked line"

out=$(in_sandbox 'curl -s -o /dev/null -I https://example.com' 2>&1 || true)
check "session end prints the blocked line" contains "blocked: example.com → pen allow example.com (add --global for all projects)" "$out"
out=$(in_sandbox 'curl -s -o /dev/null -I https://github.com' 2>&1 || true)
check "...and nothing when nothing was blocked" not contains "blocked:" "$out"
mkdir -p .pen
echo "allow = example.com" >.pen/config
check "a project allow line becomes reachable" \
  test "$(in_sandbox 'curl -s -o /dev/null -w %{http_code} -I https://example.com' 2>/dev/null)" = 200
rm -rf .pen
fake_config "allow = .github.com"
fake_pen run bash -c 'sleep 5; curl -s -o /dev/null -w "%{http_code}" -I https://api.github.com' >"$SCRATCH/wild.out" 2>/dev/null &
p=$!
wait_for "^$KEY-.*-proxy-1$" || fail "proxy didn't start"
sleep 2
proxy_log=$(docker logs "$(docker ps --format '{{.Names}}' | grep -E "^$KEY-.*-proxy-1$" | head -1)" 2>&1)
wait "$p" || true
# Squid reports overlaps while parsing its config, e.g.
# "WARNING: 'api.github.com' is a subdomain of '.github.com'".
check ".github.com plus the default api.github.com: the proxy reports no overlap" \
  bash -c 'grep -q "Processing Configuration File" <<<"$1" && ! grep -qE "is a subdomain of|You should remove" <<<"$1"' _ "$proxy_log"
check "...and api.github.com is reachable" test "$(cat "$SCRATCH/wild.out")" = 200

# ==============================================================================
section "11. .NET and NuGet (step 7)"

out=$(in_sandbox 'mkdir -p app && cd app && dotnet new console -o . --force >/dev/null && dotnet add package Humanizer.Core >/dev/null && dotnet run' 2>&1 || true)
check "dotnet new console, a NuGet restore and dotnet run work" contains "Hello, World!" "$out"
out=$(in_sandbox 'cd app && rm -rf obj bin && dotnet restore -v d' 2>&1 || true)
check "a second restore finds the package in the cache volume (no download)" not grep -qiE 'GET https://[^ ]*humanizer' <<<"$out"
check "NUGET_PACKAGES is the host's path, backed by the project's volume" \
  test "$(in_sandbox 'echo $NUGET_PACKAGES; ls $NUGET_PACKAGES' 2>/dev/null | head -2 | tr '\n' ' ')" = "$NUGET_PACKAGES humanizer.core "
if command -v dotnet >/dev/null; then
  check "host build and run after the sandbox's build" bash -c "cd app && dotnet build -v q >/dev/null 2>&1 && dotnet run --no-build | grep -q 'Hello, World!'"
  check "...then the sandbox builds again" contains "Hello, World!" "$(in_sandbox 'cd app && dotnet build -v q >/dev/null && dotnet run --no-build' 2>&1)"
else
  note "no .NET SDK on the host: skipped the host/sandbox round trip"
fi
rm -rf app

note "login checks (step 5: log in to claude and codex, still logged in after pen upgrade --clean, logged out in another project) are manual"

# ==============================================================================
section "12. Repo refusals at start (step 4)"

now() { python3 -c 'import time; print(time.time())'; }
git worktree add -q "$SCRATCH/wt" 2>/dev/null # kept until section 13 (its commondir is mounted read-only)
check "refuses a linked worktree, pointing at the main checkout" refuse "$SCRATCH/wt" "run pen from the main checkout"
forget "$SCRATCH/wt"
touch .gitmodules
check "refuses a repo with .gitmodules (not supported yet)" refuse "$PROJECT_DIR" "submodules, which pen doesn't support yet"
rm .gitmodules
git init -q libs/sub
git init -q --bare sub2
out=$(pen_run true 2>&1 || true)
check "refuses a repo with another repo inside it, naming it" contains "  libs/sub" "$out"
check "...and a bare repo inside it" contains "  sub2 (a bare repo)" "$out"
check "...saying to run pen in that repo" contains "run pen in each repo instead" "$out"
check "...without starting anything" test "$(containers)" = 0
rm -rf libs sub2
mkdir -p "$SCRATCH/repos"
git init -q "$SCRATCH/repos/a"; git init -q "$SCRATCH/repos/b"
out=$(cd "$SCRATCH/repos" && "$PEN" run true </dev/null 2>&1 || true)
check "refuses a folder of repos, naming them" has_all "$out" "contains git repos" "  a" "  b"
forget "$SCRATCH/repos"
git worktree add -q .claude/worktrees/x 2>/dev/null
check "a git worktree add worktree under .claude/worktrees/ starts normally" pen_run true
git worktree remove --force .claude/worktrees/x && rm -rf .claude

# The scan with a 100k-file node_modules and obj/ trees: a repo planted at
# the bottom makes pen refuse right after the scan, before any Docker call,
# so the refusal's time is the scan's (plus pen's own startup).
python3 - "$PROJECT_DIR" <<'PY'
import os, sys
root = sys.argv[1]
for top, dirs, files in (("node_modules", 5000, 20), ("src/obj", 500, 10)):
    for d in range(dirs):
        p = os.path.join(root, top, f"pkg{d}", "lib" if d % 2 else "dist")
        os.makedirs(p, exist_ok=True)
        for f in range(files):
            open(os.path.join(p, f"f{f}.js"), "w").close()
PY
git init -q node_modules/pkg4999/dist/deep
t0=$(now); out=$(pen_run true 2>&1 || true); t1=$(now)
scan=$(python3 -c "print(f'{$t1 - $t0:.2f}')")
check "the nested-repo scan finds a repo deep in node_modules" contains "node_modules/pkg4999/dist/deep" "$out"
check "...in under about 1 s with 100k+ files (took ${scan}s)" python3 -c "import sys; sys.exit(0 if $scan < 1.2 else 1)"
rm -rf node_modules/pkg4999/dist/deep/.git
t0=$(now); pen_run true; t1=$(now)
note "a whole session with that tree (start and end scans included): $(python3 -c "print(f'{$t1 - $t0:.2f}')")s"
rm -rf node_modules src

# ==============================================================================
section "13. Read-only .git and .pen, session-end check and quarantine (step 4)"

rm -rf .git/hooks .pen
cfg_sum=$(cksum <.git/config)
out=$(in_sandbox '
  t() { if eval "$1" >/dev/null 2>&1; then echo "ok: $2"; else echo "fail: $2"; fi; }
  t "git -c user.name=a -c user.email=a@example.invalid commit -q --allow-empty -m inside" commit
  t "git checkout -q -b scratch && git checkout -q - && git merge -q --ff-only scratch && git stash list" branchwork
  t "echo x >> .git/hooks/pre-commit" hook
  t "git config core.fsmonitor x" config
  t "echo x >> .git/config" configwrite
  t "mv .git .git2" mvgit
  t "echo x > .git/worktrees/wt/commondir" wtcommondir
  t "touch .pen/config" pencreate
  t "mv .pen .pen2" mvpen
' 2>&1)
has() { grep -qxF -- "$1" <<<"$out"; }
check "git commit works" has "ok: commit"
check "branch, checkout, merge and stash work" has "ok: branchwork"
check "the session that only committed printed no warning" not contains "WARNING" "$out"
check ".git/hooks is created when missing" test -d .git/hooks
check "...and writing a hook fails" has "fail: hook"
check "git config core.fsmonitor fails" has "fail: config"
check "appending to .git/config fails" has "fail: configwrite"
check "...and .git/config is unchanged on the host" test "$(cksum <.git/config)" = "$cfg_sum"
check "mv .git .git2 fails" has "fail: mvgit"
check "a host worktree's commondir is read-only" has "fail: wtcommondir"
check ".pen/ is created empty when missing" bash -c '[ -d .pen ] && [ -z "$(ls -A .pen)" ]'
check "...touch .pen/config fails inside" has "fail: pencreate"
check "...mv .pen fails" has "fail: mvpen"
check "...and host git status shows nothing new" test -z "$(git status --porcelain)"
git worktree remove --force "$SCRATCH/wt"
git branch -q -D scratch

echo '# host-written' >.pen/config
out=$(in_sandbox 'echo x >> .pen/config && echo wrote; mv .pen .pen2 && echo moved; true' 2>&1)
check "with .pen/config present, it can't be written or moved" bash -c '! grep -qE "^(wrote|moved)$" <<<"$1"' _ "$out"
check "...and is unchanged" test "$(cat .pen/config)" = '# host-written'
rm .pen/config

: >.gitinc
git config include.path ../.gitinc
out=$(in_sandbox 'echo "[core] fsmonitor = x" >> .gitinc && echo wrote; true' 2>&1)
check "a config file .git/config includes from the project is read-only" bash -c '! grep -qx wrote <<<"$1" && [ ! -s .gitinc ]' _ "$out"
git config --unset include.path; rm .gitinc

quarantine_case() { # <what the agent runs> <path> <quarantined path>
  local out
  out=$(in_sandbox "$1" 2>&1 || true)
  check "$1: warned at session end" contains "pen: WARNING" "$out"
  check "...and moved to $3 before pen returned" bash -c "[ ! -e '$2' ] && [ -e '$3' ]"
  check "...with how to put it back" contains "mv '$PROJECT_DIR/$3' '$PROJECT_DIR/$2'" "$out"
}
quarantine_case 'touch .git/commondir' .git/commondir .git/commondir.pen-quarantine
quarantine_case 'mkdir .git/modules' .git/modules .git/modules.pen-quarantine
quarantine_case 'git init -q sub' sub/.git sub/.git.pen-quarantine
check "...and the moved .git no longer looks like a repo (its HEAD moved too)" test -e sub/.git.pen-quarantine/HEAD.pen-quarantine
quarantine_case 'git init -q --bare sub2' sub2/HEAD sub2/HEAD.pen-quarantine
check "the next session starts normally" pen_run true
rm -rf .git/commondir.pen-quarantine .git/modules.pen-quarantine sub sub2

out=$(in_sandbox 'git worktree add -q .claude/worktrees/y' 2>&1)
check "no warning for a git worktree add worktree" not contains "WARNING" "$out"
check "...and the next session starts normally" pen_run true
git worktree remove --force .claude/worktrees/y && rm -rf .claude

"$PEN" run bash -c 'touch .git/commondir; sleep 120' </dev/null >/dev/null 2>&1 &
p=$!
wait_for "^$KEY-.*-agent$" || fail "session didn't start"
for i in $(seq 100); do [ -e .git/commondir ] && break; sleep 0.1; done
kill -9 "$p"; wait "$p" 2>/dev/null || true
check "after a SIGKILL'd pen, .git/commondir is still there and its stack is up" bash -c "[ -e .git/commondir ] && [ $(containers) -ge 2 ]"
out=$(pen_run true 2>&1)
check "the next pen command ends that session" contains "whose pen exited without ending it" "$out"
check "...quarantining .git/commondir" bash -c '[ ! -e .git/commondir ] && [ -e .git/commondir.pen-quarantine ]'
check "...with the warning" contains "pen: WARNING" "$out"
check "...and takes the stack down" test "$(containers)" = 0
rm -f .git/commondir.pen-quarantine

NOGIT=$(cd "$(mktemp -d "$HOME/.pen-smoke-nogit.XXXXXX")" && pwd -P)
EXTRA_DIRS+=("$NOGIT")
out=$(cd "$NOGIT" && "$PEN" run git init -q </dev/null 2>&1 || true)
check "a .git made by git init in a project without one is quarantined" \
  bash -c "[ ! -e '$NOGIT/.git' ] && [ -d '$NOGIT/.git.pen-quarantine' ]"
check "...with the warning" contains "git init" "$out"
check "...and a project without .git gets no .git mounts" bash -c "cd '$NOGIT' && rm -rf .git.pen-quarantine && '$PEN' run bash -c '[ ! -e .git ] && ! touch .pen/x' </dev/null"

best=99
for i in 1 2 3; do
  t0=$(now); pen_run true; t1=$(now)
  best=$(python3 -c "print(f'{min($best, $t1 - $t0):.2f}')")
done
check "warm start: pen run true in under 5 s (best of 3: ${best}s; target about 2-3 s)" python3 -c "import sys; sys.exit(0 if $best < 5 else 1)"

section "All checks passed."
