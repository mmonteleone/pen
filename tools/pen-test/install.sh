#!/bin/sh
# Records each requested version, so checks can see what an image installed.
set -eu
mkdir -p /opt/pen-test
for version in "$@"; do
  : >"/opt/pen-test/$version"
done
printf '#!/bin/sh\nls /opt/pen-test\n' >/usr/local/bin/pen-test
chmod 755 /usr/local/bin/pen-test
