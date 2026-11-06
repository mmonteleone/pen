#!/usr/bin/env bash
# Container smoke checks for pen, accumulated chunk by chunk. Run on both the
# Mac (OrbStack/arm64) and the server (Docker/amd64). Exits non-zero on the
# first failure.
#
#   tests/smoke.sh            all checks
#   SMOKE_SLOW=1 tests/smoke.sh   also the PEN_REFRESH rebuild check (~1 min)
#
# Until the CLI exists (chunk 7), sessions are stood up by hand the way the CLI
# will: a session dir holding compose.yaml, squid.conf, allowlist.txt and an
# empty proxy.log (666), then `compose run`, then `down`. The image must
# already be built: docker build -t pen:latest image/
set -euo pipefail

REPO=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
IMAGE=${PEN_IMAGE:-pen:latest}

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

# --- fixture project ------------------------------------------------------------
# Under $HOME so the "only the project path exists under /Users" check means
# something. Removed (with its volumes) on exit.
PROJECT_DIR=$(cd "$(mktemp -d "$HOME/.pen-smoke.XXXXXX")" && pwd -P)
PEN_PROJECT="pen-smoke-$(echo "${PROJECT_DIR##*.}" | tr 'A-Z' 'a-z')"
STATE=$(mktemp -d "${TMPDIR:-/tmp}/pen-smoke-state.XXXXXX")
chmod 700 "$STATE"
export PROJECT_DIR PEN_PROJECT PEN_IMAGE=$IMAGE
export PEN_UID=$(id -u) PEN_GID=$(id -g)
export NUGET_PACKAGES=${NUGET_PACKAGES:-$HOME/.nuget/packages}

cleanup() {
  local s
  for s in "$STATE"/sessions/*/; do
    [ -f "$s/compose.yaml" ] && docker compose -f "$s/compose.yaml" -p "$(cat "$s/name")" down -t 1 >/dev/null 2>&1 || true
  done
  docker volume rm "${PEN_PROJECT}_home" "${PEN_PROJECT}_nuget" >/dev/null 2>&1 || true
  rm -rf "$PROJECT_DIR" "$STATE"
}
trap cleanup EXIT

git -C "$PROJECT_DIR" init -q
git -C "$PROJECT_DIR" -c user.name=smoke -c user.email=smoke@example.invalid commit -q --allow-empty -m init

# --- hand-written session (replaced by `pen run` from chunk 7) -------------------
LAST_SESSION=
session_new() {
  local id; id=$(od -An -N4 -tx1 /dev/urandom | tr -d ' \n')
  local dir="$STATE/sessions/$id"
  mkdir -p "$dir"; chmod 700 "$STATE/sessions" "$dir"
  cp "$REPO/compose.yaml" "$REPO/proxy/squid.conf" "$REPO/proxy/allowlist.txt" "$dir/"
  : >"$dir/proxy.log"; chmod 666 "$dir/proxy.log"
  echo "$PEN_PROJECT-$id" >"$dir/name"
  LAST_SESSION=$dir
  docker volume create "${PEN_PROJECT}_home" >/dev/null
  docker volume create "${PEN_PROJECT}_nuget" >/dev/null
}
compose() { docker compose --progress quiet -f "$LAST_SESSION/compose.yaml" -p "$(cat "$LAST_SESSION/name")" "$@"; }
session_down() { compose down -t 1 >/dev/null 2>&1; rm -f "$LAST_SESSION/compose.yaml"; }
# pen_run <cmd…>: one whole session, like `pen run <cmd…>` without a TTY.
pen_run() {
  session_new
  local rc=0
  compose run --rm -T --name "$(cat "$LAST_SESSION/name")-agent" agent "$@" || rc=$?
  session_down
  return $rc
}
# in_sandbox <bash script>: run a script in a fresh session, as agent.
in_sandbox() { pen_run bash -c "$1"; }

docker image inspect "$IMAGE" >/dev/null 2>&1 || fail "image $IMAGE not found; build it first: docker build -t pen:latest image/"

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
  git config commit.gpgsign true 2>/dev/null; echo "gpgsign_effective=$(git config commit.gpgsign)"
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

session_new
compose run --rm -T --name "$(cat "$LAST_SESSION/name")-agent" agent \
  bash -c 'curl -s -o /dev/null -I https://example.com; curl -s -o /dev/null -I https://github.com' || true
session_down
check "proxy.log records the denial (TCP_DENIED example.com:443)" grep -q 'TCP_DENIED/403 CONNECT example.com:443' "$LAST_SESSION/proxy.log"
check "proxy.log records the allowed request" grep -q 'CONNECT github.com:443' "$LAST_SESSION/proxy.log"
check "proxy.log has no healthcheck probes" not grep -q ' 127.0.0.1 ' "$LAST_SESSION/proxy.log"

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
# in inside a session by hand). Values are passed by name, never on a command line.
prompt_check() { # prompt_check <agent> <env var> <cmd…>
  local agent=$1 var=$2; shift 2
  if [ -z "${!var:-}" ]; then note "$agent prompt: skipped (set $var to run it)"; return; fi
  session_new
  local out
  out=$(compose run --rm -T -e "$var" --name "$(cat "$LAST_SESSION/name")-agent" agent timeout 180 "$@" </dev/null 2>&1 || true)
  session_down
  check "$agent answers a trivial prompt through the proxy" grep -q 4 <<<"$out"
  if grep -q TCP_DENIED "$LAST_SESSION/proxy.log"; then
    note "$agent: denied → $(grep TCP_DENIED "$LAST_SESSION/proxy.log" | awk '{print $5}' | sort -u | tr '\n' ' ')"
  fi
}
prompt_check claude CLAUDE_CODE_OAUTH_TOKEN claude -p 'What is 2+2? Reply with just the digit.'
prompt_check codex OPENAI_API_KEY codex exec --skip-git-repo-check 'What is 2+2? Reply with just the digit.'

if [ "${SMOKE_SLOW:-}" = 1 ]; then
  v1=$(docker run --rm --entrypoint stat "$IMAGE" -c %Y /usr/local/lib/node_modules/@anthropic-ai/claude-code/package.json)
  docker build -q --build-arg "PEN_REFRESH=$(date +%s)" -t "$IMAGE" "$REPO/image" >/dev/null
  v2=$(docker run --rm --entrypoint stat "$IMAGE" -c %Y /usr/local/lib/node_modules/@anthropic-ai/claude-code/package.json)
  check "changing PEN_REFRESH reinstalls the agents" test "$v1" != "$v2"
else
  note "PEN_REFRESH rebuild check: skipped (SMOKE_SLOW=1 to run it)"
fi

section "All checks passed."
