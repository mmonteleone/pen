#!/bin/bash
# Installs rustup, pinned below, into /usr/local/rustup (RUSTUP_HOME) with its commands
# in /usr/local/bin, each toolchain given (stable 1.85 1.85.1 …) with clippy and rustfmt,
# and Ubuntu's build-essential. CARGO_HOME stays unset, so cargo's registry cache and
# `cargo install` go to ~/.cargo in the home volume. rustup-init is checked against the pinned checksums, and
# rustup checks each component against its channel's manifest. Any failure fails the build.
set -euo pipefail

RUSTUP_VERSION=1.29.3
RUSTUP_SHA256_X64=b1c8cb14e30889e7e2999e6b69e62d9e9b9ffc0f4df8180564d306d9680e6c55
RUSTUP_SHA256_ARM64=9f608c142e0e38a78957d0661ba1dfaf0b6fb37184cfaa4b9a0aa57af6184282
export RUSTUP_HOME=/usr/local/rustup

export DEBIAN_FRONTEND=noninteractive
[ $# -gt 0 ] || { echo "rust: no versions given" >&2; exit 1; }
for v in "$@"; do
  [[ $v =~ ^(stable|1\.[0-9]+(\.[0-9]+)?)$ ]] || { echo "rust: \"$v\" isn't stable or a release (1.85, 1.85.1)" >&2; exit 1; }
done
case $(dpkg --print-architecture) in
  amd64) target=x86_64-unknown-linux-gnu sha=$RUSTUP_SHA256_X64 ;;
  arm64) target=aarch64-unknown-linux-gnu sha=$RUSTUP_SHA256_ARM64 ;;
  *) echo "rust: unsupported architecture $(dpkg --print-architecture)" >&2; exit 1 ;;
esac

# What rustup and this script use; Ubuntu's build-essential (cc, c++, make) for linking
# and for crates' build scripts.
apt-get update
apt-get install -y --no-install-recommends ca-certificates curl build-essential
rm -rf /var/lib/apt/lists/*

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
curl -fsSL --retry 3 -o "$work/rustup-init" "https://static.rust-lang.org/rustup/archive/$RUSTUP_VERSION/$target/rustup-init"
echo "$sha  $work/rustup-init" | sha256sum --check --strict --quiet
chmod 755 "$work/rustup-init"

# rustup's commands (rustup, cargo, rustc …) go into CARGO_HOME/bin: a scratch one here,
# moved into /usr/local/bin.
export CARGO_HOME=$work/cargo PATH=$work/cargo/bin:$PATH
"$work/rustup-init" -y --no-modify-path --no-update-default-toolchain --profile minimal --default-toolchain none
rustup set auto-self-update disable
# A toolchain that isn't in the image can't be installed in a session (RUSTUP_HOME isn't
# writable there), so rustup says it's missing rather than trying to.
rustup set auto-install disable
for v in "$@"; do
  rustup toolchain install "$v" --profile minimal --component clippy,rustfmt --no-self-update
done
# The default is stable if given, otherwise the newest release.
if printf '%s\n' "$@" | grep -qx stable; then default=stable; else default=$(printf '%s\n' "$@" | sort -V | tail -1); fi
rustup default "$default"
for f in "$CARGO_HOME"/bin/*; do
  mv "$f" /usr/local/bin/
done
unset CARGO_HOME
export PATH=${PATH#"$work/cargo/bin:"}
hash -r
chmod -R a+rX "$RUSTUP_HOME"

installed=$(rustup toolchain list)
for v in "$@"; do
  grep -q "^$v-$target" <<<"$installed" || { echo "rust: toolchain $v didn't install" >&2; exit 1; }
done
for c in cc c++ make; do command -v "$c" >/dev/null || { echo "rust: no $c" >&2; exit 1; }; done
rustup show
cargo --version
rustc --version
