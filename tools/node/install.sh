#!/bin/bash
# Installs the newest release of the Node major version given (24) from the official
# tarball into /usr/local, so node, npm and npx are on PATH and npm's global prefix is in
# the image, and Ubuntu's python3 and build-essential for node-gyp. SHASUMS256.txt must
# be signed by one of the pinned release keys and list the tarball's checksum, and any
# failure fails the build. Pen's private Node in /opt/pen/node is left alone.
set -euo pipefail

# The active Node.js releasers' primary keys, as listed in github.com/nodejs/release-keys.
RELEASE_KEYS=(
  5BE8A3F6C8A5C01D106C0AD820B1A390B168D356 # Antoine du Hamel
  DD792F5973C6DE52C432CBDAC77ABFA00DDBF2B7 # Juan José Arboleda
  CC68F5A3106FF448322E48ED27F5E38D5B0A215F # Marco Ippolito
  890C08DB8579162FEE0DF9DB8BEAB4DFCF555EF4 # Rafael Gonzaga
  C82FA3AE1CBEDC6BE46B9360C43CEC45C17AB93C # Richard Lau
  108F52B48DB57BB0CC439B2997B01419BD92F80A # Ruy Adorno
  655F3B5C1FB3FA8D1A0CA6BDE4A7D232B936D2FD # Stewart X Addison
  A363A499291CBBC940DD62E41F10027AF002F8B0 # Ulises Gascón
)
KEYS_URL=https://raw.githubusercontent.com/nodejs/release-keys/main/keys
DIST=https://nodejs.org/dist

export DEBIAN_FRONTEND=noninteractive
[ $# -eq 1 ] || { echo "node: one version at a time" >&2; exit 1; }
v=$1
[[ $v =~ ^[1-9][0-9]*$ ]] || { echo "node: \"$v\" isn't a major version (24)" >&2; exit 1; }
case $(dpkg --print-architecture) in
  amd64) arch=x64 ;;
  arm64) arch=arm64 ;;
  *) echo "node: unsupported architecture $(dpkg --print-architecture)" >&2; exit 1 ;;
esac

apt-get update
apt-get install -y --no-install-recommends ca-certificates curl gnupg jq xz-utils build-essential python3
rm -rf /var/lib/apt/lists/*

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
fetch() { curl -fsSL --retry 3 -o "$2" "$1"; }

# The newest release of that major version: v24.22.2.
fetch "$DIST/index.json" "$work/index.json"
release=$(jq -r --arg v "$v" '
  [.[] | select(.version | test("^v" + $v + "\\.[0-9]+\\.[0-9]+$"))]
  | max_by(.version | ltrimstr("v") | split(".") | map(tonumber)) | .version // empty' "$work/index.json")
[[ $release =~ ^v$v\.[0-9]+\.[0-9]+$ ]] || { echo "node: no release found for Node $v" >&2; exit 1; }
file=node-$release-linux-$arch.tar.xz

# The keys: each file must hold exactly its pinned key. Importing drops subkeys and user
# IDs without valid self-signatures, and the export is all gpgv trusts.
export GNUPGHOME=$work/gnupg
mkdir -m 700 "$GNUPGHOME"
for key in "${RELEASE_KEYS[@]}"; do
  fetch "$KEYS_URL/$key.asc" "$work/$key.asc"
  got=$(gpg --batch --show-keys --with-colons "$work/$key.asc" | awk -F: 'p && $1 == "fpr" { print $10 } { p = ($1 == "pub") }')
  [ "$got" = "$key" ] || { echo "node: $KEYS_URL/$key.asc isn't that key" >&2; exit 1; }
  gpg --batch --quiet --import "$work/$key.asc"
done
imported=$(gpg --batch --list-keys --with-colons | awk -F: 'p && $1 == "fpr" { print $10 } { p = ($1 == "pub") }' | sort)
[ "$imported" = "$(printf '%s\n' "${RELEASE_KEYS[@]}" | sort)" ] || { echo "node: the release keys didn't import as expected" >&2; exit 1; }
gpg --batch --export >"$work/keys.gpg"

# A clearsigned file: only the text gpgv outputs after checking the signature is used.
# The output doesn't go next to it as SHASUMS256.txt, which gpgv would take for the data
# of a detached signature.
fetch "$DIST/$release/SHASUMS256.txt.asc" "$work/SHASUMS256.txt.asc"
gpgv --keyring "$work/keys.gpg" --output - "$work/SHASUMS256.txt.asc" >"$work/signed-sums"
sums=$(awk -v f="$file" '$2 == f' "$work/signed-sums")
[ -n "$sums" ] && [ "$(wc -l <<<"$sums")" = 1 ] || { echo "node: no checksum for $file" >&2; exit 1; }
fetch "$DIST/$release/$file" "$work/$file"
(cd "$work" && sha256sum --check --strict --quiet - <<<"$sums")

tar -xJf "$work/$file" -C /usr/local --strip-components=1 --no-same-owner \
  --exclude='*/CHANGELOG.md' --exclude='*/LICENSE' --exclude='*/README.md'
[ "$(command -v node)" = /usr/local/bin/node ] || { echo "node: node on PATH is $(command -v node), not /usr/local/bin/node" >&2; exit 1; }
installed=$(node --version)
[ "$installed" = "$release" ] || { echo "node: installed $installed, not $release" >&2; exit 1; }
echo "node $installed, npm $(npm --version)"
