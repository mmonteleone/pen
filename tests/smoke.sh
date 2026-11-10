#!/usr/bin/env bash
# Container smoke checks for Pen, accumulated chunk by chunk. Run on both the
# Mac (OrbStack/arm64) and the server (Docker/amd64), against the installed Pen
# (cli/install.sh). Exits non-zero on the first failure.
#
#   tests/smoke.sh                 all checks
#   SMOKE_SLOW=1 tests/smoke.sh    also `pen upgrade` (reinstalls the agents, ~1 min)
#   SMOKE_ONLY="7 13" tests/smoke.sh
#                                  only those sections (the fixture setup always runs)
#
# Optional, for checks that need real credentials (values are only ever written
# to a throwaway config file, mode 600, removed on exit):
#   CLAUDE_CODE_OAUTH_TOKEN, OPENAI_API_KEY   trivial prompts in claude / codex, and
#                                             the seeded-behavior checks (section 17)
#
# Config-dependent checks run Pen with a throwaway HOME, so your own
# ~/.config/pen is never changed. Everything else uses your real HOME (Pen
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
# want <n>: whether section n runs (all of them unless SMOKE_ONLY lists some).
want() { [ -z "${SMOKE_ONLY:-}" ] || [[ " ${SMOKE_ONLY//,/ } " == *" $1 "* ]]; }

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
# The throwaway HOME's global git identity, which the first run copies into its seed.
printf '[user]\n\tname = Smoke Host\n\temail = smoke-host@example.invalid\n' >"$FAKE_HOME/.gitconfig"
export NUGET_PACKAGES=${NUGET_PACKAGES:-$HOME/.nuget/packages}
PEN_UID=$(id -u) PEN_GID=$(id -g)
# Under rootless Docker the host user is container root, so the agent runs as
# UID 0 inside (with no capabilities, on a read-only root filesystem).
if docker info --format '{{json .SecurityOptions}}' | grep -q 'name=rootless'; then
  ROOTLESS=1 AGENT_UID=0 AGENT_GID=0 AGENT_USER=root
else
  ROOTLESS= AGENT_UID=$PEN_UID AGENT_GID=$PEN_GID AGENT_USER=agent
fi
if command -v sha256sum >/dev/null; then sha() { sha256sum; }; else sha() { shasum -a 256; }; fi
HASH=$(printf %s "$PROJECT_DIR" | sha | cut -c1-8)
KEY= RUNTIME=
EXTRA_DIRS=() # other fixture projects: removed on exit, with their keys' state
EXTRA_KEYS=() # project keys whose dirs a section deleted: their state is removed on exit
TOOL_IMAGES=() # other images a section made up: removed on exit
# Sessions build pen-tools images: any not here at the start go on exit.
TOOLS_BEFORE=$(docker image ls --format '{{.Repository}}:{{.Tag}}' --filter reference=pen-tools | sort)

# forget_key <key>: remove a project key's containers, networks, volumes, images
# and runtime dirs (under the real and the throwaway HOME).
forget_key() {
  local k=$1
  docker ps -aq --filter "name=^$k-" | xargs docker rm -f >/dev/null 2>&1 || true
  docker network ls -q --filter "name=^$k-" | xargs docker network rm >/dev/null 2>&1 || true
  docker volume rm "${k}_home" "${k}_nuget" "${k}_dind" >/dev/null 2>&1 || true
  docker image ls -q --filter "reference=$k*" | xargs docker image rm -f >/dev/null 2>&1 || true
  rm -rf "$REAL_HOME/.local/state/pen/$k" "$FAKE_HOME/.local/state/pen/$k"
}
# forget <dir>: remove the state of any project key made for <dir>, by Pen
# with either HOME.
forget() {
  local h k
  h=$(printf %s "$(cd "$1" && pwd -P)" | sha | cut -c1-8)
  for k in "$REAL_HOME"/.local/state/pen/pen-*-"$h" "$FAKE_HOME"/.local/state/pen/pen-*-"$h"; do
    if [ -d "$k" ]; then forget_key "$(basename "$k")"; fi
  done
}

cleanup() {
  cd /
  if [ -n "$KEY" ]; then
    docker ps -aq --filter "name=^$KEY-" | xargs docker rm -f >/dev/null 2>&1 || true
    docker network ls -q --filter "name=^$KEY-" | xargs docker network rm >/dev/null 2>&1 || true
    docker volume rm "${KEY}_home" "${KEY}_nuget" "${KEY}_dind" >/dev/null 2>&1 || true
    docker image ls -q --filter "reference=$KEY*" | xargs docker image rm -f >/dev/null 2>&1 || true
    rm -rf "$REAL_HOME/.local/state/pen/$KEY"
  fi
  local d
  for d in ${EXTRA_DIRS[@]+"${EXTRA_DIRS[@]}"}; do [ -d "$d" ] && forget "$d"; rm -rf "$d"; done
  for d in ${EXTRA_KEYS[@]+"${EXTRA_KEYS[@]}"}; do forget_key "$d"; done
  for d in ${TOOL_IMAGES[@]+"${TOOL_IMAGES[@]}"}; do docker image rm -f "$d" >/dev/null 2>&1 || true; done
  docker image ls --format '{{.Repository}}:{{.Tag}}' --filter reference=pen-tools | sort | comm -13 <(printf '%s\n' "$TOOLS_BEFORE") - |
    xargs docker image rm -f >/dev/null 2>&1 || true
  rm -rf "$PROJECT_DIR" "$SCRATCH"
}
trap cleanup EXIT

git -C "$PROJECT_DIR" init -q
git -C "$PROJECT_DIR" -c user.name=smoke -c user.email=smoke@example.invalid -c commit.gpgsign=false commit -q --allow-empty -m init
cd "$PROJECT_DIR"

# pen_run <cmd…>: one whole session without a TTY, like `pen run <cmd…> </dev/null`.
pen_run() { "$PEN" run "$@" </dev/null; }
# in_sandbox <bash script>: run a script in a fresh session, as agent.
in_sandbox() { pen_run bash -c "$1"; }
# fake_pen <args…>: Pen with the throwaway HOME (its config in $FAKE_HOME/.config/pen).
fake_pen() { HOME=$FAKE_HOME DOCKER_CONFIG=${DOCKER_CONFIG:-$REAL_HOME/.docker} "$PEN" "$@" </dev/null; }
# fake_config <line…>: the throwaway config.toml, replacing it.
fake_config() { mkdir -p "$FAKE_HOME/.config/pen"; printf '%s\n' "$@" >"$FAKE_HOME/.config/pen/config.toml"; chmod 600 "$FAKE_HOME/.config/pen/config.toml"; }
# The newest session's dir (under the real HOME's runtime dir).
last_session() { ls -td "$RUNTIME"/sessions/*/ | head -1; }
# tools_cell <session dir>: the regex for its TOOLS cell in pen ps, followed by
# the separating spaces: the tools it recorded (the user's ones), or nothing.
tools_cell() { local t; t=$(paste -sd, "$1/tools" | sed 's/[.]/\\./g'); echo "${t:+$t +}"; }
containers() { docker ps -aq --filter "name=^$KEY-" | wc -l | tr -d ' '; }
PTY="python3 $REPO/tests/pty-run.py"
mode_of() { stat -c %a "$1" 2>/dev/null || stat -f %Lp "$1"; }
# wait_count <docker name regex> <n>: until n such containers are running (max 30 s).
wait_count() {
  local i
  for i in $(seq 300); do
    [ "$(docker ps --format '{{.Names}}' | grep -cE "$1")" -ge "$2" ] && return 0
    sleep 0.1
  done
  return 1
}
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
# holding <file> <exclusive|shared> <cmd…>: run cmd (stdin closed) while another process holds that
# lock; prints its exit code and stderr.
holding() {
  python3 -c '
import fcntl, subprocess, sys
f = open(sys.argv[1], "a")
fcntl.flock(f, fcntl.LOCK_EX if sys.argv[2] == "exclusive" else fcntl.LOCK_SH)
r = subprocess.run(sys.argv[3:], stdin=subprocess.DEVNULL, capture_output=True, text=True)
print(r.returncode, r.stderr)' "$@"
}
refuse() { # refuse <dir> <expected message part>
  local out rc=0
  out=$(cd "$1" && "$PEN" run true </dev/null 2>&1) || rc=$?
  [ "$rc" != 0 ] && contains "$2" "$out"
}
now() { python3 -c 'import time; print(time.time())'; }
# A docker that records each image build, and the image each session runs, so "no rebuild"
# is seen rather than guessed from timing.
TOOLS_SHIM=$SCRATCH/tools-shim; mkdir -p "$TOOLS_SHIM"; : >"$TOOLS_SHIM/builds"
cat >"$TOOLS_SHIM/docker" <<EOF
#!/bin/sh
if [ "\$1" = build ]; then echo build >>"$TOOLS_SHIM/builds"; fi
case " \$* " in *" run --rm "*) printf '%s\n' "\$PEN_IMAGE" >"$TOOLS_SHIM/image" ;; esac
exec "$(command -v docker)" "\$@"
EOF
chmod +x "$TOOLS_SHIM/docker"
# session_image <Pen options…>: starts a session with them, and prints the image it ran.
session_image() {
  PATH="$TOOLS_SHIM:$PATH" "$PEN" "$@" run true </dev/null >/dev/null 2>&1 || fail "pen $* run true"
  cat "$TOOLS_SHIM/image"
}
builds() { wc -l <"$TOOLS_SHIM/builds" | tr -d ' '; }
# The throwaway HOME's seed (section 8 checks its creation; section 17 adds to it).
seed=$FAKE_HOME/.config/pen/home

# The first session builds pen:latest if it's missing or stale, and fixes the project key.
"$PEN" run true </dev/null 2>&1 | grep '^pen:' || true
RUNTIME=$(ls -d "$HOME"/.local/state/pen/pen-*-"$HASH")
KEY=$(basename "$RUNTIME")

# Tools in the real ~/.config/pen/config.toml go into every session there: their
# programs, env and domains, and the image (pen-tools:…, and a project layer's tag).
# A section that asserts what a session without tools gets runs between tool_free,
# which points $PEN and $RUNTIME at the throwaway HOME with no config, and real_home.
PEN_REAL=$PEN RUNTIME_REAL=$RUNTIME
TOOL_FREE_PEN=$SCRATCH/pen-tool-free
printf '#!/bin/sh\nHOME=%q DOCKER_CONFIG=%q exec %q "$@"\n' \
  "$FAKE_HOME" "${DOCKER_CONFIG:-$REAL_HOME/.docker}" "$PEN" >"$TOOL_FREE_PEN"
chmod +x "$TOOL_FREE_PEN"
tool_free() { fake_config "# none"; PEN=$TOOL_FREE_PEN; RUNTIME=$FAKE_HOME/.local/state/pen/$KEY; }
real_home() { PEN=$PEN_REAL; RUNTIME=$RUNTIME_REAL; }

if want 1; then
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
check "tools present (git gh rg fd jq curl wget xz openssl less vim unzip make python3 uv tini capsh docker 'docker compose')" \
  docker run --rm --entrypoint bash "$IMAGE" -c 'for c in git gh rg fd jq curl wget xz openssl less vim unzip make gcc python3 uv uvx tini capsh setpriv docker; do command -v $c >/dev/null || { echo "missing $c" >&2; exit 1; }; done; docker compose version >/dev/null'
check "no node, npm or npx on PATH in the base" \
  docker run --rm --entrypoint bash "$IMAGE" -c '! command -v node && ! command -v npm && ! command -v npx && ! command -v corepack'
check "Pen's private Node runs (/opt/pen/node/bin/node)" \
  docker run --rm --entrypoint /opt/pen/node/bin/node "$IMAGE" -e 'process.exit(process.version.startsWith("v") ? 0 : 1)'
check "time zone data and /etc/services (tzdata, netbase)" \
  docker run --rm --entrypoint bash "$IMAGE" -c 'test -f /usr/share/zoneinfo/Europe/Berlin && grep -q "^https" /etc/services'
check "no .NET, PowerShell or CodeGraph in the base (they're tools)" \
  docker run --rm --entrypoint bash "$IMAGE" -c '! command -v dotnet && ! command -v pwsh && ! command -v codegraph && ! test -e /usr/share/dotnet'
check "no .NET settings in the base's environment" \
  docker run --rm --entrypoint bash "$IMAGE" -c '! env | grep -E "^(DOTNET|ASPNETCORE|NUGET_XMLDOC|POWERSHELL)_"'
fi

if want 2; then
# ==============================================================================
section "2. Entrypoint and hardening"

git -C "$PROJECT_DIR" config commit.gpgsign true
out=$(in_sandbox '
  echo "id=$(id -u):$(id -g)"
  echo "user=$(id -un) home=$HOME"
  grep -E "^(CapInh|CapPrm|CapEff|CapBnd|CapAmb|NoNewPrivs):" /proc/self/status
  capsh --print | grep -E "^Bounding set"
  command -v sudo >/dev/null && echo "sudo=present" || echo "sudo=absent"
  echo "pid1=$(cat /proc/1/comm) pid1uid=$(stat -c %u /proc/1)"
  echo "pids.max=$(cat /sys/fs/cgroup/pids.max 2>/dev/null)"
  echo "core=$(ulimit -c)/$(ulimit -Hc)"
  capsh --print | grep -E "^Securebits:"
  touch /usr/local/bin/x 2>/dev/null && echo "rootfs=writable" || echo "rootfs=protected"
  echo "is_sandbox=${IS_SANDBOX:-}"
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
[ -n "$ROOTLESS" ] && note "rootless Docker: the agent is container root, which is the host user"
check "runs as the host user (inside: $AGENT_UID:$AGENT_GID)" has "id=$AGENT_UID:$AGENT_GID"
check "user $AGENT_USER, HOME=/home/agent" has "user=$AGENT_USER home=/home/agent"
for cap in CapInh CapPrm CapEff CapBnd CapAmb; do
  check "$cap empty" has "$cap:	0000000000000000"
done
check "no_new_privs set" has "NoNewPrivs:	1"
check "capsh: empty bounding set" grep -qE '^Bounding set =$' <<<"$out"
check "no sudo" has "sudo=absent"
check "PID 1 is tini, running as the agent's UID" has "pid1=tini pid1uid=$AGENT_UID"
check "the image's files can't be changed (/usr/local)" has "rootfs=protected"
if [ -n "$ROOTLESS" ]; then
  check "rootless: securebits noroot set and locked" grep -qE '^Securebits: [0-7]+/0x2f/' <<<"$out" # noroot, no_setuid_fixup, both locked, keep_caps_locked
  check "rootless: the root filesystem is read-only" bash -c 'grep -q "rootfs=protected" <<<"$1"' _ "$out"
  check "rootless: IS_SANDBOX=1 (Claude's skip-permissions as root)" has "is_sandbox=1"
fi
check "pids_limit applied" has "pids.max=4096"
check "no core dumps (soft and hard limit 0)" has "core=0/0"
check "new home volume owned by the agent" has "home_owner=$AGENT_UID:$AGENT_GID"
check "new NuGet volume owned by the agent" has "nuget_owner=$AGENT_UID:$AGENT_GID"
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
fi

if want 3; then
# ==============================================================================
section "3. Proxy and network"

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
before=$(docker volume ls -q --filter dangling=true | sort)
pen_run true >/dev/null 2>&1
check "a session leaves no anonymous volumes behind (squid's log and spool dirs are tmpfs)" \
  test -z "$(comm -13 <(echo "$before") <(docker volume ls -q --filter dangling=true | sort))"

# Docker Engine 23.0.11, 25.0.5 and 26.0+ don't forward DNS from an internal
# network (CVE-2024-29018); docs/usage.md#network relies on that.
check "the proxy's name resolves inside the sandbox" in_sandbox 'getent hosts proxy >/dev/null'
check "external names don't resolve inside the sandbox" not in_sandbox 'getent hosts example.com >/dev/null'

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
fi

if want 4; then
# ==============================================================================
section "4. Agents"

out=$(in_sandbox 'for c in claude codex opencode pi; do printf "%s=" $c; timeout 30 $c --version </dev/null 2>&1 | tail -1; done')
for c in claude codex opencode pi; do
  check "$c --version works as agent ($(grep "^$c=" <<<"$out" | cut -d= -f2-))" grep -qE "^$c=.*[0-9]+\.[0-9]+" <<<"$out"
done
check "agents live in system paths, not \$HOME" \
  in_sandbox 'for c in claude codex opencode pi; do case $(readlink -f "$(command -v $c)") in /usr/local/*|/opt/*) ;; *) exit 1 ;; esac; done'
check "agent user can't write the agents' install dir" \
  in_sandbox '! touch /opt/pen/agents/lib/node_modules/.x 2>/dev/null'
check "auto-updaters off (env)" \
  in_sandbox '[ "$DISABLE_AUTOUPDATER" = 1 ] && [ "$OPENCODE_DISABLE_AUTOUPDATE" = 1 ] && [ "$PI_SKIP_VERSION_CHECK" = 1 ] && grep -q "check_for_update_on_startup = false" /etc/codex/managed_config.toml'

# Trivial prompts, only when credentials are in the environment (otherwise log
# in inside a session by hand). They reach the agent as [env] entries in a
# throwaway config, the way users pass them.
prompt_check() { # prompt_check <agent> <env var> <cmd…>
  local agent=$1 var=$2; shift 2
  if [ -z "${!var:-}" ]; then note "$agent prompt: skipped (set $var to run it)"; return; fi
  fake_config "[env]" "$var = \"${!var}\""
  local out
  out=$(fake_pen run timeout 180 "$@" 2>&1 || true)
  check "$agent answers a trivial prompt through the proxy" grep -q 4 <<<"$out"
  local log; log=$(ls -td "$FAKE_HOME"/.local/state/pen/"$KEY"/sessions/*/ | head -1)proxy.log
  if grep -q TCP_DENIED "$log"; then
    note "$agent: denied → $(grep TCP_DENIED "$log" | awk '{print $5}' | sort -u | tr '\n' ' ')"
  fi
}
# The fixture project's home volume is fresh, so this also shows
# [env] CLAUDE_CODE_OAUTH_TOKEN starts Claude logged in.
prompt_check claude CLAUDE_CODE_OAUTH_TOKEN claude -p 'What is 2+2? Reply with just the digit.'
prompt_check codex OPENAI_API_KEY codex exec --skip-git-repo-check 'What is 2+2? Reply with just the digit.'

if [ "${SMOKE_SLOW:-}" = 1 ]; then
  v1=$(docker run --rm --entrypoint stat "$IMAGE" -c %Y /opt/pen/agents/lib/node_modules/@anthropic-ai/claude-code/package.json)
  "$PEN" upgrade >"$SCRATCH/upgrade.log" 2>&1 || { tail -20 "$SCRATCH/upgrade.log" >&2; fail "pen upgrade"; }
  v2=$(docker run --rm --entrypoint stat "$IMAGE" -c %Y /opt/pen/agents/lib/node_modules/@anthropic-ai/claude-code/package.json)
  check "pen upgrade reinstalls the agents (PEN_REFRESH), base image unchanged or not" test "$v1" != "$v2"
  check "pen upgrade pulls the proxy image" grep -q 'ubuntu/squid' "$SCRATCH/upgrade.log"
else
  note "pen upgrade check: skipped (SMOKE_SLOW=1 to run it)"
fi
fi

if want 5; then
# ==============================================================================
section "5. CLI: install, dispatch, directory rules"


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
for bad in "--port 0" "--port 65536" "--port http" "--port=80x ls" "--port"; do
  rc=0; out=$("$PEN" $bad 2>&1 </dev/null) || rc=$?
  check "pen $bad is a usage error (exit 2), before any session" bash -c '[ "$1" = 2 ] && grep -q "^pen: --port: " <<<"$2"' _ "$rc" "$out"
done
rc=0; out=$("$PEN" --port 8765 allow 2>&1 </dev/null) || rc=$?
check "--port with a subcommand is a usage error" bash -c '[ "$1" = 2 ] && grep -qF -- "--port goes only with a command to run, not pen allow" <<<"$2"' _ "$rc" "$out"
check "pen --help lists pen trust" contains "pen trust" "$("$PEN" --help)"
check "pen trust --help documents --yes" contains "--yes" "$("$PEN" trust --help)"
check "pen --help documents --port" contains "--port <n>" "$("$PEN" --help)"
check "pen run --help documents --port and ports" bash -c "'$PEN' run --help | grep -qF -- '--port <n>' && '$PEN' run --help | grep -qF 'ports = ['"
check "pen --port 8765 --port=8766 starts a session" bash -c "'$PEN' --port 8765 --port=8766 run true </dev/null >/dev/null 2>&1"
check "...and --port after the command is the command's" contains "[--port 8765]" "$("$PEN" run sh -c 'echo "[$*]"' _ --port 8765 </dev/null 2>/dev/null)"
check "pen --help documents --tool" contains "--tool <spec>" "$("$PEN" --help)"
check "pen --tool list works outside a session" bash -c "'$PEN' --tool list </dev/null >/dev/null"
check "...and doesn't offer pen's test recipe" not contains "pen-test" "$("$PEN" --tool list </dev/null)"
for bad in "--tool nope" "--tool=nope:1 ls" "--tool" "--tool list ls"; do
  rc=0; out=$("$PEN" $bad 2>&1 </dev/null) || rc=$?
  check "pen $bad is a usage error (exit 2), before any session" bash -c '[ "$1" = 2 ] && grep -q "^pen: --tool" <<<"$2"' _ "$rc" "$out"
done
rc=0; out=$("$PEN" --tool 'pen-test:$(touch /tmp/x)' 2>&1 </dev/null) || rc=$?
check "a shell-like tool version is refused by the recipe's regex in the AOT build" bash -c '[ "$1" = 2 ] && grep -qF "isn'"'"'t a valid version of pen-test" <<<"$2"' _ "$rc" "$out"
rc=0; out=$("$PEN" --tool pen-test:1 allow 2>&1 </dev/null) || rc=$?
check "--tool with a subcommand is a usage error" bash -c '[ "$1" = 2 ] && grep -qF -- "--tool goes only with a command to run, not pen allow" <<<"$2"' _ "$rc" "$out"
check "pen --tool runs the session with the tool installed" test "$("$PEN" --tool pen-test run pen-test </dev/null 2>/dev/null)" = 1

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
fi

if want 6; then
# ==============================================================================
section "6. Sessions"

rc=0; pen_run sh -c 'exit 7' 2>/dev/null || rc=$?
check "the command's exit code passes through" test "$rc" = 7
check "pen run ls | head works without a TTY" test "$("$PEN" run ls -a </dev/null 2>/dev/null | head -1)" = .
out=$($PTY -- "$PEN" run tty)
check "with a terminal, the agent gets one too" contains "/dev/pts/" "$out"
out=$($PTY -- "$PEN" claude --version)
check "pen claude runs claude" grep -qE '[0-9]+\.[0-9]+\.[0-9]+' <<<"$out"
out=$($PTY --send-on '[$#] $' 'echo shell-$((6*7)); exit\r' --timeout 60 -- "$PEN")
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
check "the runtime dir records the project's real path in project-dir (prune D1)" test "$(cat "$RUNTIME/project-dir")" = "$PROJECT_DIR"
check "the project's volumes are named by its key" bash -c 'docker volume inspect "$1" "$2" >/dev/null' _ "${KEY}_home" "${KEY}_nuget"
check "pen:latest carries its image hash" test -n "$(docker image inspect --format '{{ index .Config.Labels "pen.image-hash" }}' "$IMAGE")"

# The case warning: one per session where the git dir folds case, none elsewhere.
CASE_WARNING="isn't known to be case-sensitive"
case_warnings() { grep -cF "$CASE_WARNING" <<<"$1" || true; }
probes_left() { find "$PROJECT_DIR/.git" -maxdepth 1 -iname '.pen-case-probe-*' | wc -l | tr -d ' '; }
touch "$PROJECT_DIR/.git/.smoke-case-check"
if [ -e "$PROJECT_DIR/.git/.SMOKE-CASE-CHECK" ]; then want_warnings=1; else want_warnings=0; fi
rm -f "$PROJECT_DIR/.git/.smoke-case-check"
out=$(pen_run true 2>&1 >/dev/null)
check "a session warns $want_warnings time(s) about case on this filesystem" test "$(case_warnings "$out")" = "$want_warnings"
check "the case probe leaves nothing in the git dir" test "$(probes_left)" = 0
git worktree add -q "$SCRATCH/case-wt" 2>/dev/null
out=$(cd "$SCRATCH/case-wt" && pen_run true 2>&1 >/dev/null)
check "a linked worktree's session warns as the common git dir says" test "$(case_warnings "$out")" = "$want_warnings"
check "...and its probe leaves nothing there either" test "$(probes_left)" = 0
git worktree remove --force "$SCRATCH/case-wt"
if [ "$PEN_UID" != 0 ]; then
  chmod 555 "$PROJECT_DIR/.git"
  out=$(pen_run true 2>&1 >/dev/null || true)
  chmod 755 "$PROJECT_DIR/.git"
  check "a git dir the probe can't write to gets the one warning" test "$(case_warnings "$out")" = 1
fi
NOREPO=$(cd "$(mktemp -d "$HOME/.pen-smoke-norepo.XXXXXX")" && pwd -P)
EXTRA_DIRS+=("$NOREPO")
out=$(cd "$NOREPO" && pen_run true 2>&1 >/dev/null)
check "a project that isn't a repo gets no case warning" test "$(case_warnings "$out")" = 0
fi

if want 7; then
# ==============================================================================
section "7. Locks, dead sessions, signals"

"$PEN" run sleep 120 </dev/null >/dev/null 2>&1 &
p=$!
wait_for "^$KEY-.*-agent$" || fail "session didn't start"
out=$("$PEN" ps)
check "pen ps lists the running session: project, id, tools, age and command" \
  grep -qE "^~${PROJECT_DIR#"$HOME"} +[0-9a-f]{8} +$(tools_cell "$(last_session)")[0-9]+[smh][0-9m]* +sleep 120$" <<<"$out"
check "project.lock is held shared: another shared lock is fine" lock_free "$RUNTIME/project.lock" shared
check "...and an exclusive one (pen reset) is refused" not lock_free "$RUNTIME/project.lock" exclusive
check "this dir's worktree lock is held shared too" lock_free "$RUNTIME/worktree-$HASH.lock" shared
check "...so pen sign's exclusive hold on it is refused" not lock_free "$RUNTIME/worktree-$HASH.lock" exclusive
sdir=$(ls -td "$RUNTIME"/sessions/*/ | head -1)
check "the running session's session.lock is held" not lock_free "${sdir}session.lock" exclusive
compose_pid=$(pgrep -P "$p" | head -1)
if command -v lsof >/dev/null; then
  check "the compose child holds no lock fds (lsof)" not bash -c "lsof -p $compose_pid 2>/dev/null | grep -q '\.lock'"
  check "while pen itself holds all three locks (project, worktree, session)" test "$(lsof -p "$p" 2>/dev/null | grep -c '\.lock$')" = 3
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
check "...not even the worktree lock" lock_free "$RUNTIME/worktree-$HASH.lock" exclusive
fi

if want 8; then
# ==============================================================================
section "8. Config file"

rm -rf "$FAKE_HOME/.config" # in case section 4's prompts made one
out=$(fake_pen run true 2>&1)
cfg=$FAKE_HOME/.config/pen/config.toml
check "first run creates ~/.config/pen/config.toml" test -f "$cfg"
check "...mode 600" test "$(mode_of "$cfg")" = 600
check "...with every value commented out" not grep -qvE '^[[:space:]]*(#|$|\[[a-z]+\]$|allow = \[$|\]$)' "$cfg"
check "...and says so in one line" test "$(grep -c 'created ~/.config/pen/' <<<"$out")" = 1
seed=$FAKE_HOME/.config/pen/home
check "...and creates home/ (the seed)" test "$(cd "$seed" && find . -type f | sort | tr '\n' ' ')" = "./.claude/CLAUDE.md ./.claude/settings.json ./.codex/AGENTS.md ./.codex/config.toml ./.gitconfig "
check "...whose .gitconfig has the host's user.name/user.email" \
  test "$(git config --file "$seed/.gitconfig" user.name) <$(git config --file "$seed/.gitconfig" user.email)>" = "Smoke Host <smoke-host@example.invalid>"
sum=$(cksum <"$cfg") seedsum=$(cd "$seed" && find . -type f -exec cksum {} + | sort)
out=$(fake_pen run true 2>&1)
check "a second run says nothing about config" not contains "created" "$out"
check "...and changes neither" bash -c '[ "$(cksum <"$1")" = "$2" ] && [ "$(cd "$3" && find . -type f -exec cksum {} + | sort)" = "$4" ]' _ "$cfg" "$sum" "$seed" "$seedsum"

fake_config "# comment" "allw = 1"
check "a misspelled key is reported with its file" \
  contains '~/.config/pen/config.toml: unknown key "allw"' "$(fake_pen run true 2>&1 || true)"
fake_config "[env]" "X = unquoted"
check "a TOML error is reported with its file and line" \
  contains '~/.config/pen/config.toml:2:' "$(fake_pen run true 2>&1 || true)"

secret="smoke-$$-$RANDOM-secret"
fake_config "[env]" "SMOKE_SECRET = \"$secret\""
fake_pen run bash -c 'sleep 4; echo "[$SMOKE_SECRET]"' >"$SCRATCH/env.out" 2>&1 &
p=$!
wait_for "^$KEY-.*-agent$" || fail "session didn't start"
check "an [env] value never appears in any process's arguments" not bash -c "ps -axww -o args= | grep -v grep | grep -qF '$secret'"
wait "$p" || true
check "...and reaches the agent" grep -qF "[$secret]" "$SCRATCH/env.out"

mkdir -p .pen
printf '[env]\nFOO = "x"\n' >.pen/config.toml
check "[env] in .pen/config.toml is refused, naming the file" contains '.pen/config.toml: "env" belongs in' "$(pen_run true 2>&1 || true)"
printf '[github]\nacme = "x"\n' >.pen/config.toml
check "[github] in .pen/config.toml is refused, naming the file" contains '.pen/config.toml: "github" belongs in' "$(pen_run true 2>&1 || true)"
printf 'ports = [3000, 5173]\n' >.pen/config.toml
check "ports in .pen/config.toml is accepted" pen_run true
printf 'ports = [70000]\n' >.pen/config.toml
check "an out-of-range port in .pen/config.toml is refused, naming the file" contains '.pen/config.toml: ports: 70000 isn'"'"'t a port number' "$(pen_run true 2>&1 || true)"
printf 'ports = ["3000"]\n' >.pen/config.toml
check "...and so is a quoted one" contains '.pen/config.toml: ports: "3000" isn'"'"'t a port number' "$(pen_run true 2>&1 || true)"
rm .pen/config.toml
fake_config "ports = [3000]"
check "ports in ~/.config/pen/config.toml is refused, pointing to .pen/config.toml" \
  contains '~/.config/pen/config.toml: "ports" belongs in .pen/config.toml (or use pen --port)' "$(fake_pen run true 2>&1 || true)"
fake_config 'tools = ["pen-test:1"]'
check "tools in ~/.config/pen/config.toml is accepted" fake_pen run true
check "...and a version --tool can't coexist with is refused" contains "pen-test:1 and pen-test:2 can't be used together" "$(fake_pen --tool pen-test:2 run true 2>&1 || true)"
printf 'tools = ["pen-test:2"]\n' >.pen/config.toml
check "...as is one from .pen/config.toml" contains "pen-test:1 and pen-test:2 can't be used together" "$(fake_pen run true 2>&1 || true)"
printf 'tools = ["pen-test:1"]\n' >.pen/config.toml
check "...but the same version is fine" fake_pen --tool pen-test run true
printf 'tools = ["pen-test:01"]\n' >.pen/config.toml
check "a version form the recipe doesn't want is refused, not rewritten" contains '.pen/config.toml: tools: "01" isn'"'"'t a valid version of pen-test' "$(pen_run true 2>&1 || true)"
printf 'tools = ["nope"]\n' >.pen/config.toml
check "an unknown tool in .pen/config.toml is refused, naming the file" contains '.pen/config.toml: tools: unknown tool "nope"' "$(pen_run true 2>&1 || true)"
printf 'tools = ["pen-test:1;id"]\n' >.pen/config.toml
check "...and so is a shell-like version" contains ".pen/config.toml: tools: \"1;id\" isn't a valid version of pen-test" "$(pen_run true 2>&1 || true)"
rm .pen/config.toml
fake_config "# empty"
echo "allow = example.com" >.pen/config
check "a legacy .pen/config is refused with a pointer to the new files" contains ".pen/config is pen's old config format" "$(pen_run true 2>&1 || true)"
rm -rf .pen
fi

if want 9; then
# ==============================================================================
section "9. GitHub token"

owner=pen-smoke-owner
git remote add origin "git@github.com:$owner/repo.git"
fake_config "# none"
rm -f "$FAKE_HOME/.local/state/pen/$KEY/token-notice"
out=$(fake_pen run true 2>&1)
check "no token for the origin's owner: a notice with the line to add" contains "no GitHub token for '$owner': add $owner = \"github_pat_…\" under [github]" "$out"
check "...only on the project's first session" not contains "no GitHub token" "$(fake_pen run true 2>&1)"
fake_config "[github]" "$owner = \"ghp_smoke_fake_token\""
out=$(fake_pen run bash -c 'echo "[$GH_TOKEN]"' 2>&1)
check "the owner's token reaches the agent as GH_TOKEN" contains "[ghp_smoke_fake_token]" "$out"
check "...with no notice" not contains "no GitHub token" "$out"
fake_config "# removed again"
check "removing the token later shows the notice again" contains "no GitHub token for '$owner'" "$(fake_pen run true 2>&1)"
mkdir -p .pen
echo 'github_owner = "other-owner"' >.pen/config.toml
fake_config "[github]" "other-owner = \"ghp_other\""
out=$(fake_pen run bash -c 'echo "[$GH_TOKEN]"' 2>&1 || true)
check "an unapproved github_owner is refused without a terminal, pointing to pen trust" has_all "$out" "github_owner = other-owner" "pen trust"
check "...before the token goes anywhere" not contains "[ghp_other]" "$out"
fake_pen trust --yes >/dev/null 2>&1
check "github_owner in .pen/config.toml picks the owner" contains "[ghp_other]" "$(fake_pen run bash -c 'echo "[$GH_TOKEN]"' 2>&1)"
rm -rf .pen
fake_config "# none"
out=$(HOME=$FAKE_HOME DOCKER_CONFIG=${DOCKER_CONFIG:-$REAL_HOME/.docker} $PTY --timeout 60 -- "$PEN" run bash -c "git ls-remote https://github.com/$owner/private-$$; echo rc=\$?")
check "a private fetch with no token fails with an error instead of prompting" \
  bash -c 'grep -qE "rc=[1-9]" <<<"$1" && grep -q "EXIT 0" <<<"$1"' _ "$out"
git remote remove origin
note "real-token checks (gh repo view, clone, push 403, per-owner tokens) are manual; use a read-only PAT as described in docs/usage.md#github"
fi

if want 10; then
# ==============================================================================
section "10. Allowlist merge and the blocked line"

out=$(in_sandbox 'curl -s -o /dev/null -I https://example.com' 2>&1 || true)
check "session end prints the blocked line" contains "blocked: example.com → pen allow example.com (add --global for all projects)" "$out"
out=$(in_sandbox 'curl -s -o /dev/null -I https://github.com' 2>&1 || true)
check "...and nothing when nothing was blocked" not contains "blocked:" "$out"
mkdir -p .pen
echo 'allow = ["example.com"]' >.pen/config.toml
"$PEN" trust --yes </dev/null >/dev/null 2>&1
check "a domain in .pen/config.toml's allow becomes reachable" \
  test "$(in_sandbox 'curl -s -o /dev/null -w %{http_code} -I https://example.com' 2>/dev/null)" = 200
rm -rf .pen
tool_probe='curl -s -o /dev/null -w %{http_code} -I https://example.org'
check "a domain no one allowed stays blocked" not contains 200 "$(in_sandbox "$tool_probe" 2>/dev/null)"
check "...and a --tool's recipe adds its domains to the session's allowlist" \
  test "$("$PEN" --tool pen-test run bash -c "$tool_probe" </dev/null 2>/dev/null)" = 200
fake_config 'tools = ["pen-test"]'
check "...as does one in ~/.config/pen/config.toml" test "$(fake_pen run bash -c "$tool_probe" 2>/dev/null)" = 200
fake_config 'allow = [".github.com"]'
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
fi

if want 11; then
# ==============================================================================
section "11. .NET and NuGet, with --tool dotnet"
tool_free # asserts what's there without the tool, and dotnet:8 alone

# dn_sandbox [Pen options…] <bash script>: in_sandbox with the dotnet tool (dotnet:10 unless options say).
dn_sandbox() { local script=${*: -1}; "$PEN" --tool dotnet "${@:1:$#-1}" run bash -c "$script" </dev/null; }
majors() { awk '{ split($1, v, "."); print v[1] }' | sort -n | uniq | tr '\n' ' '; }

nuget() { curl -s -o /dev/null -w "%{http_code} %{http_connect}" -I https://api.nuget.org/v3/index.json; }
out=$(in_sandbox "command -v dotnet pwsh || echo none; $(declare -f nuget); nuget" 2>/dev/null || true)
check "without the tool there's no dotnet or pwsh, and NuGet is blocked" \
  bash -c '[ "$(sed -n 1p <<<"$1")" = none ] && [ "$(sed -n 2p <<<"$1" | cut -d" " -f2)" = 403 ]' _ "$out"
out=$("$PEN" --tool dotnet:8 run bash -c "readlink -f \$(command -v pwsh); pwsh -NoProfile -Command '\$PSVersionTable.PSVersion.Major'; dotnet --list-sdks" </dev/null 2>/dev/null || true)
check "dotnet:8 alone: pwsh is the recipe's (/opt/microsoft/powershell/7) and runs" \
  bash -c '[ "$(sed -n 1p <<<"$1")" = /opt/microsoft/powershell/7/pwsh ] && grep -qxE "[0-9]+" <<<"$(sed -n 2p <<<"$1")"' _ "$out"
check "...and dotnet lists only SDK 8" test "$(tail -n +3 <<<"$out" | majors)" = "8 "
check "with the tool, NuGet is reachable" test "$(dn_sandbox "$(declare -f nuget); nuget" 2>/dev/null | cut -d' ' -f1)" = 200
out=$(dn_sandbox 'dotnet --list-sdks' 2>/dev/null || true)
check "dotnet alone lists the SDK of the default version, 10" test "$(majors <<<"$out")" = "10 "
check "dotnet:8 with dotnet:10 lists both SDKs" \
  test "$(dn_sandbox --tool dotnet:8 'dotnet --list-sdks' 2>/dev/null | majors)" = "8 10 "
out=$(dn_sandbox 'env' 2>/dev/null || true)
check "...with the ASP.NET and container settings" has_all "$out" ASPNETCORE_HTTP_PORTS=8080 DOTNET_RUNNING_IN_CONTAINER=true \
  DOTNET_GENERATE_ASPNET_CERTIFICATE=false DOTNET_USE_POLLING_FILE_WATCHER=true NUGET_XMLDOC_MODE=skip \
  POWERSHELL_DISTRIBUTION_CHANNEL= DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1

out=$(dn_sandbox 'mkdir -p app && cd app && dotnet new console -o . --force >/dev/null && dotnet add package Humanizer.Core >/dev/null && dotnet run' 2>&1 || true)
check "dotnet new console, a NuGet restore and dotnet run work" contains "Hello, World!" "$out"
check "...with nothing blocked" not contains "blocked:" "$out"
out=$(dn_sandbox 'cd app && rm -rf obj bin && dotnet restore -v d' 2>&1 || true)
check "a second restore finds the package in the cache volume (no download)" not grep -qiE 'GET https://[^ ]*humanizer' <<<"$out"
check "NUGET_PACKAGES is the host's path, backed by the project's volume" \
  test "$(dn_sandbox 'echo $NUGET_PACKAGES; ls $NUGET_PACKAGES' 2>/dev/null | head -2 | tr '\n' ' ')" = "$NUGET_PACKAGES humanizer.core "
if command -v dotnet >/dev/null; then
  check "host build and run after the sandbox's build" bash -c "cd app && dotnet build -v q >/dev/null 2>&1 && dotnet run --no-build | grep -q 'Hello, World!'"
  check "...then the sandbox builds again" contains "Hello, World!" "$(dn_sandbox 'cd app && dotnet build -v q >/dev/null && dotnet run --no-build' 2>&1)"
else
  note "no .NET SDK on the host: skipped the host/sandbox round trip"
fi
rm -rf app

# A web app serves on ASPNETCORE_HTTP_PORTS (8080), forwarded to the host.
if python3 -c 'import socket, sys; s = socket.socket(); s.settimeout(2); sys.exit(s.connect_ex(("127.0.0.1", 8080)) != 0)'; then
  note "port 8080 is in use on this machine: skipped the web app check"
else
  "$PEN" --tool dotnet --port 8080 run bash -c '
    mkdir -p web && cd web && dotnet new web -o . --force >/dev/null && dotnet build -v q >/dev/null || exit 1
    dotnet run --no-build --no-launch-profile >/dev/null 2>&1 &
    for i in $(seq 120); do curl -s --noproxy "*" -o /dev/null localhost:8080 && break; sleep 0.5; done
    echo serving
    for i in $(seq 60); do [ -e .served ] && break; sleep 0.5; done
  ' </dev/null >"$SCRATCH/web.out" 2>&1 &
  p=$!
  for i in $(seq 600); do grep -q '^serving$' "$SCRATCH/web.out" && break; kill -0 "$p" 2>/dev/null || break; sleep 0.5; done
  out=$(curl -s -m 5 http://127.0.0.1:8080/ || true)
  touch web/.served 2>/dev/null || true
  wait "$p" || true
  check "a new web app builds and serves on the forwarded port 8080" test "$out" = "Hello World!"
  rm -rf web
fi

real_home
note "login checks (log in to claude and codex, still logged in after pen upgrade --clean, logged out in another project) are manual"
fi

if want 12; then
# ==============================================================================
section "12. Repo refusals at start"

git worktree add -q "$SCRATCH/wt" 2>/dev/null # kept until section 13 (its .git/worktrees entry is mounted read-only)
mkdir -p "$SCRATCH/forged"
printf 'gitdir: %s/.git/worktrees/wt\n' "$PROJECT_DIR" >"$SCRATCH/forged/.git"
check "refuses a .git file that isn't a valid linked worktree (section 21 runs in one)" refuse "$SCRATCH/forged" "isn't a valid linked worktree"
forget "$SCRATCH/forged"; rm -rf "$SCRATCH/forged"
git init -q unlisted/x; chmod 311 unlisted
check "refuses a directory it can enter but not list, naming it" refuse "$PROJECT_DIR" "unlisted"
chmod 755 unlisted; rm -rf unlisted
mkdir -p shut; chmod 000 shut
check "a directory that can't be entered at all starts" pen_run true
chmod 755 shut; rm -rf shut
mkdir fifo; mkfifo fifo/.git
check "refuses a FIFO named .git rather than hanging on it" refuse "$PROJECT_DIR" "fifo"
rm -rf fifo
touch .gitmodules
check "a repo with .gitmodules starts (section 26 runs with real submodules)" pen_run true
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
# the bottom makes Pen refuse right after the scan, before any Docker call,
# so the refusal's time is the scan's (plus Pen's own startup).
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
fi

if want 13; then
# ==============================================================================
section "13. Read-only .git and .pen, session-end check and quarantine"

# Section 12 leaves this worktree for the worktree-entry checks below.
[ -e "$SCRATCH/wt" ] || git worktree add -q "$SCRATCH/wt" 2>/dev/null

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
  t "echo 0000000000000000000000000000000000000000 > .git/worktrees/wt/HEAD" wthead
  t "git worktree prune && test ! -e .git/worktrees/wt" wtprune
  t "test -d .git/worktrees/wt" wtkept
  t "mv .git/worktrees .git/worktrees2" mvworktrees
  t "touch .pen/config.toml" pencreate
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
check "...as is the rest of its entry (its HEAD)" has "fail: wthead"
check "git worktree prune doesn't delete the entry of a worktree outside the project" has "fail: wtprune"
check "...which is still there afterward" has "ok: wtkept"
check "mv .git/worktrees fails" has "fail: mvworktrees"
check "...and the worktree still works on the host" bash -c "git -C '$SCRATCH/wt' status --porcelain >/dev/null && git -C '$SCRATCH/wt' -c user.name=s -c user.email=s@example.invalid -c commit.gpgsign=false commit -q --allow-empty -m host-side"
check "...and git worktree list on the host doesn't call it prunable" not contains prunable "$(git worktree list --porcelain)"
check ".pen/ is created empty when missing" bash -c '[ -d .pen ] && [ -z "$(ls -A .pen)" ]'
check "...touch .pen/config.toml fails inside" has "fail: pencreate"
check "...mv .pen fails" has "fail: mvpen"
st=$(git status --porcelain | tr '\n' ' ')
check "...and host git status shows nothing new${st:+ (it shows: $st)}" test -z "$st"
out=$(in_sandbox 'mkdir .git/rebase-merge .git/sequencer && printf "pick abc one\n  x touch planted-by-a-rebase\n# exec commented\n" >.git/rebase-merge/git-rebase-todo && mkfifo .git/rebase-merge/done && printf "x from-the-sequencer\n" >.git/sequencer/todo' 2>&1)
check "a planted paused rebase is reported at session end" contains ".git/rebase-merge: a paused rebase" "$out"
check "...with its exec line" contains "x touch planted-by-a-rebase" "$out"
check "...and not the commented one" not contains "exec commented" "$out"
check "...saying to inspect it before continuing" contains "before git rebase --continue" "$out"
check "...and that a FIFO in it can't be read" contains "couldn't read done" "$out"
check "...and the sequencer's" contains "x from-the-sequencer" "$out"
check "...and it's left in place" grep -q planted-by-a-rebase .git/rebase-merge/git-rebase-todo
rm -rf .git/rebase-merge .git/sequencer

out=$(in_sandbox 'mkdir .git/rebase-merge && echo "\$(touch prompt-canary)" >.git/rebase-merge/msgnum && echo 2 >.git/rebase-merge/end' 2>&1)
check "a planted count a shell prompt would run is quarantined at session end" contains ".git/rebase-merge/msgnum → .git/rebase-merge/msgnum.pen-quarantine" "$out"
check "...while its neighbour git could have written stays" test -f .git/rebase-merge/end
prompt_sh=$(for f in /opt/homebrew/opt/git/etc/bash_completion.d/git-prompt.sh /usr/lib/git-core/git-sh-prompt /usr/share/git-core/contrib/completion/git-prompt.sh; do [ -f "$f" ] && echo "$f" && break; done)
if [ -n "$prompt_sh" ]; then
  printf 'source %q\nPROMPT_COMMAND=%q\n' "$prompt_sh" '__git_ps1 a b' >"$SCRATCH/prompt.rc"
  bash --noprofile --rcfile "$SCRATCH/prompt.rc" -i </dev/null >/dev/null 2>&1 || true
  check "...and the next prompt runs nothing" test ! -e prompt-canary
  mv .git/rebase-merge/msgnum.pen-quarantine .git/rebase-merge/msgnum
  bash --noprofile --rcfile "$SCRATCH/prompt.rc" -i </dev/null >/dev/null 2>&1 || true
  check "...though it does if the file is put back (so that check means something)" test -e prompt-canary
  rm -f prompt-canary
else
  note "git-prompt.sh isn't installed here, so the shell prompt replay was skipped"
fi
rm -rf .git/rebase-merge

out=$(in_sandbox 'mkdir .git/rebase-merge && echo "\$(touch prompt-canary)" >.git/rebase-merge/msgnum && echo 2 >.git/rebase-merge/end && chmod 555 .git/rebase-merge' 2>&1)
check "a read-only state dir doesn't keep a planted count in place" contains ".git/rebase-merge/msgnum → .git/rebase-merge/msgnum.pen-quarantine" "$out"
check "...and says what mode it changed" contains "changed .git/rebase-merge from mode 555" "$out"
chmod -R u+w .git/rebase-merge; rm -rf .git/rebase-merge
out=$(in_sandbox 'mkdir "$HOME/rm" && echo "\$(touch prompt-canary)" >"$HOME/rm/msgnum" && ln -s "$HOME/rm" .git/rebase-merge && chmod 555 .git' 2>&1)
check "...nor does a read-only .git keep a symlinked one" contains ".git/rebase-merge → .git/rebase-merge.pen-quarantine" "$out"
check "...and the check finished" not contains "git wasn't checked" "$out"
chmod u+w .git; rm -f .git/rebase-merge.pen-quarantine
out=$(in_sandbox 'mkdir .git/rebase-merge && echo "\$(touch prompt-canary)" >.git/rebase-merge/msgnum && echo 2 >.git/rebase-merge/end && chmod 311 .git' 2>&1)
check "...nor does a .git that can't be listed" contains ".git/rebase-merge/msgnum → .git/rebase-merge/msgnum.pen-quarantine" "$out"
check "...and the check finished" not contains "git wasn't checked" "$out"
chmod u+rwx .git; rm -rf .git/rebase-merge

git worktree remove --force "$SCRATCH/wt"
git branch -q -D scratch

echo '# host-written' >.pen/config.toml
out=$(in_sandbox 'echo x >> .pen/config.toml && echo wrote; mv .pen .pen2 && echo moved; true' 2>&1)
check "with .pen/config.toml present, it can't be written or moved" bash -c '! grep -qE "^(wrote|moved)$" <<<"$1"' _ "$out"
check "...and is unchanged" test "$(cat .pen/config.toml)" = '# host-written'
rm .pen/config.toml

: >.gitinc
git config include.path ../.gitinc
out=$(in_sandbox 'echo "[core] fsmonitor = x" >> .gitinc && echo wrote; true' 2>&1)
check "a config file .git/config includes from the project is read-only" bash -c '! grep -qx wrote <<<"$1" && [ ! -s .gitinc ]' _ "$out"
git config --unset include.path; rm .gitinc

printf '[extensions]\nworktreeConfig = true\n' >.git/extension.inc
git config include.path extension.inc
printf '[include]\npath = ../.gitinc\n' >.git/config.worktree
printf '[include]\npath = .gitinc-nested\n' >.gitinc
: >.gitinc-nested
out=$(in_sandbox 'for f in .git/config.worktree .gitinc .gitinc-nested; do echo x >> "$f" && echo "wrote $f"; done; true' 2>&1)
check "included worktreeConfig settings and recursive worktree includes are protected" bash -c '! grep -q "^wrote " <<<"$1"' _ "$out"
git config --unset include.path
rm .git/extension.inc .git/config.worktree .gitinc .gitinc-nested

quarantine_case() { # <what the agent runs> <path> <quarantined path>
  local out
  out=$(in_sandbox "$1" 2>&1 || true)
  check "$1: warned at session end" contains "pen: WARNING" "$out"
  check "...and moved to $3 before pen returned" bash -c "[ ! -e '$2' ] && [ -e '$3' ]"
  check "...with how to put it back" contains "mv '$PROJECT_DIR/$3' '$PROJECT_DIR/$2'" "$out"
}
quarantine_case 'touch .git/commondir' .git/commondir .git/commondir.pen-quarantine
quarantine_case 'git init -q --bare .git/modules/x' .git/modules/x .git/modules/x.pen-quarantine
rm -rf .git/modules
quarantine_case 'git init -q sub' sub/.git sub/.git.pen-quarantine
check "...and the moved .git no longer looks like a repo (its HEAD moved too)" test -e sub/.git.pen-quarantine/HEAD.pen-quarantine
quarantine_case 'git init -q --bare sub2' sub2/HEAD sub2/HEAD.pen-quarantine
check "the next session starts normally" pen_run true
rm -rf .git/commondir.pen-quarantine sub sub2

out=$(in_sandbox 'git worktree add -q .claude/worktrees/y' 2>&1)
check "no warning for a git worktree add worktree" not contains "WARNING" "$out"
check "...and the next session starts normally" pen_run true
check "...and can commit in it (its entry is writable, being inside the project)" \
  in_sandbox 'git -C .claude/worktrees/y -c user.name=a -c user.email=a@example.invalid commit -q --allow-empty -m in-y'
check "...but not write its commondir" in_sandbox '! echo x 2>/dev/null > .git/worktrees/y/commondir'
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

# The agent shuts Pen's own cwd along with a plant: the end must still run, and say what to do.
canary=$SCRATCH/shut-canary
printf '#!/bin/sh\ntouch %q\n' "$canary" >"$SCRATCH/shut-hook"; chmod +x "$SCRATCH/shut-hook"
out=$(in_sandbox "printf '[core]\\n fsmonitor = $SCRATCH/shut-hook\\n' >> .git/CONFIG; chmod 000 ." 2>&1 || true)
perm=$(stat -c %a "$PROJECT_DIR" 2>/dev/null || stat -f %Lp "$PROJECT_DIR")
check "chmod 000 . in a session: it's really shut" test "$perm" = 0
check "...and pen warns that git wasn't checked" contains "git wasn't checked" "$out"
check "...and takes the stack down" test "$(cd "$SCRATCH" && containers)" = 0
out2=$("$PEN" run true </dev/null 2>&1 || true)
check "...and a command started there says why it can't run" grep -qE "current directory|can.t read" <<<"$out2"
line=$(sed -n 's/.*as one line: //p' <<<"$out")
check "...and prints a command to fix it" contains "chmod u+rwx" "$line"
(cd "$SCRATCH" && eval "${line%pen true}\"\$PEN\" run true </dev/null" >/dev/null 2>&1)
check "...which restores the config" bash -c '! grep -q fsmonitor .git/config'
git status >/dev/null 2>&1
check "...so the planted fsmonitor doesn't run" test ! -e "$canary"
check "...and ends that session" test "$(containers)" = 0
chmod 755 "$PROJECT_DIR"
check "...and the next session starts normally" pen_run true
rm -f .git/config.pen-quarantine

out=$(in_sandbox 'mkdir unlisted && git init -q unlisted/x && chmod 311 unlisted' 2>&1 || true)
check "a repo hidden in a directory left unlistable is quarantined" \
  bash -c '[ ! -e unlisted/x/.git ] && [ -d unlisted/x/.git.pen-quarantine ]'
check "...saying pen left the directory's mode changed" contains "changed unlisted from mode 311" "$out"
rm -rf unlisted

NOGIT=$(cd "$(mktemp -d "$HOME/.pen-smoke-nogit.XXXXXX")" && pwd -P)
EXTRA_DIRS+=("$NOGIT")
out=$(cd "$NOGIT" && "$PEN" run git init -q </dev/null 2>&1 || true)
check "a .git made by git init in a project without one is quarantined" \
  bash -c "[ ! -e '$NOGIT/.git' ] && [ -d '$NOGIT/.git.pen-quarantine' ]"
check "...with the warning" contains "git init" "$out"
check "...and a project without .git gets no .git mounts" bash -c "cd '$NOGIT' && rm -rf .git.pen-quarantine && '$PEN' run bash -c '[ ! -e .git ] && ! touch .pen/x' </dev/null"

# The core.hooksPath warning: one per session where the hooks are in the sandbox's reach, none elsewhere.
hooks_warnings() { grep -cF "core.hooksPath" <<<"$1" || true; }
git config core.hooksPath .husky
out=$(pen_run true 2>&1 >/dev/null)
check "core.hooksPath in the project warns once" test "$(hooks_warnings "$out")" = 1
check "...in plain words" contains "can be changed from the sandbox" "$out"
git worktree add -q "$SCRATCH/hooks-wt" 2>/dev/null
out=$(cd "$SCRATCH/hooks-wt" && pen_run true 2>&1 >/dev/null)
check "...and once in a linked worktree, where it's that worktree's" test "$(hooks_warnings "$out")" = 1
git worktree remove --force "$SCRATCH/hooks-wt"
git config core.hooksPath "$PROJECT_DIR/.git/hooks"
out=$(pen_run true 2>&1 >/dev/null)
check ".git/hooks by its full path doesn't warn" test "$(hooks_warnings "$out")" = 0
git config core.hooksPath /dev/null
out=$(pen_run true 2>&1 >/dev/null)
check "a path outside the project doesn't warn" test "$(hooks_warnings "$out")" = 0
git config --unset core.hooksPath
printf '[core]\n\thooksPath = %s/hooks\n' "$PROJECT_DIR" >"$SCRATCH/hooks-global"
out=$(GIT_CONFIG_GLOBAL=$SCRATCH/hooks-global pen_run true 2>&1 >/dev/null)
check "global config that leads into the project warns once" test "$(hooks_warnings "$out")" = 1
out=$(pen_run true 2>&1 >/dev/null)
check "no core.hooksPath, no warning" test "$(hooks_warnings "$out")" = 0

best=99
for i in 1 2 3; do
  t0=$(now); pen_run true; t1=$(now)
  best=$(python3 -c "print(f'{min($best, $t1 - $t0):.2f}')")
done
check "warm start: pen run true in under 5 s (best of 3: ${best}s; target about 2-3 s)" python3 -c "import sys; sys.exit(0 if $best < 5 else 1)"
fi

if want 14; then
# ==============================================================================
section "14. Project layer"
tool_free # without tools, the layer is <key>:latest

rm -rf .pen && mkdir .pen
printf 'FROM pen:latest\nRUN echo layer-ok >/etc/pen-smoke-layer\n' >.pen/Dockerfile
echo 'allow = ["example.org"]' >.pen/config.toml
out=$(in_sandbox 'cat /etc/pen-smoke-layer' 2>&1 || true)
check "an unapproved .pen/ doesn't start a session without a terminal" has_all "$out" "allow example.org" ".pen/Dockerfile" "pen trust"
check "...builds no layer" bash -c '! docker image inspect "$1" >/dev/null 2>&1' _ "$KEY:latest"
check "...and starts nothing" test "$(containers)" = 0
check "pen trust without a terminal needs --yes" contains "--yes" "$("$PEN" trust </dev/null 2>&1 || true)"
"$PEN" trust --yes </dev/null >/dev/null 2>&1
check "pen trust --yes has nothing new to approve the second time" contains "nothing new" "$("$PEN" trust --yes </dev/null 2>&1)"
out=$(in_sandbox 'cat /etc/pen-smoke-layer; curl -s -o /dev/null -w "code=%{http_code}\n" -I https://example.org' 2>&1)
check ".pen/Dockerfile is built and used" contains "layer-ok" "$out"
check "...as <project key>:latest" bash -c 'docker image inspect "$1" >/dev/null' _ "$KEY:latest"
check "...and the .pen/config.toml allow entry is reachable" contains "code=200" "$out"
t0=$(now); pen_run true; t1=$(now)
note "a session with a cached project layer: $(python3 -c "print(f'{$t1 - $t0:.2f}')")s"

# A rebuilt pen:latest (same hash label, new image) reaches the layer.
docker image tag "$IMAGE" pen:smoke-backup
printf 'FROM pen:smoke-backup\nLABEL smoke.base=rebuilt\n' | docker build -q -t "$IMAGE" - >/dev/null
pen_run true
docker image tag pen:smoke-backup "$IMAGE" && docker rmi pen:smoke-backup >/dev/null
check "a FROM pen:latest layer picks up a base-image rebuild" \
  test "$(docker image inspect --format '{{ index .Config.Labels "smoke.base" }}' "$KEY:latest")" = rebuilt

# What a build sees from .pen/, read through Pen's private copy of it.
printf 'FROM pen:latest\nCOPY . /etc/pen-ctx/\n' >.pen/Dockerfile
printf 'secret\n' >.pen/.dockerignore
echo s >.pen/secret
printf '#!/bin/sh\necho script-ran\n' >.pen/run.sh; chmod 755 .pen/run.sh
ln -s run.sh .pen/link
"$PEN" trust --yes </dev/null >/dev/null 2>&1
out=$(in_sandbox '/etc/pen-ctx/run.sh; readlink /etc/pen-ctx/link; [ -e /etc/pen-ctx/config.toml ] && echo config-copied; [ -e /etc/pen-ctx/secret ] && echo secret-copied; true' 2>&1)
check "a build's context keeps modes and symlinks" has_all "$out" "script-ran" "run.sh"
check "...leaves out config.toml, which isn't hashed" not contains "config-copied" "$out"
check "...and honors .dockerignore" not contains "secret-copied" "$out"
check "...and the private copy is gone afterwards" bash -c '! ls -d "$1"/layer-*/ >/dev/null 2>&1' _ "$RUNTIME"
rm -rf .pen/.dockerignore .pen/secret .pen/run.sh .pen/link

printf 'FROM pen:latest\nRUN echo smoke-build-error && exit 3\n' >.pen/Dockerfile
out=$(pen_run true 2>&1 || true)
check "a changed Dockerfile is asked about again, and alone" bash -c 'grep -q "\.pen/Dockerfile" <<<"$1" && ! grep -q "allow example.org" <<<"$1" && ! grep -q smoke-build-error <<<"$1"' _ "$out"
"$PEN" trust --yes </dev/null >/dev/null 2>&1
out=$(pen_run true 2>&1 || true)
check "a failing layer build stops the launch" contains "building the project layer from .pen/Dockerfile failed" "$out"
check "...showing the build output" contains "smoke-build-error" "$out"
check "...without starting anything" test "$(containers)" = 0
rm -rf .pen
docker image rm "$KEY:latest" >/dev/null 2>&1 || true
real_home
fi

if want 15; then
# ==============================================================================
section "15. pen allow <domain…>"

loop='for i in $(seq 60); do [ "$(curl -s -o /dev/null -w %{http_code} -I https://example.com)" = 200 ] && { echo got-200; exit 0; }; sleep 0.5; done; echo never-200'
"$PEN" run bash -c "$loop" </dev/null >"$SCRATCH/allow1.out" 2>&1 &
a1=$!
"$PEN" run bash -c "$loop" </dev/null >"$SCRATCH/allow2.out" 2>&1 &
a2=$!
wait_count "^$KEY-.*-agent$" 2 || fail "two sessions didn't start"
out=$("$PEN" allow example.com 2>&1)
wait "$a1" "$a2" || true
check "pen allow applies to both running sessions" contains "applied to 2 running sessions" "$out"
check "...and both reach the domain with no restart" bash -c 'grep -q got-200 "$1" && grep -q got-200 "$2"' _ "$SCRATCH/allow1.out" "$SCRATCH/allow2.out"
check "...whose end line no longer suggests allowing it" not grep -q "pen allow example.com" "$SCRATCH/allow1.out"
check "it creates .pen/config.toml with the commented stub" bash -c 'head -1 .pen/config.toml | grep -q "^# pen project config" && grep -q "^# github_owner" .pen/config.toml'
check "...plus an allow list with the domain" test "$(grep -cx '  "example.com",' .pen/config.toml)" = 1
out=$("$PEN" allow example.com 2>&1)
check "running it again adds nothing" bash -c 'grep -q "already in .pen/config.toml" <<<"$1" && [ "$(grep -c "example.com" .pen/config.toml)" = 1 ]' _ "$out"
check "a non-domain is refused" not "$PEN" allow 'http://x.io' 2>/dev/null
check "a domain added with pen allow doesn't ask next session" pen_run true

# A --tool's domains: only the session knows about the flag, so its record has to keep them.
keep='for i in $(seq 60); do [ "$(curl -s -o /dev/null -w %{http_code} -I https://example.net)" = 200 ] && break; sleep 0.5; done; curl -s -o /dev/null -w "tool-domain-%{http_code}\n" -I https://example.org'
"$PEN" --tool pen-test run bash -c "$keep" </dev/null >"$SCRATCH/allow3.out" 2>&1 &
a3=$!
wait_count "^$KEY-.*-agent$" 1 || fail "the session with a tool didn't start"
out=$("$PEN" allow example.net 2>&1)
wait "$a3" || true
check "pen allow applies to a session with a tool" contains "applied to 1 running session" "$out"
check "...which keeps the domains its tool brought" contains tool-domain-200 "$(cat "$SCRATCH/allow3.out")"

OTHER=$(cd "$(mktemp -d "$HOME/.pen-smoke-other.XXXXXX")" && pwd -P)
EXTRA_DIRS+=("$OTHER")
git -C "$OTHER" init -q
OHASH=$(printf %s "$OTHER" | sha | cut -c1-8)
fake_config "# none" 'allow = [".github.com"]'
loop=${loop//example.com/example.net}
(cd "$OTHER" && fake_pen run bash -c "$loop; sleep 4") >"$SCRATCH/global.out" 2>&1 & # kept up so its proxy's logs can be read
g=$!
wait_for "^pen-.*-$OHASH-.*-agent$" || fail "the other project's session didn't start"
out=$(HOME=$FAKE_HOME DOCKER_CONFIG=${DOCKER_CONFIG:-$REAL_HOME/.docker} "$PEN" allow --global example.net 2>&1)
oproxy=$(docker ps --format '{{.Names}}' | grep -E "^pen-.*-$OHASH-.*-proxy-1$" | head -1)
sleep 1
oproxy_log=$(docker logs "$oproxy" 2>&1 || true)
wait "$g" || true
check "pen allow --global adds to ~/.config/pen/config.toml, keeping what was there" \
  bash -c 'grep -qx "  \"example.net\"," "$1" && grep -qx "# none" "$1" && grep -q "\.github\.com" "$1" && [ "$(stat -c %a "$1" 2>/dev/null || stat -f %Lp "$1")" = 600 ]' _ "$FAKE_HOME/.config/pen/config.toml"
check "...and reaches a running session of another project" bash -c 'grep -q "applied to 1 running session" <<<"$1" && grep -q got-200 "$2"' _ "$out" "$SCRATCH/global.out"
check "...whose reconfigure with .github.com plus api.github.com is clean" \
  bash -c 'grep -q "Reconfiguring Squid Cache" <<<"$1" && ! grep -qE "is a subdomain of|You should remove|FATAL" <<<"$1"' _ "$oproxy_log"
# (A reconfigured Squid, no longer root, logs "ERROR: Cannot open cache_log
# (/dev/stderr)" and keeps writing to stderr anyway: harmless.)
fi

if want 16; then
# ==============================================================================
section "16. pen allow with no arguments"

"$PEN" run bash -c 'curl -s -o /dev/null -I https://example.edu; sleep 1.5; curl -s -o /dev/null -I https://example.org; echo READY; sleep 120' \
  </dev/null >"$SCRATCH/list.out" 2>&1 &
p=$!
for i in $(seq 300); do grep -q READY "$SCRATCH/list.out" 2>/dev/null && break; sleep 0.1; done
out=$("$PEN" allow 2>/dev/null)
check "lists blocked domains while the session runs" has_all "$out" example.org example.edu
check "...newest first" test "$(grep -oE 'example\.(org|edu)' <<<"$out" | head -1)" = example.org
check "...with the line to paste" contains "pen allow example.org example.edu" "$out"
want 15 && check "...leaving out domains allowed since (example.com, by section 15)" not contains "example.com" "$out"
kill -9 "$p"; wait "$p" 2>/dev/null || true
out=$("$PEN" allow 2>&1)
check "...after a SIGKILL'd session (which it ends)" bash -c 'grep -q "example.org" <<<"$1" && grep -q "whose pen exited" <<<"$1"' _ "$out"
check "...and once the session has exited normally" contains "example.org" "$(in_sandbox true 2>/dev/null; "$PEN" allow 2>/dev/null)"
fi

if want 17; then
# ==============================================================================
section "17. Home seed"

SEEDP=$(cd "$(mktemp -d "$HOME/.pen-smoke-seed.XXXXXX")" && pwd -P)
EXTRA_DIRS+=("$SEEDP")
git -C "$SEEDP" init -q
git -C "$SEEDP" -c user.name=x -c user.email=x@example.invalid -c commit.gpgsign=false commit -q --allow-empty -m init
fake_config "# none"
[ -d "$seed" ] || fake_pen run true >/dev/null 2>&1 # section 8 makes it
slug=$(printf %s "$SEEDP" | LC_ALL=C sed 's/[^A-Za-z0-9]/-/g')
mkdir -p "$seed/.claude/projects/$slug/memory" "$seed/.claude/projects/-elsewhere/memory"
echo "seeded memory" >"$seed/.claude/projects/$slug/memory/MEMORY.md"
echo "not yours" >"$seed/.claude/projects/-elsewhere/memory/MEMORY.md"
seed_pen() { (cd "$SEEDP" && fake_pen run bash -c "$1" 2>&1); }

out=$(seed_pen '
  grep -q "running in a pen sandbox" ~/.claude/CLAUDE.md && echo claude-note
  grep -q "running in a pen sandbox" ~/.codex/AGENTS.md && echo codex-note
  echo "mcp=$(jq -c ".mcpServers | keys" ~/.claude.json 2>/dev/null || echo none)"
  echo "mode=$(jq -r .permissions.defaultMode ~/.claude/settings.json)"
  echo "attribution=$(jq -c .attribution ~/.claude/settings.json)"
  grep -q "^approval_policy = \"never\"" ~/.codex/config.toml && echo codex-never
  grep -q "mcp_servers" ~/.codex/config.toml || echo codex-no-mcp
  echo "projects=$(ls ~/.claude/projects | tr "\n" " ")"
  echo "memory=$(cat ~/.claude/projects/*/memory/MEMORY.md)"
  git commit -q --allow-empty -m "sandbox commit" && echo committed
  mkdir -p .codegraph && touch .codegraph/db && echo "status=[$(git status --porcelain)]"
  echo "local edit" >>~/.claude/CLAUDE.md
  touch -d "next year" ~/.claude/CLAUDE.md
  printf "%s" "project login" >~/.codex/auth.json
  printf "%s" "claude login" >~/.claude/.credentials.json
  echo "{\"oauthAccount\": {\"smoke\": true}, \"mcpServers\": {\"from-sandbox\": {\"command\": \"true\"}}}" >~/.claude.json
') || { printf '%s\n' "$out" >&2; fail "the first seeded session failed"; }
has() { grep -qxF -- "$1" <<<"$out"; }
check "a new project's first session has the seeded CLAUDE.md and AGENTS.md" bash -c 'grep -qx claude-note <<<"$1" && grep -qx codex-note <<<"$1"' _ "$out"
check "...and no MCP servers for Claude (no .claude.json in the seed)" has "mcp=none"
check "...or Codex" has codex-no-mcp
check "...Claude's default mode is bypassPermissions" has "mode=bypassPermissions"
check "...with Claude's own attribution defaults (none seeded)" has "attribution=null"
check "...and Codex runs without approvals" has codex-never
check "only this project's .claude/projects/<slug>/ is copied" has "projects=$slug "
check "...with its memory" has "memory=seeded memory"
check "a sandbox commit has the seeded author" \
  test "$(git -C "$SEEDP" log -1 --format='%an <%ae>')" = "Smoke Host <smoke-host@example.invalid>"
check "...and no co-author trailer" not grep -qi co-authored-by <<<"$(git -C "$SEEDP" log -1 --format=%B)"
check "git status in the sandbox doesn't list .codegraph/" has "status=[]"

out=$(seed_pen 'tail -1 ~/.claude/CLAUDE.md; echo "mcp=$(jq -c ".mcpServers | keys" ~/.claude.json)"; cat ~/.codex/auth.json; echo; cat ~/.claude/.credentials.json; echo')
check "seed settings overwrite sandbox edits even with a future mtime" not has "local edit"
check "...without changing the seed" not grep -q "local edit" "$seed/.claude/CLAUDE.md"
check "...or another project" not contains "local edit" "$(fake_pen run tail -1 /home/agent/.claude/CLAUDE.md 2>/dev/null)"
check "with no .claude.json in the seed, Claude's MCP servers are the sandbox's own" has 'mcp=["from-sandbox"]'
check "Codex credentials survive synchronization" has 'project login'
check "Claude credentials survive synchronization" has 'claude login'

# Replace files by rename, as editors save: on OrbStack, a host file rewritten in place
# can look unchanged to a new container for several seconds.
echo "added later" >"$seed/.added-later"
{ cat "$seed/.claude/CLAUDE.md"; echo "seed v2"; } >"$seed/.claude/CLAUDE.md.new" && mv "$seed/.claude/CLAUDE.md.new" "$seed/.claude/CLAUDE.md"
printf '%s' '{"mcpServers": {"seeded-later": {"type": "stdio", "command": "true"}}}' >"$seed/.claude.json.new" \
  && mv "$seed/.claude.json.new" "$seed/.claude.json"
out=$(seed_pen 'cat ~/.added-later; tail -1 ~/.claude/CLAUDE.md; echo "mcp=$(jq -c ".mcpServers | keys" ~/.claude.json)"; echo "login=$(jq -c .oauthAccount ~/.claude.json)"')
check "a file added to the seed later reaches the project's next session" has "added later"
check "editing a seed file replaces the project's copy" has "seed v2"
check "a .claude.json added to the seed owns an existing project's whole MCP map" has 'mcp=["seeded-later"]'
check "...and the project stays logged in (the rest of .claude.json is kept)" has 'login={"smoke":true}'
seed_pen 'rm ~/.codex/auth.json ~/.claude/.credentials.json' >/dev/null

if [ -n "${CLAUDE_CODE_OAUTH_TOKEN:-}" ]; then
  fake_config "[env]" "CLAUDE_CODE_OAUTH_TOKEN = \"$CLAUDE_CODE_OAUTH_TOKEN\""
  (cd "$SEEDP" && fake_pen run timeout 300 claude -p 'Create a file named perm-check.txt containing "ok", then commit it with git (message "perm check"). Reply "done".' >/dev/null 2>&1) || true
  check "claude starts in skip-permissions mode (wrote and committed without asking)" test -f "$SEEDP/perm-check.txt"
  check "...and its commit has the seeded author" test "$(git -C "$SEEDP" log -1 --format="%an")" = "Smoke Host"
  out=$(cd "$SEEDP" && fake_pen run timeout 300 claude -p 'Fetch https://example.com and show me its title.' 2>&1 || true)
  check "claude reports the blocked domain" grep -qiE 'example\.com.*(block|allow|proxy|403)|(block|allow|proxy|403).*example\.com' <<<"$out"
else
  note "claude skip-permissions, co-author and blocked-domain checks: skipped (set CLAUDE_CODE_OAUTH_TOKEN)"
fi
if [ -n "${OPENAI_API_KEY:-}" ]; then
  fake_config "[env]" "OPENAI_API_KEY = \"$OPENAI_API_KEY\""
  out=$(cd "$SEEDP" && fake_pen run timeout 300 codex exec 'Fetch https://example.com and show me its title.' 2>&1 || true)
  check "codex reports the blocked domain" grep -qiE 'example\.com.*(block|allow|proxy|403)|(block|allow|proxy|403).*example\.com' <<<"$out"
else
  note "codex blocked-domain check: skipped (set OPENAI_API_KEY)"
fi
note "with tools = [\"codegraph\"] and its MCP entry in the seed, codegraph_explore from Claude after codegraph init is a manual check (needs a login)"
fi

if want 18; then
# ==============================================================================
section "18. pen reset"

in_sandbox 'echo kept >~/.reset-marker'
"$PEN" run sleep 120 </dev/null >/dev/null 2>&1 &
p=$!
wait_for "^$KEY-.*-agent$" || fail "session didn't start"
name=$(docker ps --format '{{.Names}}' | grep -E "^$KEY-.*-agent$" | head -1)
out=$(echo y | "$PEN" reset 2>&1 || true)
check "pen reset refuses while a session is open" contains "refusing to reset while a session is running" "$out"
check "...naming its agent container" contains "  $name" "$out"
check "...and removes nothing" bash -c 'docker volume inspect "$1" >/dev/null' _ "${KEY}_home"
docker rm -f "$name" >/dev/null
wait "$p" 2>/dev/null || true
check "after docker rm -f, that session's pen finishes its end path" test "$(containers)" = 0
out=$(echo n | "$PEN" reset 2>&1 || true)
check "pen reset asks before removing, echoing the answer read without a terminal" contains "pen: remove them? [y/N] n" "$out"
check "answering no removes nothing" bash -c 'grep -q "nothing removed" <<<"$1" && docker volume inspect "$2" >/dev/null' _ "$out" "${KEY}_home"
out=$(echo y | "$PEN" reset 2>&1)
check "answering yes removes the project's volumes" bash -c 'grep -q "removed ${2}_home, ${2}_nuget" <<<"$1" && ! docker volume inspect "${2}_home" >/dev/null 2>&1' _ "$out" "$KEY"
check "...so the next session starts with a fresh home" test "$(in_sandbox 'cat ~/.reset-marker 2>/dev/null || echo fresh')" = fresh
note "reset logging out of claude and codex is a manual check"
fi

if want 19; then
# ==============================================================================
section "19. Unsigned-commit reminder and pen sign"

rm -rf .pen # section 15 leaves an allow that this HOME hasn't approved
ssh-keygen -q -t ed25519 -N '' -C pen-smoke -f "$SCRATCH/signkey"
git config --file "$FAKE_HOME/.gitconfig" gpg.format ssh
git config --file "$FAKE_HOME/.gitconfig" user.signingkey "$SCRATCH/signkey"
git config --file "$FAKE_HOME/.gitconfig" gpg.ssh.program ssh-keygen
fake_config "# none"
git init -q --bare "$SCRATCH/origin.git"
git remote add origin "$SCRATCH/origin.git"
git push -q origin HEAD:refs/heads/smoke-base
git branch -q -u origin/smoke-base
git config commit.gpgsign true # the repo's own setting doesn't matter, only the host's global one

out=$(fake_pen run git -c user.name=Agent -c user.email=agent@example.invalid commit -q --allow-empty -m s1 2>&1) || fail "session in section 19 refused: $out"
check "no reminder when the host's global commit.gpgsign is off" not contains "unsigned commit" "$out"
git config --file "$FAKE_HOME/.gitconfig" commit.gpgsign true
out=$(fake_pen run git -c user.name=Agent -c user.email=agent@example.invalid commit -q --allow-empty -m s2 2>&1)
check "with it on, session end counts the unsigned commits" contains "2 unsigned commits; review, then run pen sign" "$out"
git config --unset commit.gpgsign

fake_pen run sleep 120 >/dev/null 2>&1 & # pen sign below runs with this HOME too, so this is its project's session
p=$!
wait_for "^$KEY-.*-agent$" || fail "session didn't start"
name=$(docker ps --format '{{.Names}}' | grep -E "^$KEY-.*-agent$" | head -1)
out=$(fake_pen sign 2>&1 || true)
check "pen sign refuses while a session is open" contains "refusing to sign while a session is running" "$out"
check "...naming its agent container" contains "  $name" "$out"
docker rm -f "$name" >/dev/null
wait "$p" 2>/dev/null || true

fake_runtime=$FAKE_HOME/.local/state/pen/$KEY
fake_env=(env HOME="$FAKE_HOME" DOCKER_CONFIG="${DOCKER_CONFIG:-$REAL_HOME/.docker}" "$PEN")
# pen sign holds this dir's worktree lock exclusive (and project.lock shared) for its whole run.
out=$(holding "$fake_runtime/worktree-$HASH.lock" exclusive "${fake_env[@]}" run true)
check "a session started while pen sign runs here is refused" contains "a pen sign is running in this directory" "$out"
out=$(holding "$fake_runtime/worktree-$HASH.lock" exclusive "${fake_env[@]}" sign)
check "...as is a second pen sign here" contains "another pen sign is running in this directory" "$out"
out=$(holding "$fake_runtime/project.lock" shared "${fake_env[@]}" reset)
check "...and pen reset (sign holds project.lock shared)" contains "a pen sign or another pen reset is running" "$out"
# pen reset holds project.lock exclusive.
out=$(holding "$fake_runtime/project.lock" exclusive "${fake_env[@]}" run true)
check "a session started while pen reset runs is refused" contains "a pen reset is running in this project" "$out"
out=$(holding "$fake_runtime/project.lock" exclusive "${fake_env[@]}" sign)
check "...as is pen sign" contains "refusing to sign: a pen reset is running" "$out"

before=$(for c in $(git rev-list --reverse origin/smoke-base..HEAD); do git log -1 --format='%T %an <%ae> %ad' "$c"; done)
out=$(fake_pen sign 2>&1)
check "pen sign lists the commits it rewrites" has_all "$out" "re-signing 2 commits in origin/smoke-base..HEAD" " s1" " s2"
check "...signs each of them" bash -c 'for c in $(git rev-list origin/smoke-base..HEAD); do git cat-file commit "$c" | grep -q "^gpgsig" || exit 1; done'
check "...keeping their trees, authors and dates" \
  test "$(for c in $(git rev-list --reverse origin/smoke-base..HEAD); do git log -1 --format='%T %an <%ae> %ad' "$c"; done)" = "$before"
check "...with the host user as committer" test "$(git log -1 --format=%cn)" = "Smoke Host"
check "...and the signatures verify" bash -c 'echo "smoke-host@example.invalid $(cat "$1.pub")" >"$1.signers" && git -c gpg.ssh.program=ssh-keygen -c gpg.ssh.allowedSignersFile="$1.signers" verify-commit HEAD 2>/dev/null' _ "$SCRATCH/signkey"
check "with no allowed signers file, it says how to fix that" has_all "$out" "can't verify the signatures just made" "gpg.ssh.allowedSignersFile isn't set"
before=$(git rev-parse HEAD)
sleep 1.1 # so a re-signed commit gets a new committer date, and so a new hash
out=$(fake_pen sign 2>&1)
check "...and each pen sign re-signs" test "$(git rev-parse HEAD)" != "$before"
check "...saying so again" contains "can't verify the signatures just made" "$out"
git config --file "$FAKE_HOME/.gitconfig" gpg.ssh.allowedSignersFile "$SCRATCH/signkey.signers"
check "the reminder is gone after signing" not contains "unsigned" "$(fake_pen run true 2>&1)"
before=$(git rev-parse HEAD)
check "running pen sign again changes nothing" contains "are signed" "$(fake_pen sign 2>&1)"
check "...keeping the validly signed commits' hashes" test "$(git rev-parse HEAD)" = "$before"

# A commit with any signature header, which is all Pen used to look at.
forged=$(printf 'tree %s\nparent %s\nauthor Agent <agent@example.invalid> 1700000000 +0000\ncommitter Agent <agent@example.invalid> 1700000000 +0000\ngpgsig -----BEGIN SSH SIGNATURE-----\n Zm9v\n -----END SSH SIGNATURE-----\n\nforged\n' \
  "$(git rev-parse 'HEAD^{tree}')" "$before" | git hash-object -t commit -w --stdin)
git update-ref HEAD "$forged"
out=$(fake_pen sign 2>&1)
check "a forged signature header is re-signed" contains "re-signing 3 commits" "$out"
check "...and verifies now" bash -c 'git -c gpg.ssh.program=ssh-keygen -c gpg.ssh.allowedSignersFile="$1.signers" verify-commit HEAD 2>/dev/null' _ "$SCRATCH/signkey"
check "...leaving the valid ones below it" test "$(git rev-parse HEAD~1)" = "$before"

# The agent's own key, trusted by the repo's config, doesn't count.
ssh-keygen -q -t ed25519 -N '' -C pen-smoke-agent -f "$SCRATCH/agentkey"
echo "agent@example.invalid $(cat "$SCRATCH/agentkey.pub")" >"$SCRATCH/agentkey.signers"
git config gpg.ssh.allowedSignersFile "$SCRATCH/agentkey.signers"
git -c user.name=Agent -c user.email=agent@example.invalid -c gpg.format=ssh -c gpg.ssh.program=ssh-keygen -c user.signingkey="$SCRATCH/agentkey" commit -q -S --allow-empty -m "agent-signed"
forged=$(git rev-parse HEAD)
check "a commit signed by the agent verifies by the repo's allowedSignersFile" test "$(git -c gpg.ssh.program=ssh-keygen log -1 --format=%G?)" = G
out=$(fake_pen sign 2>&1)
check "...but pen sign ignores that file and re-signs it" contains "agent-signed" "$out"
check "...with the host's key" bash -c 'git -c gpg.ssh.program=ssh-keygen -c gpg.ssh.allowedSignersFile="$1.signers" verify-commit HEAD 2>/dev/null' _ "$SCRATCH/signkey"
check "...replacing it" test "$(git rev-parse HEAD)" != "$forged"
git config --unset gpg.ssh.allowedSignersFile

fake_pen run git -c user.name=Agent -c user.email=agent@example.invalid commit -q --allow-empty -m s3 >/dev/null 2>&1
git update-ref refs/remotes/fork/s3 HEAD
out=$(fake_pen sign 2>&1 || true)
check "pen sign refuses when a commit is already on a remote branch" contains "on a remote branch" "$out"
git update-ref -d refs/remotes/fork/s3
mkdir .git/rebase-merge
echo 'exec planted-by-a-rebase' >.git/rebase-merge/git-rebase-todo
out=$(fake_pen sign 2>&1 || true)
check "pen sign reports a paused rebase with its exec line" has_all "$out" ".git/rebase-merge: a paused rebase" "exec planted-by-a-rebase"
check "...and doesn't refuse over it" not contains "not signing" "$out"
check "...and leaves it in place" test -f .git/rebase-merge/git-rebase-todo
rm -rf .git/rebase-merge
mkdir .git/rebase-merge
echo '$(touch prompt-canary)' >.git/rebase-merge/msgnum
out=$(fake_pen sign 2>&1 || true)
check "pen sign refuses over a planted count, having quarantined it" has_all "$out" "msgnum.pen-quarantine" "not signing"
rm -rf .git/rebase-merge
git remote remove origin
note "GitHub showing the signed commits as Verified after a push is a manual check"
fi

if want 20; then
# ==============================================================================
section "20. Docker-in-Docker"

pen_docker() { "$PEN" --docker run "$@" </dev/null; }
rm -rf .pen && mkdir .pen

t0=$(now)
out=$(pen_docker docker run --rm hello-world 2>&1 || true)
t1=$(now)
check "pen --docker: docker run hello-world works" contains "Hello from Docker!" "$out"
check "...and no dind is left after exit" test "$(containers)" = 0
check "...nor its network" test -z "$(docker network ls -q --filter "name=^$KEY-")"
check "the project's dind volume is named by its key" bash -c 'docker volume inspect "$1" >/dev/null' _ "${KEY}_dind"
t2=$(now); pen_docker true; t3=$(now)
note "docker session start: first $(python3 -c "print(f'{$t1 - $t0:.1f}')")s (with a pull), warm $(python3 -c "print(f'{$t3 - $t2:.1f}')")s"

out=$(pen_docker bash -c '
  t() { if docker run --rm -v "$PWD:/src" alpine sh -c "$1" >/dev/null 2>&1; then echo "ok: $2"; else echo "fail: $2"; fi; }
  t "echo x >> /src/.git/config" config
  t "echo x > /src/.git/hooks/pre-commit" hook
  t "touch /src/.pen/x" pen
  t "mv /src/.git /src/.git2" mvgit
  t "echo inner > /src/inner-file" write
' 2>&1)
has() { grep -qxF -- "$1" <<<"$out"; }
check "an inner container (-v \$PWD:/src) can't write .git/config" has "fail: config"
check "...or .git/hooks" has "fail: hook"
check "...or .pen/" has "fail: pen"
check "...or move .git" has "fail: mvgit"
check "...but can write the project" bash -c 'grep -qxF "ok: write" <<<"$1" && [ "$(cat inner-file)" = inner ]' _ "$out"
owner=$(stat -c %u inner-file 2>/dev/null || stat -f %u inner-file)
if [ -n "$ROOTLESS" ] || [ "$(uname -s)" = Darwin ]; then
  check "...and the file an inner root container wrote belongs to the host user" test "$owner" = "$(id -u)"
else
  note "rootful Linux: the file an inner root container wrote is owned by UID $owner (expected: root; see docs/usage.md#docker-in-docker)"
fi
rm -f inner-file 2>/dev/null || pen_docker docker run --rm -v "$PROJECT_DIR:/src" alpine rm -f /src/inner-file >/dev/null 2>&1 || true

mkdir -p dind-compose
printf 'hello-compose\n' >dind-compose/msg
printf 'services:\n  cat:\n    image: alpine\n    volumes: ["./:/w:ro"]\n    command: ["cat", "/w/msg"]\n' >dind-compose/compose.yaml
out=$(pen_docker bash -c 'cd dind-compose && docker compose run --rm -T cat; docker compose down -t 1' 2>&1 || true)
check "a compose stack with a bind mount from the project works" contains "hello-compose" "$out"
rm -rf dind-compose

printf 'FROM curlimages/curl\nRUN curl -sSf -o /dev/null https://github.com && echo allowed-ok\nRUN curl -sSf -o /dev/null https://example.com && echo SHOULD-FAIL || echo blocked-ok\n' >Dockerfile.dind-smoke
out=$(pen_docker docker build --no-cache --progress plain -f Dockerfile.dind-smoke . 2>&1 || true)
rm -f Dockerfile.dind-smoke
check "a docker build RUN step reaches an allowed domain" grep -qE '^#[0-9]+ [0-9.]+ allowed-ok' <<<"$out"
check "...and not one that isn't allowed" bash -c 'grep -qE "^#[0-9]+ [0-9.]+ blocked-ok" <<<"$1" && ! grep -qE "^#[0-9]+ [0-9.]+ SHOULD-FAIL" <<<"$1"' _ "$out"
check "...which shows up in the blocked line" contains "blocked: example.com" "$out"
out=$(pen_docker docker run --rm curlimages/curl -s -o /dev/null -w '%{http_code}' -I https://github.com 2>&1 || true)
check "a container inside dind reaches an allowed domain through the proxy" contains "200" "$out"

rc=0; pen_run docker ps >/dev/null 2>&1 || rc=$?
check "without --docker, docker ps fails" test "$rc" != 0
check "...and the agent has no DOCKER_HOST" test "$(in_sandbox 'echo "[${DOCKER_HOST:-}]"' 2>/dev/null)" = "[]"

printf 'FROM curlimages/curl\nRUN curl -sSf -o /dev/null https://github.com\n' >Dockerfile.dind-smoke
"$PEN" --docker run bash -c 'for i in $(seq 600); do [ -e .dind-go ] && break; sleep 0.1; done
  docker build -q --no-cache -f Dockerfile.dind-smoke . >/dev/null 2>&1 && echo build-after-plain:ok || echo build-after-plain:fail
  sleep 120' </dev/null >"$SCRATCH/dind-bg.out" 2>&1 &
p=$!
wait_for "^$KEY-.*-agent$" || fail "docker session didn't start"
check "the docker session's dind is running" wait_for "^$KEY-.*-dind$"
check "the dind lock is held" not lock_free "$RUNTIME/dind.lock" exclusive
out=$(in_sandbox 'curl -s --max-time 5 http://dind:2375/_ping && echo reached || echo unreachable' 2>&1)
check "a plain session alongside can't reach dind:2375" contains "unreachable" "$out"
rc=0; out=$(pen_docker true 2>&1) || rc=$?
check "a second --docker session is refused" bash -c '[ "$1" != 0 ] && grep -q "another --docker session is running" <<<"$2"' _ "$rc" "$out"
touch .dind-go
for i in $(seq 900); do grep -q build-after-plain "$SCRATCH/dind-bg.out" && break; sleep 0.1; done
check "after a plain session came and went, the docker session's builds still work" grep -q build-after-plain:ok "$SCRATCH/dind-bg.out"
kill -9 "$p"; wait "$p" 2>/dev/null || true
rm -f .dind-go Dockerfile.dind-smoke
check "after a SIGKILL'd docker session, its dind is still up" wait_for "^$KEY-.*-dind$"
out=$(pen_docker true 2>&1 || true)
check "the next --docker session ends it and starts" contains "whose pen exited without ending it" "$out"
check "...leaving nothing running" test "$(containers)" = 0

docker run -d --name "$KEY-smoke-stale" --entrypoint sleep -v "${KEY}_dind:/x" "$IMAGE" 120 >/dev/null
rc=0; out=$(pen_docker true 2>&1) || rc=$?
check "a container still using the dind volume is refused, named" bash -c '[ "$1" != 0 ] && grep -q "$2 still uses" <<<"$3"' _ "$rc" "$KEY-smoke-stale" "$out"
docker rm -f "$KEY-smoke-stale" >/dev/null
check "...and the dind lock is free again" lock_free "$RUNTIME/dind.lock" exclusive

out=$(pen_docker bash -c 'docker run --rm -v "$PWD:/src" alpine touch /src/.git/commondir' 2>&1 || true)
check "an inner container's .git/commondir is quarantined at session end" \
  bash -c '[ ! -e .git/commondir ] && [ -e .git/commondir.pen-quarantine ] && grep -q "pen: WARNING" <<<"$1"' _ "$out"
rm -f .git/commondir.pen-quarantine

echo 'docker = true' >.pen/config.toml
check "docker = true isn't used until approved" contains "docker = true" "$(pen_run true 2>&1 || true)"
"$PEN" trust --yes </dev/null >/dev/null 2>&1
out=$(pen_run bash -c 'echo "[$DOCKER_HOST]"; docker version --format "server {{.Server.Version}}"' 2>&1 || true)
check "docker = true in .pen/config.toml turns dind on" has_all "$out" "[tcp://dind:2375]" "server "
echo 'docker = "maybe"' >.pen/config.toml
rc=0; out=$(pen_run true 2>&1) || rc=$?
check "...and a bad value names the file" bash -c '[ "$1" != 0 ] && grep -q "^pen: .pen/config.toml: docker must be true or false" <<<"$2"' _ "$rc" "$out"
rm -f .pen/config.toml
check "nothing is left running" test "$(containers)" = 0
fi

if want 21; then
# ==============================================================================
section "21. Linked worktrees"

rm -rf .pen # section 15 leaves an allow that the throwaway HOME hasn't approved
git worktree add -q -b smoke-wt "$SCRATCH/wt21" 2>/dev/null
WT=$(cd "$SCRATCH/wt21" && pwd -P)
WT_HASH=$(printf %s "$WT" | sha | cut -c1-8)
base=$(git -C "$WT" rev-parse HEAD)
main_head=$(git rev-parse HEAD)
echo main-only >main-only.txt
G=$PROJECT_DIR/.git

out=$(cd "$WT" && in_sandbox "
  t() { if eval \"\$1\" >/dev/null 2>&1; then echo \"ok: \$2\"; else echo \"fail: \$2\"; fi; }
  t 'git -c user.name=a -c user.email=a@example.invalid commit -q --allow-empty -m in-wt' commit
  t 'git checkout -q -b smoke-wt2 && git checkout -q smoke-wt' checkout
  t 'echo x >> \"$G/config\"' configwrite
  t 'git config core.fsmonitor x' config
  t 'echo x >> \"$G/hooks/pre-commit\"' hook
  t 'echo \"gitdir: /tmp\" > .git' gitfile
  t 'mv \"$G\" \"$G.2\"' mvcommon
  t 'echo 0000000000000000000000000000000000000000 > \"$G/HEAD\"' mainhead
  t 'test \"\$(ls -A \"$PROJECT_DIR\")\" = .git' mainhidden
  t 'touch .pen/x' pen
" 2>&1)
has() { grep -qxF -- "$1" <<<"$out"; }
check "pen starts in a linked worktree, and git commit works there" has "ok: commit"
check "...as does switching branches (its own worktrees/ entry is writable)" has "ok: checkout"
check "...and the host sees the commit" test "$(git log -1 --format=%s smoke-wt)" = in-wt
check "the session printed no warning" not contains "WARNING" "$out"
check "writing the common .git/config fails" has "fail: configwrite"
check "...as does git config" has "fail: config"
check "writing a hook in the common .git/hooks fails" has "fail: hook"
check "writing the worktree's .git file fails" has "fail: gitfile"
check "mv of the common git dir fails" has "fail: mvcommon"
check "the main checkout's HEAD is read-only" has "fail: mainhead"
check "...and stays as it was" test "$(git rev-parse HEAD)" = "$main_head"
check "the main checkout's files aren't visible, only its .git" has "ok: mainhidden"
check "the worktree's own .pen/ is created" test -d "$WT/.pen"
check "...and read-only inside" has "fail: pen"
check "the worktree shares the main checkout's key: no state of its own" \
  bash -c "! ls -d '$REAL_HOME'/.local/state/pen/pen-*-'$WT_HASH' >/dev/null 2>&1 && ! docker volume inspect pen-wt21-'$WT_HASH'_home >/dev/null 2>&1"
check "...and its session ran under the main checkout's runtime dir, with its own worktree lock" test -e "$RUNTIME/worktree-$WT_HASH.lock"
check "...whose project-dir still names the main checkout (prune D1)" test "$(cat "$RUNTIME/project-dir")" = "$PROJECT_DIR"
git -C "$WT" branch -q -D smoke-wt2

out=$(cd "$WT" && in_sandbox "touch '$G/commondir'" 2>&1 || true)
check "a commondir planted in the common dir is quarantined when a worktree session ends" \
  bash -c "[ ! -e '$G/commondir' ] && [ -e '$G/commondir.pen-quarantine' ]"
check "...with the warning" contains "pen: WARNING" "$out"
check "...and the worktree's .git file is left alone" test -f "$WT/.git"
rm -f "$G/commondir.pen-quarantine"

cd "$WT"
"$PEN" run bash -c "touch '$G/commondir'; sleep 120" </dev/null >/dev/null 2>&1 &
p=$!
cd "$PROJECT_DIR"
wait_for "^$KEY-.*-agent$" || fail "worktree session didn't start"
for i in $(seq 100); do [ -e "$G/commondir" ] && break; sleep 0.1; done
kill -9 "$p"; wait "$p" 2>/dev/null || true
out=$(pen_run true 2>&1)
check "a pen in the main checkout ends a dead worktree session" contains "whose pen exited without ending it" "$out"
check "...checking it with its own saved dirs (the commondir is quarantined)" \
  bash -c "[ ! -e '$G/commondir' ] && [ -e '$G/commondir.pen-quarantine' ]"
check "...and takes its stack down" test "$(containers)" = 0
rm -f "$G/commondir.pen-quarantine"

# Parallel sessions, pen ps and pen sign, all with the throwaway HOME (one runtime dir).
[ -e "$SCRATCH/signkey" ] || ssh-keygen -q -t ed25519 -N '' -C pen-smoke -f "$SCRATCH/signkey"
git config --file "$FAKE_HOME/.gitconfig" gpg.format ssh
git config --file "$FAKE_HOME/.gitconfig" user.signingkey "$SCRATCH/signkey"
git config --file "$FAKE_HOME/.gitconfig" gpg.ssh.program ssh-keygen
fake_config "# none"
(cd "$WT" && fake_pen run sleep 120 >/dev/null 2>&1) &
p_wt=$!
wait_for "^$KEY-.*-agent$" || fail "worktree session didn't start"
name_wt=$(docker ps --format '{{.Names}}' | grep -E "^$KEY-.*-agent$" | head -1)
fake_pen run sleep 120 >/dev/null 2>&1 &
p_main=$!
check "sessions run in parallel in the main checkout and the worktree" wait_count "^$KEY-.*-agent$" 2
name_main=$(docker ps --format '{{.Names}}' | grep -E "^$KEY-.*-agent$" | grep -vxF "$name_wt" | head -1)
out=$(fake_pen ps)
check "pen ps lists both" bash -c 'grep -qE "^$1 +[0-9a-f]{8} .*sleep 120$" <<<"$3" && grep -qE "^$2 +[0-9a-f]{8} .*sleep 120$" <<<"$3"' _ "$PROJECT_DIR" "$WT" "$out"
out=$(cd "$WT" && fake_pen allow example.org 2>&1)
check "pen allow in the worktree adds to the worktree's .pen/config.toml only" \
  bash -c 'grep -qx "  \"example.org\"," "$1/.pen/config.toml" && ! grep -qs example.org .pen/config.toml' _ "$WT"
check "...and applies only to the worktree's session, not the main checkout's" contains "applied to 1 running session in this directory" "$out"
fsess=$FAKE_HOME/.local/state/pen/$KEY/sessions
check "...whose allowlist alone now has the domain" \
  bash -c 'n=0; for d in "$1"/*/; do [ -e "$d/compose.yaml" ] || continue; grep -qx "PROJECT_DIR=$2" "$d/session.env" && w=1 || w=; grep -q "example\.org" "$d/allowlist.txt" && a=1 || a=; [ "$w" = "$a" ] || exit 1; n=$((n+1)); done; [ "$n" = 2 ]' _ "$fsess" "$WT"
out=$(cd "$WT" && fake_pen sign "$base" 2>&1 || true)
check "pen sign in the worktree is refused while the worktree has a session" contains "refusing to sign while a session is running in this directory" "$out"
check "...naming only that session" bash -c 'grep -qF "  $1" <<<"$3" && ! grep -qF "$2" <<<"$3"' _ "$name_wt" "$name_main" "$out"
docker rm -f "$name_wt" >/dev/null
wait "$p_wt" 2>/dev/null || true
out=$(cd "$WT" && fake_pen sign "$base" 2>&1)
check "pen sign in the worktree succeeds while the main checkout has a session" contains "re-signing 1 commit" "$out"
check "...and signs the worktree's branch" bash -c "git cat-file commit smoke-wt | grep -q '^gpgsig'"
check "...leaving the main checkout's HEAD alone" test "$(git rev-parse HEAD)" = "$main_head"
docker rm -f "$name_main" >/dev/null
wait "$p_main" 2>/dev/null || true

tool_free # without tools, the layer is <key>-<dir hash>:latest
mkdir -p "$WT/.pen"; echo 'FROM pen:latest' >"$WT/.pen/Dockerfile"
check "a worktree's .pen/Dockerfile needs its own approval" contains "pen trust" "$(cd "$WT" && "$PEN" run true </dev/null 2>&1 || true)"
(cd "$WT" && "$PEN" trust --yes </dev/null >/dev/null 2>&1)
check "a worktree's .pen/Dockerfile builds its own layer, $KEY-$WT_HASH:latest" \
  bash -c "cd '$WT' && '$PEN' run true </dev/null >/dev/null 2>&1 && docker image inspect '$KEY-$WT_HASH:latest' >/dev/null 2>&1"
docker image rm -f "$KEY-$WT_HASH:latest" >/dev/null 2>&1 || true
real_home

# A worktree the agent makes, with config in its entry: host git in it would run this.
hook21=$SCRATCH/hook21
canary21=$SCRATCH/canary21
printf '#!/bin/sh\ntouch %s\n' "$canary21" >"$hook21"
chmod +x "$hook21"
plant21="git worktree add -q --detach forged21 && printf '[core]\n\tfsmonitor = $hook21\n' >'$G/worktrees/forged21/config.worktree'"
fires21() { rm -f "$canary21"; git -C forged21 status >/dev/null 2>&1; [ -e "$canary21" ]; }
unforge21() { chmod -R u+rwx "$G/worktrees" 2>/dev/null; rm -rf forged21 "$G/worktrees/forged21"*; git worktree prune; }
git config extensions.worktreeConfig true
for mode in "" 555 311 000; do
  out=$(in_sandbox "$plant21${mode:+ && chmod $mode '$G/worktrees/forged21'}" 2>&1)
  check "a config.worktree in a worktree the agent made (entry mode ${mode:-as made}) is quarantined" \
    test ! -e "$G/worktrees/forged21/config.worktree" -a -e "$G/worktrees/forged21/config.worktree.pen-quarantine"
  check "...with the warning" contains "pen: WARNING" "$out"
  check "...and git there no longer runs it" not fires21
  unforge21
done

"$PEN" run bash -c "$plant21; sleep 120" </dev/null >/dev/null 2>&1 &
p_a=$!
wait_for "^$KEY-.*-agent$" || fail "planting session didn't start"
for i in $(seq 100); do [ -e "$G/worktrees/forged21/config.worktree" ] && [ -s "$G/worktrees/forged21/config.worktree" ] && break; sleep 0.1; done
name_a=$(docker ps --format '{{.Names}}' | grep -E "^$KEY-.*-agent$" | head -1)
check "the replay is real: while that session runs, git in its worktree runs the planted config" fires21
out=$(pen_run true 2>&1)
check "a second session can't take the unchanged plant as its baseline: it's quarantined at its end" \
  test ! -e "$G/worktrees/forged21/config.worktree" -a -e "$G/worktrees/forged21/config.worktree.pen-quarantine"
check "...with the warning" contains "pen: WARNING" "$out"
check "...and git there no longer runs it" not fires21
docker rm -f "$name_a" >/dev/null
wait "$p_a" 2>/dev/null || true
unforge21

"$PEN" run bash -c "$plant21; sleep 120" </dev/null >/dev/null 2>&1 &
p_a=$!
wait_for "^$KEY-.*-agent$" || fail "planting session didn't start"
for i in $(seq 100); do [ -s "$G/worktrees/forged21/config.worktree" ] && break; sleep 0.1; done
name_a=$(docker ps --format '{{.Names}}' | grep -E "^$KEY-.*-agent$" | head -1)
"$PEN" run sleep 120 </dev/null >/dev/null 2>&1 &
p_b=$!
check "...started alongside the planting session" wait_count "^$KEY-.*-agent$" 2
name_b=$(docker ps --format '{{.Names}}' | grep -E "^$KEY-.*-agent$" | grep -vxF "$name_a" | head -1)
docker rm -f "$name_a" >/dev/null
wait "$p_a" 2>/dev/null || true
check "when the planting session ends first, the plant goes then" not fires21
docker rm -f "$name_b" >/dev/null
wait "$p_b" 2>/dev/null || true
check "...and the second's end doesn't write it back" test ! -e "$G/worktrees/forged21/config.worktree"
check "...so git there still doesn't run it" not fires21
unforge21

git config --worktree user.name mine
out=$(pen_run true 2>&1)
check "the repo's own config.worktree stays, with no warning" bash -c '[ "$(git config --worktree user.name)" = mine ] && ! grep -q "pen: WARNING" <<<"$1"' _ "$out"
git config --unset extensions.worktreeConfig
rm -f "$G/config.worktree"

check "nothing is left running" test "$(containers)" = 0
rm -f main-only.txt
git worktree remove --force "$WT"
git branch -q -D smoke-wt
note "manual: git worktree add ../x -b feat && cd ../x && pen claude"
fi

if want 22; then
# ==============================================================================
section "22. Port forwarding"

P=8765
# sid_of <agent container>: its session id; other_agent <name>: another running agent.
sid_of() { local n=${1#"$KEY"-}; echo "${n%-agent}"; }
DISP="~${PROJECT_DIR#"$HOME"}" # how pen ps shows the project
port_open() { python3 -c 'import socket, sys; s = socket.socket(); s.settimeout(2); sys.exit(s.connect_ex(("127.0.0.1", int(sys.argv[1]))))' "$1"; }
port_open "$P" && fail "port $P is already in use on this machine: free it to run this section"
rm -rf .pen

# One session serves on $P and checks the network from inside, then waits.
"$PEN" --port "$P" run bash -c "
  mkdir -p /tmp/srv && echo served-from-sandbox >/tmp/srv/hello.txt
  python3 -m http.server $P --bind 0.0.0.0 --directory /tmp/srv >/dev/null 2>&1 &
  for i in \$(seq 100); do curl -s --noproxy '*' -o /dev/null localhost:$P && break; sleep 0.1; done
  t() { if eval \"\$1\" >/dev/null 2>&1; then echo \"ok: \$2\"; else echo \"fail: \$2\"; fi; }
  t \"curl -s -m 5 --noproxy '*' https://github.com\" direct
  t \"curl -s -m 5 --noproxy '*' http://ports:$P/hello.txt | grep -q served-from-sandbox\" loopback
  t \"curl -s -m 5 -x http://ports:$P https://github.com\" viaports
  t \"curl -s -m 5 --noproxy '*' http://ports:80/\" otherport
  echo ready
  sleep 120
" </dev/null >"$SCRATCH/ports.out" 2>&1 &
p=$!
for i in $(seq 600); do grep -q '^ready$' "$SCRATCH/ports.out" && break; kill -0 "$p" 2>/dev/null || break; sleep 0.1; done
grep -q '^ready$' "$SCRATCH/ports.out" || { cat "$SCRATCH/ports.out" >&2; fail "the port-forwarding session didn't get ready"; }
out=$(cat "$SCRATCH/ports.out")
has() { grep -qxF -- "$1" <<<"$out"; }
agent=$(docker ps --format '{{.Names}}' | grep -E "^$KEY-.*-agent$" | head -1)
s=$RUNTIME/sessions/$(sid_of "$agent")/
fwd=${agent%-agent}-ports

check "pen --port $P: the host reaches a server in the sandbox at 127.0.0.1:$P" \
  contains served-from-sandbox "$(curl -s -m 5 "http://127.0.0.1:$P/hello.txt")"
check "...and at localhost:$P" contains served-from-sandbox "$(curl -s -m 5 "http://localhost:$P/hello.txt")"
check "...published on the host's 127.0.0.1 only" \
  test "$(docker inspect --format '{{range $k, $v := .HostConfig.PortBindings}}{{$k}}={{range $v}}{{.HostIp}}:{{.HostPort}}{{end}} {{end}}' "$fwd")" = "$P/tcp=127.0.0.1:$P "
check "the forwarder runs as nobody, with no capabilities, on a read-only root" \
  test "$(docker inspect --format '{{.Config.User}} {{.HostConfig.CapDrop}} {{.HostConfig.ReadonlyRootfs}} {{.HostConfig.SecurityOpt}}' "$fwd")" = "nobody [ALL] true [no-new-privileges:true]"
check "the agent is still on the sandbox network alone" \
  bash -c 'docker inspect --format "{{range \$k, \$v := .NetworkSettings.Networks}}{{\$k}} {{end}}" "$1" | grep -qxE "[^ ]+_sandbox "' _ "$agent"
check "the agent still can't reach the internet directly" has "fail: direct"
check "...nor through the forwarder: ports:$P leads back to the agent itself" has "ok: loopback"
check "...so using it as a proxy fails" has "fail: viaports"
check "...and the forwarder listens on no other port" has "fail: otherport"
check "the session's ports file lists $P" test "$(cat "${s}ports")" = "$P"
out=$("$PEN" ps)
check "pen ps shows the forwarded port" grep -qE "^$DISP +$(sid_of "$agent") +$P +$(tools_cell "$RUNTIME/sessions/$(sid_of "$agent")")[0-9]+[smhd]" <<<"$out"

rc=0; out=$("$PEN" --port "$P" run true </dev/null 2>&1) || rc=$?
check "the same --port in a second session is an error" \
  bash -c '[ "$1" = 1 ] && grep -qF "pen: --port $3: port $3 is already in use on this machine" <<<"$2"' _ "$rc" "$out" "$P"
check "...and leaves no session behind" test "$(docker ps --format '{{.Names}}' | grep -cE "^$KEY-.*-agent$")" = 1
other_agent() { docker ps --format '{{.Names}}' | grep -E "^$KEY-.*-agent$" | grep -vxF "$1" | head -1; }
mkdir -p .pen && printf 'ports = [%s]\n' "$P" >.pen/config.toml
"$PEN" run bash -c 'echo started; sleep 120' </dev/null >"$SCRATCH/ports2.out" 2>&1 &
p2=$!
for i in $(seq 600); do grep -q '^started$' "$SCRATCH/ports2.out" && break; kill -0 "$p2" 2>/dev/null || break; sleep 0.1; done
out=$(cat "$SCRATCH/ports2.out")
check "the same port from .pen/config.toml: a second session starts" grep -qx started <<<"$out"
check "...with a warning" contains "pen: not forwarding port $P (from .pen/config.toml): it's already in use" "$out"
second=$(other_agent "$agent")
s2=$RUNTIME/sessions/$(sid_of "$second")
check "...forwarding nothing: an empty ports file, no ports service or forwarder" \
  bash -c '[ -e "$1/ports" ] && [ ! -s "$1/ports" ] && ! grep -q "\"ports\":" "$1/session.yaml" && ! docker inspect "${2%-agent}-ports" >/dev/null 2>&1' _ "$s2" "$second"
check "...and pen ps shows no ports for it" grep -qE "^$DISP +$(sid_of "$second") +$(tools_cell "$s2")[0-9]+[smhd] " <<<"$("$PEN" ps)"
check "the first session still forwards $P" contains served-from-sandbox "$(curl -s -m 5 "http://127.0.0.1:$P/hello.txt")"

docker rm -f "$agent" "$second" >/dev/null
wait "$p" "$p2" 2>/dev/null || true
check "the forwarder goes with its session" bash -c '! docker inspect "$1" >/dev/null 2>&1' _ "$fwd"
check "...as does its network" test -z "$(docker network ls -q --filter "name=^$KEY-")"
check "...and the port is free again" not port_open "$P"

"$PEN" run bash -c "echo config-port >/tmp/hello.txt; cd /tmp && python3 -m http.server $P --bind 0.0.0.0 >/dev/null 2>&1 & sleep 120" </dev/null >/dev/null 2>&1 &
p=$!
ok=
for i in $(seq 300); do contains config-port "$(curl -s -m 2 "http://127.0.0.1:$P/hello.txt")" && { ok=1; break; }; sleep 0.1; done
check "ports = [$P] in .pen/config.toml alone forwards it" test -n "$ok"
docker ps --format '{{.Names}}' | grep -E "^$KEY-.*-agent$" | xargs docker rm -f >/dev/null
wait "$p" 2>/dev/null || true
rm -rf .pen
check "nothing is left running" test "$(containers)" = 0
fi

if want 23; then
# ==============================================================================
section "23. pen prune"

# Under the throwaway HOME, whose state holds only smoke fixtures: your own
# projects' volumes show up there as unknown, which pen prune never removes.
fake_config "# none"
fake_prune() { HOME=$FAKE_HOME DOCKER_CONFIG=${DOCKER_CONFIG:-$REAL_HOME/.docker} "$PEN" prune; }
FSTATE=$FAKE_HOME/.local/state/pen
GONE=$(cd "$(mktemp -d "$HOME/.pen-smoke-gone.XXXXXX")" && pwd -P)
LIVE=$(cd "$(mktemp -d "$HOME/.pen-smoke-live.XXXXXX")" && pwd -P)
EXTRA_DIRS+=("$GONE" "$LIVE")
git -C "$GONE" init -q
git -C "$LIVE" init -q
mkdir -p "$GONE/.pen"
echo 'FROM pen:latest' >"$GONE/.pen/Dockerfile"
(cd "$GONE" && fake_pen trust --yes && fake_pen run true) >/dev/null 2>&1 || fail "a session in the project to delete failed"
ghash=$(cd "$GONE" && HOME=$FAKE_HOME DOCKER_CONFIG=${DOCKER_CONFIG:-$REAL_HOME/.docker} session_image --tool "pen-test:$(date +%s)")
ghash=${ghash##*:}
(cd "$LIVE" && fake_pen run true) >/dev/null 2>&1 || fail "a session in the live project failed"
gkey=$(basename "$(ls -d "$FSTATE"/pen-*-"$(printf %s "$GONE" | sha | cut -c1-8)")")
lkey=$(basename "$(ls -d "$FSTATE"/pen-*-"$(printf %s "$LIVE" | sha | cut -c1-8)")")
EXTRA_KEYS+=("$gkey" "$lkey")
check "both projects have volumes, and the one to delete a project layer" \
  bash -c 'docker volume inspect "${1}_home" "${2}_home" >/dev/null && docker image inspect "$1:latest" >/dev/null' _ "$gkey" "$lkey"
others=$(docker volume ls -q | grep -v "^${gkey}_" | sort)
rm -rf "$GONE"

# Tools images: one on an old base, and what must stay though it has the same label or a
# like name: a layer stacked on it, which inherits its label, and a project named tools.
STALE=pen-tools:$(printf %s "stale$$" | sha | cut -c1-16)
STACKED=$lkey:$(printf %s "stacked$$" | sha | cut -c1-16)
NAMED=pen-tools-0123abcd:$(printf %s "named$$" | sha | cut -c1-16)
TOOL_IMAGES+=("$STALE" "$NAMED")
printf 'FROM %s\nLABEL pen.base-id=sha256:smoke-old-base\n' "$IMAGE" | docker build -q -t "$STALE" - >/dev/null || fail "building an image on an old base failed"
printf 'FROM %s\n' "$STALE" | docker build -q -t "$STACKED" -t "$NAMED" - >/dev/null || fail "building a layer on it failed"
check "a stacked layer does inherit the label" test \
  "$(docker image inspect --format '{{ index .Config.Labels "pen.base-id" }}' "$STACKED")" = sha256:smoke-old-base

out=$(echo n | fake_prune 2>&1 || true)
check "pen prune lists the deleted project as missing, with its volumes and layer" \
  has_all "$out" "to remove, missing" "  $GONE  ($gkey)" "${gkey}_home, ${gkey}_nuget; $gkey:$ghash, $gkey:latest"
check "...noting it may be on a volume that isn't mounted" contains "isn't mounted" "$out"
check "...and the live one as kept" bash -c 'grep -q "^kept, in use:" <<<"$1" && grep -qF "  $2  ($3)" <<<"$1"' _ "$out" "$LIVE" "$lkey"
check "...and the tools image on an old base" bash -c 'grep -q "tools images on an old base" <<<"$1" && grep -qF "  $2" <<<"$1"' _ "$out" "$STALE"
check "...asking first" contains "[y/N] n" "$out"
check "...leaving the image there for a no" bash -c 'docker image inspect "$1" >/dev/null' _ "$STALE"
check "answering no removes nothing" bash -c 'grep -q "nothing removed" <<<"$1" && docker volume inspect "${2}_home" >/dev/null && [ -d "$3/$2" ]' _ "$out" "$gkey" "$FSTATE"

r=$(holding "$FSTATE/$gkey/project.lock" shared bash -c 'echo y | "$@"' _ env HOME="$FAKE_HOME" DOCKER_CONFIG="${DOCKER_CONFIG:-$REAL_HOME/.docker}" "$PEN" prune)
check "a key whose project.lock a session holds is skipped" \
  bash -c 'grep -q "skipping $2" <<<"$1" && [ "${1%% *}" = 1 ] && docker volume inspect "${2}_home" >/dev/null && [ -d "$3/$2" ]' _ "$r" "$gkey" "$FSTATE"

out=$(echo y | fake_prune 2>&1 || true)
check "answering yes removes the deleted project's volumes" bash -c 'grep -q "removed $2" <<<"$1" && ! docker volume inspect "${2}_home" >/dev/null 2>&1 && ! docker volume inspect "${2}_nuget" >/dev/null 2>&1' _ "$out" "$gkey"
check "...its project layers" bash -c '! docker image inspect "$1:latest" >/dev/null 2>&1 && ! docker image inspect "$1:$2" >/dev/null 2>&1' _ "$gkey" "$ghash"
check "...but not the shared tools image they were built on" bash -c 'docker image inspect "$1" >/dev/null' _ "pen-tools:$ghash"
check "...and the tools image on an old base goes" not docker image inspect "$STALE"
check "...but not a layer stacked on it, with the same label" docker image inspect "$STACKED"
check "...or a project named tools, by repository" docker image inspect "$NAMED"
check "...and its runtime dir" test ! -e "$FSTATE/$gkey"
check "the live project keeps its volumes and runtime dir" bash -c 'docker volume inspect "${1}_home" >/dev/null && [ -s "$2/$1/project-dir" ]' _ "$lkey" "$FSTATE"
check "...as does every other volume (unknown to this HOME, so never removed)" test "$(docker volume ls -q | grep -v "^${gkey}_" | sort)" = "$others"
out=$(echo n | fake_prune 2>&1)
check "run again, it finds nothing to prune" bash -c 'grep -q "pen: nothing to prune" <<<"$1" && ! grep -q "$2" <<<"$1"' _ "$out" "$gkey"
fi

if want 24; then
# ==============================================================================
section "24. Submodule repos protected"

# A worktree inside the project with submodules of its own: their repos live
# in .git/worktrees/<n>/modules/ (section 26 has the project's own). With
# the checkout deinitialized, nothing in the tree points at the module repo, so
# Pen starts, and the repo must still be protected like .git itself.
SMLIB=$SCRATCH/smlib
git init -q "$SMLIB"
git -C "$SMLIB" -c user.name=smoke -c user.email=smoke@example.invalid -c commit.gpgsign=false commit -q --allow-empty -m lib
git worktree add -q .claude/worktrees/sm 2>/dev/null
SMWT=$PROJECT_DIR/.claude/worktrees/sm
git -C "$SMWT" -c protocol.file.allow=always submodule add -q "$SMLIB" libs/x 2>/dev/null
git -C "$SMWT" submodule deinit -q -f libs/x
M=$PROJECT_DIR/.git/worktrees/sm/modules/libs/x
check "the worktree's submodule repo exists, with its checkout deinitialized" \
  bash -c "[ -f '$M/HEAD' ] && [ ! -e '$SMWT/libs/x/.git' ]"

out=$(in_sandbox "
  t() { if eval \"\$1\" >/dev/null 2>&1; then echo \"ok: \$2\"; else echo \"fail: \$2\"; fi; }
  t 'echo x >> \"$M/config\"' configwrite
  t 'git --git-dir=\"$M\" config core.fsmonitor x' config
  t 'echo x >> \"$M/hooks/post-checkout\"' hook
  t 'mv \"$M\" \"$M.2\"' mvrepo
  t 'git --git-dir=\"$M\" update-ref refs/heads/smoke HEAD' ref
" 2>&1)
has() { grep -qxF -- "$1" <<<"$out"; }
check "pen starts with a submodule repo in a worktree's entry" has "ok: ref"
check "writing the submodule repo's config fails" has "fail: configwrite"
check "...as does git config there" has "fail: config"
check "writing a hook in the submodule repo fails" has "fail: hook"
check "mv of the submodule repo fails" has "fail: mvrepo"
check "the session printed no warning" not contains "WARNING" "$out"
git --git-dir="$M" update-ref -d refs/heads/smoke

touch "$M/commondir"
check "a submodule repo with a commondir is refused at start" refuse "$PROJECT_DIR" "libs/x/commondir exists"
rm "$M/commondir"
ln -s "$SMLIB/.git" "$PROJECT_DIR/.git/worktrees/sm/modules/evil"
check "a symlinked submodule repo is refused at start" refuse "$PROJECT_DIR" "modules/evil is a symlink"
rm "$PROJECT_DIR/.git/worktrees/sm/modules/evil"

check "nothing is left running" test "$(containers)" = 0
git worktree remove --force "$SMWT"
rm -rf .claude
fi

if want 25; then
# ==============================================================================
section "25. Submodule checkouts, and quarantining only new module repos"

# As in section 24, but with the worktree's submodule checked out: its .git
# file points at the module repo, whose core.worktree points back, so Pen
# starts. At the end, only module repos made during the session (and .git
# files not matching a known one) are quarantined.
SMLIB=$SCRATCH/smlib
[ -d "$SMLIB/.git" ] || { git init -q "$SMLIB"
  git -C "$SMLIB" -c user.name=smoke -c user.email=smoke@example.invalid -c commit.gpgsign=false commit -q --allow-empty -m lib; }
git worktree add -q .claude/worktrees/sm 2>/dev/null
SMWT=$PROJECT_DIR/.claude/worktrees/sm
git -C "$SMWT" -c protocol.file.allow=always submodule add -q "$SMLIB" libs/x 2>/dev/null
MODS=$PROJECT_DIR/.git/worktrees/sm/modules
M=$MODS/libs/x
check "the worktree's submodule is checked out" test -f "$SMWT/libs/x/.git"

out=$(in_sandbox "
  t() { if eval \"\$1\" >/dev/null 2>&1; then echo \"ok: \$2\"; else echo \"fail: \$2\"; fi; }
  t 'git -C \"$SMWT/libs/x\" update-ref refs/heads/smoke HEAD' ref
  t 'echo x >> \"$M/config\"' configwrite
  t 'mv \"$MODS/libs\" \"$MODS/libs.2\"' mvlevel
  t 'mv \"$MODS\" \"$MODS.2\"' mvmodules
  t 'git init -q --bare \"$MODS/new\"' newrepo
  t 'mkdir -p \"$SMWT/forged\" && echo \"gitdir: $M\" > \"$SMWT/forged/.git\"' forged
" 2>&1)
has() { grep -qxF -- "$1" <<<"$out"; }
check "pen starts with a submodule checked out in a worktree" has "ok: ref"
check "git in the submodule checkout works in the sandbox" \
  bash -c "git -C '$SMWT/libs/x' rev-parse -q --verify refs/heads/smoke >/dev/null"
check "writing the submodule repo's config still fails" has "fail: configwrite"
check "mv of a level above the submodule repo fails" has "fail: mvlevel"
check "mv of modules/ fails" has "fail: mvmodules"
check "the sandbox could make a new module repo and a .git file" bash -c "grep -qxF 'ok: newrepo' <<<\"\$1\" && grep -qxF 'ok: forged' <<<\"\$1\"" _ "$out"
check "the session warned" contains "WARNING" "$out"
check "...about the new module repo" contains "worktrees/sm/modules/new → " "$out"
check "...and the forged .git file" contains "forged/.git → " "$out"
check "...and nothing about the submodule that was there" not contains "libs/x" "$out"
check "the new module repo was quarantined" \
  bash -c "[ ! -e '$MODS/new' ] && [ -d '$MODS/new.pen-quarantine' ] && [ -f '$SMWT/forged/.git.pen-quarantine' ]"
check "the existing module repo and its checkout's .git file are untouched" \
  bash -c "[ -f '$M/HEAD' ] && [ -f '$SMWT/libs/x/.git' ] && git -C '$SMWT/libs/x' status --porcelain >/dev/null"
out=$(in_sandbox true 2>&1)
check "a second session ends without a warning" not contains "WARNING" "$out"

check "nothing is left running" test "$(containers)" = 0
git -C "$SMWT" submodule deinit -q -f libs/x
git worktree remove --force "$SMWT"
rm -rf .claude
fi

if want 26; then
# ==============================================================================
section "26. Submodules in the project"

SMLIB=$SCRATCH/smlib
[ -d "$SMLIB/.git" ] || { git init -q "$SMLIB"
  git -C "$SMLIB" -c user.name=smoke -c user.email=smoke@example.invalid -c commit.gpgsign=false commit -q --allow-empty -m lib; }
SMSTART=$(git rev-parse HEAD)
hostgit() { git -c user.name=smoke -c user.email=smoke@example.invalid -c commit.gpgsign=false -c protocol.file.allow=always "$@"; }
hostgit submodule add -q "$SMLIB" libs/x 2>/dev/null
hostgit commit -q -m "add libs/x"
M=$PROJECT_DIR/.git/modules/libs/x
check "the submodule is initialized, its repo in .git/modules" bash -c "[ -f libs/x/.git ] && [ -f '$M/HEAD' ]"

out=$(in_sandbox "
  t() { if eval \"\$1\" >/dev/null 2>&1; then echo \"ok: \$2\"; else echo \"fail: \$2\"; fi; }
  g() { git -c user.name=a -c user.email=a@example.invalid \"\$@\"; }
  t 'g -C libs/x commit -q --allow-empty -m in-sub' commit
  t 'g add libs/x && g commit -q -m bump' bump
  t 'echo x >> \"$M/config\"' configwrite
  t 'git -C libs/x config core.fsmonitor x' config
  t 'echo x >> \"$M/hooks/post-checkout\"' hook
" 2>&1)
has() { grep -qxF -- "$1" <<<"$out"; }
check "pen starts with an initialized submodule" has "ok: commit"
check "...and committing inside it works" test "$(git -C libs/x log -1 --format=%s)" = in-sub
check "...as does committing the bump in the superproject" test "$(git log -1 --format=%s)" = bump
check "writing the submodule repo's config fails" has "fail: configwrite"
check "...as does git config in the submodule" has "fail: config"
check "writing a hook in the submodule repo fails" has "fail: hook"
check "the session printed no warning" not contains "WARNING" "$out"
# The checkout's .git file is the project's own (writable, like any file);
# repointing it is caught at the end.
out=$(in_sandbox 'echo "gitdir: /tmp" > libs/x/.git' 2>&1 || true)
check "repointing the checkout's .git file is quarantined at the end" \
  bash -c "grep -qF 'libs/x/.git → ' <<<\"\$1\" && [ -f libs/x/.git.pen-quarantine ]" _ "$out"
rm libs/x/.git.pen-quarantine
printf 'gitdir: ../../.git/modules/libs/x\n' >libs/x/.git
check "with it put back, git in the submodule works on the host" git -C libs/x status --porcelain
check "...and the next session ends without a warning" not contains "WARNING" "$(in_sandbox true 2>&1)"

hostgit submodule deinit -q -f libs/x
git rm -q -f libs/x
rm -rf .git/modules
hostgit commit -q -m "remove libs/x"
# The old layout: a submodule with its own .git dir in the checkout.
git clone -q "$SMLIB" libs/y 2>/dev/null
hostgit submodule add -q "$SMLIB" libs/y >/dev/null 2>&1
check "a submodule with its own .git dir is refused, naming it" refuse "$PROJECT_DIR" "libs/y (a submodule with its own .git dir)"
check "...suggesting git submodule absorbgitdirs" refuse "$PROJECT_DIR" "git submodule absorbgitdirs"
hostgit submodule absorbgitdirs 2>/dev/null
check "after absorbgitdirs, pen starts" pen_run true

check "nothing is left running" test "$(containers)" = 0
hostgit submodule deinit -q -f libs/y
git reset -q --hard "$SMSTART"
rm -rf libs .git/modules .gitmodules
fi

if want 27; then
# ==============================================================================
section "27. Directory mounts hold under other spellings of the name"

# Where the filesystem ignores case, .GIT and .git are one directory, so the
# read-only mounts must hold whichever way the path is spelled. Only the session
# can say: the host folding case doesn't prove the sandbox's view of it does.
MARKER=.smoke27-marker
TOKEN=smoke27-$RANDOM$RANDOM
CFG_COPY=$SCRATCH/smoke27-config
mkdir -p .git/hooks .pen
cp -p .git/config "$CFG_COPY"
printf '# %s %s\n' "$MARKER" "$TOKEN" >>.git/config
for d in .git .git/hooks .pen; do printf '%s\n' "$TOKEN" >"$d/$MARKER"; done
cfg_sum=$(cksum <.git/config)
hooks_list=$(ls -A .git/hooks)
pen_list=$(ls -A .pen)

out=$(in_sandbox '
  export LC_ALL=C
  M=.smoke27-marker
  seen() { echo "read: $1: $(cat "$1" 2>/dev/null)"; }
  attempt() {
    local err
    if err=$({ echo smoke27 >>"$1"; } 2>&1); then echo "wrote: $1"
    elif [[ $err == *"Read-only file system"* ]]; then echo "read-only: $1"
    else echo "refused: $1: $err"; fi
  }
  seen ".git/$M"; seen ".git/hooks/$M"; seen ".pen/$M"
  attempt .git/.smoke27-plain
  attempt smoke27-project
  seen ".GIT/$M"; seen ".git/HOOKS/$M"; seen ".PEN/$M"
  echo "read: .GIT/config: $(grep -F "$M" .GIT/config 2>/dev/null)"
  attempt .GIT/.smoke27-alias
  attempt .GIT/config
  attempt .git/HOOKS/x
  attempt .PEN/x
  echo finished
' 2>&1) || fail "the session failed: $out"
saw() { grep -qxF -- "$1" <<<"$out" || { printf '%s\n' "$out" >&2; return 1; }; }
wrote_on_host() { [ "$(cat "$1" 2>/dev/null)" = smoke27 ]; }

# A session that can't see these, or can't write anywhere, proves nothing below.
check "the session reads what the host put in .git" saw "read: .git/$MARKER: $TOKEN"
check "...in .git/hooks" saw "read: .git/hooks/$MARKER: $TOKEN"
check "...and in .pen" saw "read: .pen/$MARKER: $TOKEN"
check "a normal .git/ path is writable in the session" saw "wrote: .git/.smoke27-plain"
check "...and reaches the host" wrote_on_host .git/.smoke27-plain
check "a project file is writable in the session" saw "wrote: smoke27-project"
check "...and reaches the host" wrote_on_host smoke27-project
# Output cut short must fail here, not look like a session that doesn't fold case.
check "the session tried the other spellings and got to the end" saw finished

folds=0
for p in ".GIT/$MARKER" ".git/HOOKS/$MARKER" ".PEN/$MARKER"; do
  if grep -qxF -- "read: $p: $TOKEN" <<<"$out"; then folds=$((folds + 1)); fi
done
if [ "$folds" = 0 ]; then
  check "the session wrote through none of the other spellings" \
    not grep -qE '^wrote: (\.GIT/|\.git/HOOKS/|\.PEN/)' <<<"$out"
  if [ -e ".GIT/$MARKER" ]; then
    note "the host ignores case but the session doesn't: only the host's files could be checked"
  else
    note "skipped: this filesystem is case-sensitive, so .GIT isn't .git"
  fi
else
  check "the session reads the same files as .GIT, .git/HOOKS and .PEN" test "$folds" = 3
  check "...and .GIT/config is the host's .git/config" saw "read: .GIT/config: # $MARKER $TOKEN"
  check "a new file in .GIT is writable in the session" saw "wrote: .GIT/.smoke27-alias"
  check "...and reaches the host's .git" wrote_on_host .git/.smoke27-alias
  check "writing .GIT/config hits the read-only mount" saw "read-only: .GIT/config"
  check "writing .git/HOOKS/x hits the read-only mount" saw "read-only: .git/HOOKS/x"
  check "writing .PEN/x hits the read-only mount" saw "read-only: .PEN/x"
fi
check ".git/config is unchanged on the host" test "$(cksum <.git/config)" = "$cfg_sum"
check ".git/hooks is unchanged on the host" test "$(ls -A .git/hooks)" = "$hooks_list"
check ".pen is unchanged on the host" test "$(ls -A .pen)" = "$pen_list"

check "nothing is left running" test "$(containers)" = 0
cp -p "$CFG_COPY" .git/config
rm -f ".git/$MARKER" ".git/hooks/$MARKER" ".pen/$MARKER" .git/.smoke27-plain .git/.smoke27-alias smoke27-project
fi

if want 28; then
# ==============================================================================
section "28. Tools image"
tool_free # each check names the tools its session has
rm -rf .pen # section 15 leaves an allow that the throwaway HOME hasn't approved

# Versions are unique to this run, so no earlier image or build cache can stand in.
: >"$TOOLS_SHIM/builds"
in_image() { docker run --rm --entrypoint sh "$1" -c "$2"; }

V=$(date +%s)
A=pen-test:$V B=pen-test-b:$V
tag=$(session_image --tool "$A" --tool "$B")
check "a session with two tools runs one image, tagged pen-tools:<hash>" bash -c '[[ $1 =~ ^pen-tools:[0-9a-f]{16}$ ]] && [ "$2" = 1 ]' _ "$tag" "$(builds)"
check "...labeled with the base image's ID" test \
  "$(docker image inspect --format '{{ index .Config.Labels "pen.base-id" }}' "$tag")" = "$(docker image inspect --format '{{.Id}}' "$IMAGE")"
id=$(docker image inspect --format '{{.Id}}' "$tag")
out=$(in_image "$tag" "ls /opt/pen-test /opt/pen-test-b; stat -c 'owner=%u' /opt/pen-test; [ -e /pen-recipe ] && echo script-left; true")
check "...which installed each at its version, as root" has_all "$out" "$V" "owner=0"
check "...and kept no recipe scripts" not contains script-left "$out"
out=$("$PEN" --tool "$A" --tool "$B" run pen-test </dev/null 2>/dev/null)
check "the session can run the tool" test "$out" = "$V"
out=$("$PEN" --tool "$A" --tool "$B" run printenv TOOL_TEST_ENV </dev/null 2>/dev/null)
check "...and has its recipe's env, literally" test "$out" = 'from "pen-test" $HOME \ here'

check "swapping the flags runs the same image" test "$(session_image --tool "$B" --tool "$A")" = "$tag"
check "...without a rebuild" test "$(builds)" = 1
check "...and the image is untouched" test "$(docker image inspect --format '{{.Id}}' "$tag")" = "$id"

OTHER_TOOLS=$(cd "$(mktemp -d "$HOME/.pen-smoke-tools.XXXXXX")" && pwd -P); EXTRA_DIRS+=("$OTHER_TOOLS"); git -C "$OTHER_TOOLS" init -q
check "a session in another project shares that one image" test "$(cd "$OTHER_TOOLS" && session_image --tool "$A" --tool "$B")" = "$tag"
check "...also without a rebuild" test "$(builds)" = 1
check "...and runs the tool from it" test "$(cd "$OTHER_TOOLS" && "$PEN" --tool "$A" --tool "$B" run pen-test </dev/null 2>/dev/null)" = "$V"

many=$(session_image --tool "$B" --tool "pen-test-b:$((V + 1))")
check "another set of tools is another image" bash -c '[ "$1" != "$2" ] && [ "$3" = 2 ]' _ "$many" "$tag" "$(builds)"
check "...and versions of one tool reach its script as separate arguments" \
  test "$(in_image "$many" 'ls /opt/pen-test-b | wc -l' | tr -d ' ')" = 2

"$PEN" --tool "$A" run sleep 20 </dev/null >/dev/null 2>&1 &
ps_session=$!
wait_for "^$KEY-.*-agent$" || fail "the session for pen ps didn't start"
out=$("$PEN" ps)
check "pen ps shows a session's tools" bash -c 'grep -q "TOOLS" <<<"$1" && grep -qF "$2" <<<"$1"' _ "$out" "$A"
docker rm -f "$(docker ps --format '{{.Names}}' | grep -E "^$KEY-.*-agent$" | head -1)" >/dev/null 2>&1 || true
wait "$ps_session" || true

# tools in .pen/config.toml: nothing happens until they're approved.
A1=pen-test:$V A2=pen-test:$((V + 1)) U=$((V + 2))
rm -rf .pen && mkdir .pen
printf 'tools = ["pen-test:%s"]\n' "$U" >.pen/config.toml
before=$(builds)
rc=0; out=$(PATH="$TOOLS_SHIM:$PATH" "$PEN" run true 2>&1 </dev/null) || rc=$?
check "an unapproved tool in .pen/ refuses a session without a terminal, showing its domains" \
  bash -c '[ "$1" != 0 ] && grep -qF "tool pen-test:$3: sessions run with it installed, and the sandbox can reach example.org" <<<"$2" && grep -qF "pen trust" <<<"$2"' _ "$rc" "$out" "$U"
check "...and builds nothing" test "$(builds)" = "$before"
out=$("$PEN" trust --list </dev/null 2>/dev/null)
check "pen trust --list shows it" contains "tool pen-test:$U" "$out"
check "...and approves nothing" not pen_run true
check "a --tool for the same entry doesn't ask" test "$("$PEN" --tool "pen-test:$U" run pen-test </dev/null 2>/dev/null)" = "$U"
check "...and doesn't approve the entry" not pen_run true
"$PEN" trust --yes </dev/null >/dev/null 2>&1
check "once approved, a session runs with it" test "$(pen_run pen-test 2>/dev/null)" = "$U"
check "...and reaches its domain" test "$(pen_run curl -s -o /dev/null -w '%{http_code}' -I https://example.org 2>/dev/null)" = 200
grep -v '^tool-allow ' "$RUNTIME/trust-$HASH" >"$SCRATCH/trust-less"; cp "$RUNTIME/trust-$HASH" "$SCRATCH/trust-full"
cat "$SCRATCH/trust-less" >"$RUNTIME/trust-$HASH"
rc=0; out=$("$PEN" run true 2>&1 </dev/null) || rc=$?
check "a domain the approval doesn't record asks again" bash -c '[ "$1" != 0 ] && grep -qF "tool pen-test:$3 now brings more domains: the sandbox can reach example.org" <<<"$2"' _ "$rc" "$out" "$U"
out=$("$PEN" trust --list </dev/null 2>/dev/null)
check "...and pen trust --list shows it" contains "now brings more domains" "$out"
cat "$SCRATCH/trust-full" >"$RUNTIME/trust-$HASH"
printf 'tools = ["pen-test:%s"]\n' "$((U + 1))" >.pen/config.toml
rc=0; out=$("$PEN" run true 2>&1 </dev/null) || rc=$?
check "changing the version asks again" bash -c '[ "$1" != 0 ] && grep -qF "tool pen-test:$3" <<<"$2"' _ "$rc" "$out" "$((U + 1))"
rm -rf .pen

# A project layer on tools images: the Dockerfile says FROM pen:latest and runs the tool.
mkdir .pen
printf 'FROM pen:latest\nRUN pen-test >/etc/pen-smoke-tool\n' >.pen/Dockerfile
rc=0; out=$("$PEN" --tool "$A1" run true 2>&1 </dev/null) || rc=$?
check "an unapproved .pen/ isn't stacked on tools" bash -c '[ "$1" != 0 ] && grep -qF ".pen/ asks for things you haven'"'"'t approved" <<<"$2"' _ "$rc" "$out"
"$PEN" trust --yes </dev/null >/dev/null 2>&1
layer1=$(session_image --tool "$A1")
check "a project layer on tools is <key>:<hash of the tools image>" \
  bash -c '[[ $1 == "$2":* ]] && [[ ${1##*:} =~ ^[0-9a-f]{16}$ ]] && docker image inspect "pen-tools:${1##*:}" >/dev/null' _ "$layer1" "$KEY"
check "...and its Dockerfile can RUN the tool" test "$(in_image "$layer1" 'cat /etc/pen-smoke-tool')" = "$V"
layer2=$(session_image --tool "$A2")
check "another tool set in the project is another image" bash -c '[ "$1" != "$2" ] && [[ $2 == "$3":* ]]' _ "$layer1" "$layer2" "$KEY"
check "...with that set's tool" test "$(in_image "$layer2" 'cat /etc/pen-smoke-tool')" = "$((V + 1))"
check "...leaving the first as it was" test "$(in_image "$layer1" 'cat /etc/pen-smoke-tool')" = "$V"
# The image's creation time and layers, not its ID: on the containerd image store a
# fully cached build gets a new ID, while a step that really runs again makes a new layer.
built() { docker image inspect --format '{{.Created}} {{json .RootFS.Layers}}' "$1"; }
id1=$(built "$layer1")
check "building the first again gives the same image" test "$(built "$(session_image --tool "$A1")")" = "$id1"

printf 'FROM pen:latest\nRUN pen-test >/etc/pen-smoke-tool; echo changed >/etc/pen-smoke-changed\n' >.pen/Dockerfile
rc=0; out=$("$PEN" --tool "$A1" run true 2>&1 </dev/null) || rc=$?
check "a changed Dockerfile isn't built on tools until approved" bash -c '[ "$1" != 0 ] && grep -qF ".pen/ asks for things you haven'"'"'t approved" <<<"$2"' _ "$rc" "$out"
check "...leaving the image as it was" test "$(built "$layer1")" = "$id1"
"$PEN" trust --yes </dev/null >/dev/null 2>&1
check "once approved, it keeps the same tag" test "$(session_image --tool "$A1")" = "$layer1"
check "...which now holds the new layer, not the old one" test "$(in_image "$layer1" 'cat /etc/pen-smoke-changed')" = changed

printf 'FROM pen:latest\nRUN test ! -e /opt/pen-test\n' >.pen/Dockerfile
"$PEN" trust --yes </dev/null >/dev/null 2>&1
check "with no tools, a project layer is still <key>:latest, on plain pen:latest" test "$(session_image)" = "$KEY:latest"
rm -rf .pen

git worktree add -q -b smoke-wt28 "$SCRATCH/wt28" 2>/dev/null
WT28=$(cd "$SCRATCH/wt28" && pwd -P)
mkdir -p "$WT28/.pen"; printf 'FROM pen:latest\nRUN pen-test >/etc/pen-smoke-tool\n' >"$WT28/.pen/Dockerfile"
(cd "$WT28" && "$PEN" trust --yes </dev/null >/dev/null 2>&1)
wt_layer=$(cd "$WT28" && session_image --tool "$A1")
check "in a linked worktree it's <key>-<dir hash>:<hash of the tools image>" \
  test "$wt_layer" = "$KEY-$(printf %s "$WT28" | sha | cut -c1-8):${layer1##*:}"
check "...and can RUN the tool too" test "$(in_image "$wt_layer" 'cat /etc/pen-smoke-tool')" = "$V"
git worktree remove --force "$WT28"; git branch -qD smoke-wt28

# Each built-in recipe builds a small project through the proxy, with nothing blocked.
# tool_run <tool> <bash script>: a session with that tool, stdout and stderr together.
tool_run() { "$PEN" --tool "$1" run bash -c "$2" </dev/null 2>&1; }

out=$(tool_run go '
  mkdir -p gohi && cd gohi && go mod init example.com/hi >/dev/null 2>&1
  printf "package main\nimport (\"fmt\"; \"github.com/google/uuid\"; \"golang.org/x/text/cases\"; \"golang.org/x/text/language\")\nfunc main() { _ = uuid.New(); fmt.Println(cases.Title(language.English).String(\"hello go\")) }\n" >main.go
  go mod tidy && go build -o hi . && ./hi && go version' || true)
check "go: a module with dependencies builds and runs" contains "Hello Go" "$out"
check "...with the recipe's default, the newest Go 1.27" grep -qE '^go version go1\.27(\.[0-9]+)? linux/' <<<"$out"
check "...with nothing blocked" not contains "blocked:" "$out"
rm -rf gohi

out=$(tool_run rust '
  cargo new --quiet rshi && cd rshi && cargo add --quiet itoa@1 &&
  printf "fn main() {\n    println!(\"hello rust {}\", itoa::Buffer::new().format(42));\n}\n" >src/main.rs &&
  cargo run --quiet && cargo clippy --quiet && cargo fmt --check &&
  echo "RUSTUP_HOME=$RUSTUP_HOME CARGO_HOME=${CARGO_HOME-unset}" && ls ~/.cargo/registry/cache/*/ | grep -c "^itoa-"' || true)
check "rust: a crate with a dependency builds, runs and passes clippy and rustfmt" contains "hello rust 42" "$out"
check "...with rustup in the image and cargo's cache in the home volume" \
  bash -c 'grep -qxF "RUSTUP_HOME=/usr/local/rustup CARGO_HOME=unset" <<<"$1" && grep -v "^pen:" <<<"$1" | tail -1 | grep -qx 1' _ "$out"
check "...with nothing blocked" not contains "blocked:" "$out"
rm -rf rshi

# Maven, fetched as a wrapper would, and a dependency from Maven Central, through the
# proxy the recipe tells Java about.
mkdir -p jv/src/main/java/hi
cat >jv/pom.xml <<'EOF'
<project xmlns="http://maven.apache.org/POM/4.0.0">
  <modelVersion>4.0.0</modelVersion>
  <groupId>hi</groupId><artifactId>hi</artifactId><version>1</version>
  <properties><maven.compiler.release>21</maven.compiler.release><project.build.sourceEncoding>UTF-8</project.build.sourceEncoding></properties>
  <dependencies><dependency><groupId>org.apache.commons</groupId><artifactId>commons-lang3</artifactId><version>3.17.0</version></dependency></dependencies>
</project>
EOF
printf 'package hi;\npublic class Hi { public static void main(String[] a) { System.out.println(org.apache.commons.lang3.StringUtils.capitalize("hello java")); } }\n' >jv/src/main/java/hi/Hi.java
out=$(tool_run java '
  mvn=https://repo.maven.apache.org/maven2/org/apache/maven/apache-maven/3.9.16/apache-maven-3.9.16-bin.tar.gz
  curl -fsSL "$mvn" | tar -xz -C /tmp && cd jv && /tmp/apache-maven-3.9.16/bin/mvn -B -q package &&
  java -cp "target/hi-1.jar:$(ls ~/.m2/repository/org/apache/commons/commons-lang3/3.17.0/*.jar)" hi.Hi && java -version &&
  java -XshowSettings:properties -version 2>&1 | grep -E "^ +user\.home = "' || true)
check "java: a Maven project with a dependency builds and runs" contains "Hello java" "$out"
check "...with the recipe's default, JDK 21" grep -qE '^openjdk version "21\.' <<<"$out"
check "...with Java's user.home the home volume, even as rootless Docker's root" grep -qE '^ +user\.home = /home/agent$' <<<"$out"
check "...with nothing blocked" not contains "blocked:" "$out"
rm -rf jv

mkdir -p cg && printf 'export function greet(name: string) { return helper(name); }\nfunction helper(n: string) { return "hi " + n; }\n' >cg/a.ts
out=$(tool_run codegraph 'cd cg && codegraph init . >/dev/null 2>&1 && codegraph query greet && git status --porcelain --ignored .' || true)
check "codegraph: indexes a project and finds a symbol" bash -c 'grep -q "greet" <<<"$1" && grep -q "a.ts:1" <<<"$1"' _ "$out"
check "...and git ignores its .codegraph/" contains "!! cg/.codegraph/" "$out"
check "...with nothing blocked" not contains "blocked:" "$out"
rm -rf cg

# registry.npmjs.org is still a default (OpenCode's plugin install); Yarn's registry isn't.
yarn_registry() { curl -s -o /dev/null -w "%{http_code} %{http_connect}" -I https://registry.yarnpkg.com/ms; }
out=$(in_sandbox "command -v node npm npx || echo none; $(declare -f yarn_registry); yarn_registry" 2>/dev/null || true)
check "without the node tool there's no node, npm or npx, and Yarn's registry is blocked" \
  bash -c '[ "$(sed -n 1p <<<"$1")" = none ] && [ "$(sed -n 2p <<<"$1" | cut -d" " -f2)" = 403 ]' _ "$out"

# A package from the registry, and a native addon that npm builds with node-gyp against
# the tarball's headers (npm_config_nodedir).
mkdir -p nodehi/addon
printf '{ "name": "addon", "version": "1.0.0", "main": "build/Release/hello.node" }\n' >nodehi/addon/package.json
printf "{ 'targets': [ { 'target_name': 'hello', 'sources': [ 'hello.c' ] } ] }\n" >nodehi/addon/binding.gyp
cat >nodehi/addon/hello.c <<'EOF'
#include <node_api.h>
static napi_value Hello(napi_env env, napi_callback_info info) {
  napi_value s;
  napi_create_string_utf8(env, "hello addon", NAPI_AUTO_LENGTH, &s);
  return s;
}
NAPI_MODULE_INIT() {
  napi_value fn;
  napi_create_function(env, "hello", NAPI_AUTO_LENGTH, Hello, NULL, &fn);
  napi_set_named_property(env, exports, "hello", fn);
  return exports;
}
EOF
out=$(tool_run node '
  cd nodehi && npm init -y >/dev/null && npm install --no-fund --no-audit ms@2 >/dev/null &&
  node -e "console.log(\"hello node \" + require(\"ms\")(60000))" &&
  rm -rf ~/.cache/node-gyp && (cd addon && npm install --no-fund --no-audit >/dev/null) &&
  node -e "console.log(require(\"./addon\").hello())" && { [ -e ~/.cache/node-gyp ] || echo no-header-download; } &&
  echo "nodedir=$(npm config get nodedir) node=$(command -v node)" && node -v' || true)
check "node: a package from the registry installs and runs" contains "hello node 1m" "$out"
check "...and npm builds a native addon with node-gyp" contains "hello addon" "$out"
check "...against the tarball's headers, with node in /usr/local" \
  bash -c 'grep -qxF no-header-download <<<"$1" && grep -qxF "nodedir=/usr/local node=/usr/local/bin/node" <<<"$1"' _ "$out"
check "...with the recipe's default, Node 26" grep -qE '^v26\.[0-9]+\.[0-9]+$' <<<"$out"
check "...with nothing blocked" not contains "blocked:" "$out"
rm -rf nodehi

# Node 22 warns that the proxy agent NODE_USE_ENV_PROXY turns on is experimental,
# unless the recipe's NODE_OPTIONS turns that warning off.
out=$("$PEN" --tool node:22 --tool codegraph run bash -c '
  node -v; node -e "fetch(\"https://registry.yarnpkg.com/ms\").then(r => console.log(\"fetch \" + r.status))"; npm -v >/dev/null
  codex --version; codegraph --version' </dev/null 2>"$SCRATCH/node22.err" || true)
err=$(cat "$SCRATCH/node22.err")
check "node:22: node -v is 22, and fetch goes through the proxy" bash -c 'grep -qE "^v22\.[0-9]+\.[0-9]+$" <<<"$1" && grep -qx "fetch 200" <<<"$1"' _ "$out"
check "...without Node's experimental proxy agent warning" bash -c '! grep -qE "UNDICI-EHPA|EnvHttpProxyAgent" <<<"$1"' _ "$err"
check "...and codex and codegraph still run, unaffected by it" \
  bash -c 'grep -q "^codex-cli " <<<"$1" && grep -qE "^1\.[0-9]+\.[0-9]+$" <<<"$1"' _ "$out"
check "...with nothing blocked" not contains "blocked:" "$out$err"

# The base has Ubuntu's python3 (3.12, in /usr/bin) for now: these check the recipe's
# interpreters are the ones used, by path, version and sys.base_prefix.
out=$(tool_run python '
  echo "python3=$(command -v python3) python=$(command -v python)"; python3 --version
  uv init -q pyhi && cd pyhi && uv add -q six &&
  uv run python -c "import six, sys; print(\"hello six\", six.__version__); print(\"base=\" + sys.base_prefix)" && cd .. &&
  python3 -m venv pyvenv && pyvenv/bin/pip install -q --disable-pip-version-check six &&
  pyvenv/bin/python -c "import six; print(\"venv six\", six.__version__)" &&
  { [ -e ~/.local/share/uv/python ] || echo no-python-download; } &&
  printf "#include <Python.h>\nint main(void) { return PY_MINOR_VERSION; }\n" >pyhdr.c &&
  gcc $(python3-config --includes) -o pyhdr pyhdr.c && { ./pyhdr; echo "minor=$?"; }' || true)
check "python: python3 and python are the recipe's, its default, Python 3.15" \
  bash -c 'grep -qxF "python3=/usr/local/bin/python3 python=/usr/local/bin/python" <<<"$1" && grep -qE "^Python 3\.15\.[0-9]+$" <<<"$1"' _ "$out"
check "...uv adds a package from PyPI and runs it on that interpreter, downloading none" \
  bash -c 'grep -q "^hello six 1\." <<<"$1" && grep -qE "^base=/usr/local/share/uv/python/cpython-3\.15[.0-9]*-linux-" <<<"$1" && grep -qx no-python-download <<<"$1"' _ "$out"
check "...and pip installs into a venv from python3 -m venv" grep -q "^venv six 1\." <<<"$out"
check "...and python3-config gives its headers to build a C program against" grep -qx "minor=15" <<<"$out"
check "...with nothing blocked" not contains "blocked:" "$out"
rm -rf pyhi pyvenv pyhdr pyhdr.c

out=$("$PEN" --tool python:3.12 --tool python:3.13 run bash -c '
  echo "python3.12=$(command -v python3.12)"; python3 --version; python3.12 --version
  uv venv -q -p 3.12 v12 && v12/bin/python -c "import sys; print(\"v12=\" + sys.base_prefix)"
  uv venv -q -p 3.14 v14 && v14/bin/python -c "import sys; print(\"v14=\" + sys.base_prefix)"; uv python uninstall -q 3.14
  rm -rf v12 v14' </dev/null 2>&1 || true)
check "python:3.12 with python:3.13: python3 is 3.13, and python3.12 is the recipe's" \
  bash -c 'grep -qE "^Python 3\.13\.[0-9]+$" <<<"$1" && grep -qE "^Python 3\.12\.[0-9]+$" <<<"$1" && grep -qxF python3.12=/usr/local/bin/python3.12 <<<"$1"' _ "$out"
check "...which uv venv uses for 3.12, and a Python not in the image downloads into the home volume" \
  bash -c 'grep -qE "^v12=/usr/local/share/uv/python/cpython-3\.12" <<<"$1" && grep -qE "^v14=/home/agent/\.local/share/uv/python/cpython-3\.14" <<<"$1"' _ "$out"
check "...with nothing blocked" not contains "blocked:" "$out"

rc=0; out=$("$PEN" --tool "$A" --build-tools-image 2>&1 </dev/null) || rc=$?
check "there's no build option any more: real sessions build" bash -c '[ "$1" = 2 ] && grep -qF "unknown option" <<<"$2"' _ "$rc" "$out"
real_home
fi

section "All checks passed."
