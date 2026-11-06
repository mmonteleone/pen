#!/usr/bin/env bash
# Builds the pen CLI natively for this machine (Native AOT) and installs it to
# ~/.local/bin/pen.
#
#   cli/install.sh                 build and install
#   cli/install.sh --out DIR       build only, leaving the binary in DIR
#   cli/install.sh --container     build in a .NET SDK container even on macOS
#                                  (produces a Linux binary; use with --out)
#
# macOS: uses the host .NET 11 SDK and Xcode's command-line tools.
# Linux: builds inside a .NET 11 SDK container, so the host needs only Docker.
#   The binary can't need a newer glibc than the build image has. .NET 11's
#   Ubuntu images are 26.04 (glibc 2.43), so the default is Azure Linux 3.0
#   (glibc 2.38); its binary asks for GLIBC_2.34 and runs on Ubuntu 22.04+ and
#   Debian 12+. Check the server with `ldd --version`; PEN_BUILD_IMAGE
#   overrides the build image.
set -euo pipefail

REPO=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
BUILD_IMAGE=${PEN_BUILD_IMAGE:-mcr.microsoft.com/dotnet/sdk:11.0-azurelinux3.0-aot}
DEST=$HOME/.local/bin

out= container=
while [ $# -gt 0 ]; do
  case $1 in
    --out) out=${2:?--out needs a directory}; shift 2 ;;
    --container) container=1; shift ;;
    -h|--help) sed -n '2,16p' "$0"; exit 0 ;;
    *) echo "install.sh: unknown argument $1" >&2; exit 2 ;;
  esac
done

case $(uname -m) in
  x86_64|amd64) arch=x64 ;;
  arm64|aarch64) arch=arm64 ;;
  *) echo "install.sh: unsupported architecture $(uname -m)" >&2; exit 1 ;;
esac
[ "$(uname -s)" = Linux ] && container=1

stage=$(mktemp -d "${TMPDIR:-/tmp}/pen-install.XXXXXX")
trap 'rm -rf "$stage"' EXIT

if [ -n "$container" ]; then
  rid=linux-$arch
  echo "building pen ($rid) in $BUILD_IMAGE"
  # Builds from a copy of the repo, so no root-owned obj/ or bin/ lands in it.
  # The NuGet cache is a named volume, so rebuilds don't re-download.
  docker run --rm \
    -v "$REPO:/src:ro" -v "$stage:/out" -v pen-cli-build-nuget:/root/.nuget/packages \
    -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 \
    "$BUILD_IMAGE" bash -euo pipefail -c "
      mkdir /build && cd /src && tar --exclude='./cli/*/bin' --exclude='./cli/*/obj' --exclude=./.git -cf - . | tar -xf - -C /build
      dotnet publish /build/cli/Pen/Pen.csproj -c Release -r $rid -o /build/out -p:StripSymbols=true -v quiet -nologo
      cp /build/out/pen /out/pen
      chown $(id -u):$(id -g) /out/pen
    "
else
  rid=osx-$arch
  command -v dotnet >/dev/null || { echo "install.sh: the .NET 11 SDK is needed on macOS" >&2; exit 1; }
  xcode-select -p >/dev/null 2>&1 || { echo "install.sh: run xcode-select --install first" >&2; exit 1; }
  echo "building pen ($rid)"
  DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 \
    dotnet publish "$REPO/cli/Pen/Pen.csproj" -c Release -r "$rid" -o "$stage/publish" -v quiet -nologo
  cp "$stage/publish/pen" "$stage/pen"
fi

if [ -n "$out" ]; then
  mkdir -p "$out"
  install -m 0755 "$stage/pen" "$out/pen"
  echo "built $out/pen"
  exit 0
fi

mkdir -p "$DEST"
# Install by rename, so a running pen keeps its (old) binary.
install -m 0755 "$stage/pen" "$DEST/.pen.new"
mv -f "$DEST/.pen.new" "$DEST/pen"
"$DEST/pen" --version
case ":$PATH:" in
  *":$DEST:"*) ;;
  *) echo "note: $DEST isn't on your PATH" ;;
esac
