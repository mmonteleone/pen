#!/bin/bash
# Installs uv, pinned below, into /usr/local/bin (uv, uvx), and each Python minor version
# given (3.13 3.14 …) as the newest managed build of it that uv knows, in
# /usr/local/share/uv/python, with python3.X and python3.X-config in /usr/local/bin for
# each and python3, python and python3-config for the newest. Ubuntu's build-essential
# comes too, for C extensions. The uv tarball is checked against the pinned checksums; uv
# checks each Python build against the sha256 it embeds for that build, and fails on a
# mismatch. Any failure fails the build.
set -euo pipefail

# uv's list of Python builds, with their checksums, is compiled into it, so this version
# fixes each minor version's patch release (3.13 is always 3.13.18 with uv 0.13.7): a
# newer patch release needs a newer uv here, which rebuilding the image doesn't bring.
UV_VERSION=0.13.7
UV_SHA256_X64=c5221ff41b7892bfc223543883600125f6a47fe845093e0f5bdff4f359196d83
UV_SHA256_ARM64=71279f8d3c67241e807b9d8db872d1bf541579fb9f08b7d1eb1682196d1e57dc

export DEBIAN_FRONTEND=noninteractive
[ $# -gt 0 ] || { echo "python: no versions given" >&2; exit 1; }
for v in "$@"; do
  [[ $v =~ ^3\.[0-9]+$ ]] || { echo "python: \"$v\" isn't a minor version (3.13)" >&2; exit 1; }
done
case $(dpkg --print-architecture) in
  amd64) target=x86_64-unknown-linux-gnu sha=$UV_SHA256_X64 ;;
  arm64) target=aarch64-unknown-linux-gnu sha=$UV_SHA256_ARM64 ;;
  *) echo "python: unsupported architecture $(dpkg --print-architecture)" >&2; exit 1 ;;
esac

apt-get update
apt-get install -y --no-install-recommends ca-certificates curl build-essential
rm -rf /var/lib/apt/lists/*

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
file=uv-$target.tar.gz
curl -fsSL --retry 3 -o "$work/$file" "https://github.com/astral-sh/uv/releases/download/$UV_VERSION/$file"
echo "$sha  $work/$file" | sha256sum --check --strict --quiet
tar -xzf "$work/$file" -C "$work" --no-same-owner
install -m 755 "$work/uv-$target/uv" "$work/uv-$target/uvx" /usr/local/bin/
hash -r
[ "$(uv --version | cut -d' ' -f2)" = "$UV_VERSION" ] || { echo "python: uv is $(uv --version), not $UV_VERSION" >&2; exit 1; }

# Only here: in a session /usr/local is read-only, so uv keeps the Pythons it downloads
# itself in ~/.local/share/uv/python, and finds these on PATH. The cache is scratch.
UV_CACHE_DIR=$work/cache UV_PYTHON_INSTALL_DIR=/usr/local/share/uv/python UV_PYTHON_BIN_DIR=/usr/local/bin \
  uv python install --no-config "$@"
newest=$(printf '%s\n' "$@" | sort -V | tail -1)
# uv links only python3.X. python3.X-config (for building against the headers) works out
# its prefix from the directory it's run from, without following a link to itself, so
# /usr/local/bin gets a wrapper that runs it where it is.
for v in "$@"; do
  config=$(dirname "$(readlink "/usr/local/bin/python$v")")/python$v-config
  [ -x "$config" ] || { echo "python: no $config" >&2; exit 1; }
  printf '#!/bin/sh\nexec %s "$@"\n' "$config" >"/usr/local/bin/python$v-config"
  chmod 755 "/usr/local/bin/python$v-config"
done
ln -sfn "python$newest" /usr/local/bin/python3
ln -sfn "python$newest" /usr/local/bin/python
ln -sfn "python$newest-config" /usr/local/bin/python3-config
chmod -R a+rX /usr/local/share/uv
hash -r

for v in "$@"; do
  [ -x "/usr/local/bin/python$v" ] || { echo "python: python$v didn't install" >&2; exit 1; }
  got=$("python$v" -c 'import sys; print("%d.%d" % sys.version_info[:2])')
  [ "$got" = "$v" ] || { echo "python: python$v is Python $got" >&2; exit 1; }
  "python$v" --version
  inc=$("python$v-config" --includes)
  [[ $inc == -I/usr/local/share/uv/python/*/include/python$v* ]] || { echo "python: python$v-config --includes gives \"$inc\"" >&2; exit 1; }
  inc=${inc%% *}
  [ -e "${inc#-I}/Python.h" ] || { echo "python: python$v-config's include dir ${inc#-I} has no Python.h" >&2; exit 1; }
done
python3-config --includes | grep -q "include/python$newest" || { echo "python: python3-config isn't Python $newest's" >&2; exit 1; }
[ "$(command -v python3)" = /usr/local/bin/python3 ] || { echo "python: python3 on PATH is $(command -v python3), not /usr/local/bin/python3" >&2; exit 1; }
[ "$(python3 -c 'import sys; print("%d.%d" % sys.version_info[:2])')" = "$newest" ] || { echo "python: python3 isn't Python $newest" >&2; exit 1; }
python3 -m venv "$work/venv"
"$work/venv/bin/python" -m pip --version
uv --version
