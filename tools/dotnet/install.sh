#!/bin/bash
# Installs the latest .NET SDK of each major version given (8 10 …) into /usr/share/dotnet
# with dotnet-install.sh, and the pinned PowerShell below. Every download is verified
# (the script by Microsoft's signature, archives by checksum), and any failure fails the build.
set -euo pipefail

POWERSHELL_VERSION=7.6.9
POWERSHELL_SHA256_X64=1da6d6d6035d651f20e70ddee7c4c15ac6ab8b9fb40ee92240d91263f28a884b
POWERSHELL_SHA256_ARM64=02eb9991af447c7e7fe881acde195961afd397c40b8eb1c2b3856b84ec2d85f2
# The key that signs dotnet-install.sh, published next to it.
INSTALL_SCRIPT_KEY=2B930AB1228D11D5D7F6B6ACB9CF1A51FC7D3ACF
FEED=https://builds.dotnet.microsoft.com/dotnet
ROOT=/usr/share/dotnet

export DEBIAN_FRONTEND=noninteractive DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
[ $# -gt 0 ] || { echo "dotnet: no versions given" >&2; exit 1; }
for v in "$@"; do
  [[ $v =~ ^[1-9][0-9]*$ ]] || { echo "dotnet: \"$v\" isn't a major version" >&2; exit 1; }
done
case $(dpkg --print-architecture) in
  amd64) arch=x64 pwsh_sha=$POWERSHELL_SHA256_X64 ;;
  arm64) arch=arm64 pwsh_sha=$POWERSHELL_SHA256_ARM64 ;;
  *) echo "dotnet: unsupported architecture $(dpkg --print-architecture)" >&2; exit 1 ;;
esac

# What the SDKs and PowerShell need at run time, and what this script uses.
apt-get update
apt-get install -y --no-install-recommends \
  ca-certificates curl gnupg jq tzdata \
  libc6 libgcc-s1 libgssapi-krb5-2 libicu78 libssl3t64 libstdc++6 zlib1g
rm -rf /var/lib/apt/lists/*

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
fetch() { curl -fsSL --retry 3 -o "$2" "$1"; }

# dotnet-install.sh, checked against its signature by the pinned key.
fetch "$FEED/scripts/v1/dotnet-install.sh" "$work/dotnet-install.sh"
fetch "$FEED/scripts/v1/dotnet-install.sig" "$work/dotnet-install.sig"
fetch "$FEED/scripts/v1/dotnet-install.asc" "$work/dotnet-install.asc"
export GNUPGHOME=$work/gnupg
mkdir -m 700 "$GNUPGHOME"
keys=$(gpg --batch --show-keys --with-colons "$work/dotnet-install.asc" | awk -F: '$1 == "fpr" { print $10 }')
[ "$keys" = "$INSTALL_SCRIPT_KEY" ] || { echo "dotnet: dotnet-install.sh is signed by an unexpected key" >&2; exit 1; }
gpg --batch --dearmor <"$work/dotnet-install.asc" >"$work/key.gpg"
gpgv --keyring "$work/key.gpg" "$work/dotnet-install.sig" "$work/dotnet-install.sh"

# Oldest first, so the newest SDK's dotnet host is the one left in place.
for v in $(printf '%s\n' "$@" | sort -n); do
  meta=$(curl -fsSL --retry 3 "$FEED/release-metadata/$v.0/releases.json") ||
    { echo "dotnet: no release information for .NET $v" >&2; exit 1; }
  sdk=$(jq -r '."latest-sdk" // empty' <<<"$meta")
  [[ $sdk =~ ^$v\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?$ ]] || { echo "dotnet: no SDK found for .NET $v" >&2; exit 1; }
  hash=$(jq -r --arg sdk "$sdk" --arg name "dotnet-sdk-linux-$arch.tar.gz" \
    'first(.releases[] | (.sdks // [.sdk])[] | select(.version == $sdk) | .files[] | select(.name == $name) | .hash) // empty' <<<"$meta")
  [[ $hash =~ ^[0-9a-f]{128}$ ]] || { echo "dotnet: no checksum for SDK $sdk on $arch" >&2; exit 1; }
  # The kept archive is the one extracted: checked before anything from it runs.
  bash "$work/dotnet-install.sh" --version "$sdk" --install-dir "$ROOT" --architecture "$arch" --os linux \
    --no-path --zip-path "$work/sdk.tar.gz" --keep-zip
  echo "$hash  $work/sdk.tar.gz" | sha512sum --check --strict --quiet
  rm -f "$work/sdk.tar.gz"
  sdks+=("$sdk")
done
ln -sfn "$ROOT/dotnet" /usr/bin/dotnet
listed=$(dotnet --list-sdks)
for sdk in ${sdks[@]+"${sdks[@]}"}; do
  grep -q "^$sdk " <<<"$listed" || { echo "dotnet: SDK $sdk didn't install" >&2; exit 1; }
done

# PowerShell, self-contained, so it doesn't depend on which SDKs are installed.
fetch "https://github.com/PowerShell/PowerShell/releases/download/v$POWERSHELL_VERSION/powershell-$POWERSHELL_VERSION-linux-$arch.tar.gz" "$work/pwsh.tar.gz"
echo "$pwsh_sha  $work/pwsh.tar.gz" | sha256sum --check --strict --quiet
pwsh_dir=/opt/microsoft/powershell/7
rm -rf "$pwsh_dir"
mkdir -p "$pwsh_dir"
tar -xzf "$work/pwsh.tar.gz" -C "$pwsh_dir" --no-same-owner
chmod 755 "$pwsh_dir/pwsh"
ln -sfn "$pwsh_dir/pwsh" /usr/bin/pwsh
pwsh --version
dotnet --list-sdks
