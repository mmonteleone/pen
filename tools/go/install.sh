#!/bin/bash
# Installs the latest release of the Go minor version given (1.27) from the official
# tarball into /usr/local/go, and links go and gofmt into /usr/local/bin, with Ubuntu's gcc
# and libc6-dev so cgo is on and `go test -race` works. The tarball is
# checked against Google's signature by the pinned key and against go.dev's checksum,
# and any failure fails the build.
set -euo pipefail

# Google's Linux package signing key, which signs the Go tarballs.
SIGNING_KEY=EB4C1BFD4F042F6DDDCCEC917721F63BD38B4796
ROOT=/usr/local/go

export DEBIAN_FRONTEND=noninteractive
[ $# -eq 1 ] || { echo "go: one version at a time" >&2; exit 1; }
v=$1
[[ $v =~ ^1\.[1-9][0-9]*$ ]] || { echo "go: \"$v\" isn't a minor version (1.27)" >&2; exit 1; }
case $(dpkg --print-architecture) in
  amd64|arm64) arch=$(dpkg --print-architecture) ;;
  *) echo "go: unsupported architecture $(dpkg --print-architecture)" >&2; exit 1 ;;
esac

apt-get update
apt-get install -y --no-install-recommends ca-certificates curl gnupg jq gcc libc6-dev
rm -rf /var/lib/apt/lists/*

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
fetch() { curl -fsSL --retry 3 -o "$2" "$1"; }

# The newest stable release of that minor version: go1.27.3, or go1.20 before 1.21's go1.21.0.
fetch "https://go.dev/dl/?mode=json&include=all" "$work/releases.json"
release=$(jq -r --arg v "$v" '
  [.[] | select(.stable and (.version | test("^go" + ($v | gsub("\\."; "\\.")) + "(\\.[0-9]+)?$")))
   | {version, patch: ((.version | capture("^go1\\.[0-9]+\\.(?<p>[0-9]+)$").p // "0") | tonumber)}]
  | max_by(.patch) | .version // empty' "$work/releases.json")
[[ $release =~ ^go$v(\.[0-9]+)?$ ]] || { echo "go: no release found for Go $v" >&2; exit 1; }
file=$release.linux-$arch.tar.gz
hash=$(jq -r --arg v "$release" --arg f "$file" \
  'first(.[] | select(.version == $v) | .files[] | select(.filename == $f and .kind == "archive") | .sha256) // empty' "$work/releases.json")
[[ $hash =~ ^[0-9a-f]{64}$ ]] || { echo "go: no checksum for $file" >&2; exit 1; }

fetch "https://dl.google.com/go/$file" "$work/go.tar.gz"
fetch "https://dl.google.com/go/$file.asc" "$work/go.tar.gz.asc"
fetch "https://dl.google.com/linux/linux_signing_key.pub" "$work/key.asc"
echo "$hash  $work/go.tar.gz" | sha256sum --check --strict --quiet
export GNUPGHOME=$work/gnupg
mkdir -m 700 "$GNUPGHOME"
keys=$(gpg --batch --show-keys --with-colons "$work/key.asc" | awk -F: 'p && $1 == "fpr" { print $10 } { p = ($1 == "pub") }')
[ "$keys" = "$SIGNING_KEY" ] || { echo "go: the tarball's signing key isn't the expected one" >&2; exit 1; }
gpg --batch --dearmor <"$work/key.asc" >"$work/key.gpg"
gpgv --keyring "$work/key.gpg" "$work/go.tar.gz.asc" "$work/go.tar.gz"

rm -rf "$ROOT"
tar -xzf "$work/go.tar.gz" -C /usr/local --no-same-owner
ln -sfn "$ROOT/bin/go" /usr/local/bin/go
ln -sfn "$ROOT/bin/gofmt" /usr/local/bin/gofmt
installed=$(go env GOVERSION)
[ "$installed" = "$release" ] || { echo "go: installed $installed, not $release" >&2; exit 1; }
# Go turns cgo off when it finds no C compiler.
[ "$(go env CGO_ENABLED)" = 1 ] || { echo "go: cgo is off" >&2; exit 1; }
go version
