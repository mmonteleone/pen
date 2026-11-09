#!/bin/sh
# Records each requested version, so checks can see what an image installed.
set -eu
mkdir -p /opt/pen-test-b
for version in "$@"; do
  : >"/opt/pen-test-b/$version"
done
