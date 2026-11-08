#!/bin/bash
set -euo pipefail
warn() { echo "pen-entrypoint: $*" >&2; }

# Docker sessions only (PEN_DOCKER=1, set by the session's compose override):
# containers and build steps inside dind can't resolve "proxy", but reach its
# IP through dind's NAT. The docker client passes config.json's proxies into
# every container and build. Other sessions never write it: each session's
# proxy has its own IP, and only one docker session runs at a time.
if [ "${PEN_DOCKER:-}" = 1 ]; then
  cfg=$HOME/.docker/config.json
  ip=$(getent ahostsv4 proxy | awk 'NR == 1 { print $1 }') || ip=
  if [ -z "$ip" ]; then
    warn "couldn't resolve the proxy: containers inside Docker won't reach the network"
  elif [ -e "$cfg" ] && ! jq -e 'type == "object"' "$cfg" >/dev/null 2>&1; then
    warn "~/.docker/config.json isn't a JSON object: not setting the proxy for containers inside Docker"
  else
    url="http://$ip:3128"
    mkdir -p "$HOME/.docker"
    if tmp=$(mktemp "$cfg.pen.XXXXXX") \
        && { [ -e "$cfg" ] && cat -- "$cfg" || echo '{}'; } \
          | jq --arg u "$url" '.proxies.default = {httpProxy: $u, httpsProxy: $u, noProxy: "localhost,127.0.0.1"}' >"$tmp" \
        && mv -f -- "$tmp" "$cfg"; then
      :
    else
      rm -f -- "${tmp:-}"
      warn "couldn't write the proxy into ~/.docker/config.json: containers inside Docker won't reach the network"
    fi
  fi
fi
