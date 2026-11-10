#!/bin/bash
# Installs Pi at the version given (latest, or an exact release such as 1.5.2) with the
# base's helper for agents from npm (/opt/pen/lib/npm-agent.sh), into /opt/pen/pi. No
# install scripts run. /usr/local/bin/pi runs the package's bundle on Pen's Node.
set -euo pipefail
export PI_SKIP_VERSION_CHECK=1
exec /opt/pen/lib/npm-agent.sh pi @earendil-works/pi-coding-agent \
  --node dist/bundle/cli.js --since 0.84.3 -- "$@"
