#!/bin/bash
# Installs Ubuntu's headless JDK of each version given (8 11 17 21 25), from the signed
# Ubuntu archive. java, javac and the rest point at the newest. Any failure fails the build.
#
# Java ignores the HTTP(S)_PROXY variables, so each JDK's net.properties names the
# session's proxy, which sessions always reach as proxy:3128. That also applies to a
# .pen/Dockerfile's RUN steps, which have no proxy: a step that downloads with Java
# needs JAVA_TOOL_OPTIONS="-Dhttp.proxyHost= -Dhttps.proxyHost=" to connect directly.
set -euo pipefail

export DEBIAN_FRONTEND=noninteractive
[ $# -gt 0 ] || { echo "java: no versions given" >&2; exit 1; }
for v in "$@"; do
  [[ $v =~ ^[1-9][0-9]*$ ]] || { echo "java: \"$v\" isn't a JDK version" >&2; exit 1; }
done
arch=$(dpkg --print-architecture)

apt-get update
apt-get install -y --no-install-recommends ca-certificates $(printf 'openjdk-%s-jdk-headless\n' "$@")
rm -rf /var/lib/apt/lists/*

newest=$(printf '%s\n' "$@" | sort -n | tail -1)
for v in "$@"; do
  home=/usr/lib/jvm/java-$v-openjdk-$arch
  props=/etc/java-$v-openjdk/net.properties
  [ -f "$props" ] || { echo "java: no $props for JDK $v" >&2; exit 1; }
  cat >>"$props" <<'PROPS'

# pen: sessions reach the network only through their proxy.
http.proxyHost=proxy
http.proxyPort=3128
https.proxyHost=proxy
https.proxyPort=3128
http.nonProxyHosts=localhost|127.*|[::1]|proxy|dind
PROPS
  "$home/bin/javac" -version
  "$home/bin/java" -version 2>&1 | grep -qE "^openjdk version \"(1\.)?$v[.\"]" ||
    { echo "java: JDK $v didn't install" >&2; exit 1; }
done
[ "$(readlink -f "$(command -v javac)")" = "/usr/lib/jvm/java-$newest-openjdk-$arch/bin/javac" ] ||
  { echo "java: javac isn't JDK $newest's" >&2; exit 1; }
[ "$(readlink -f "$(command -v java)")" = "/usr/lib/jvm/java-$newest-openjdk-$arch/bin/java" ] ||
  { echo "java: java isn't JDK $newest's" >&2; exit 1; }
java -version
