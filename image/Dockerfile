# syntax=docker/dockerfile:1
# Pen base image. Builds natively on arm64 (Mac/OrbStack) and amd64 (server).
# Nothing is installed into /home/agent: all of $HOME is a per-project volume.

FROM ubuntu:24.04

ARG TARGETARCH
ARG NODE_VERSION=lts

SHELL ["/bin/bash", "-euo", "pipefail", "-c"]
ENV DEBIAN_FRONTEND=noninteractive

# Remove every regular user (and group) with an ID in 1000–60000, so none can
# collide with the host UID/GID the entrypoint creates at start. Matches by ID,
# not name: today that's `ubuntu` (1000).
RUN in_range() { awk -F: '$3 >= 1000 && $3 <= 60000 { print $1 }' "$1"; }; \
    for u in $(in_range /etc/passwd); do userdel -r "$u" 2>/dev/null || userdel "$u"; done; \
    for g in $(in_range /etc/group); do groupdel "$g"; done; \
    test -z "$(in_range /etc/passwd)$(in_range /etc/group)"

# Base tools. No compilers, make or Python: tools bring those. Pen's Node (nodejs.org's
# build) needs libatomic1.
RUN apt-get update; \
    apt-get install -y --no-install-recommends \
      ca-certificates curl wget gnupg git less vim unzip xz-utils bzip2 patch jq ripgrep fd-find \
      tzdata netbase openssl tini libcap2-bin bash-completion socat libatomic1; \
    ln -s /usr/bin/fdfind /usr/local/bin/fd; \
    rm -rf /var/lib/apt/lists/*

# gh and the docker CLI + buildx and compose plugins (client only) from their apt repos.
RUN install -d -m 0755 /etc/apt/keyrings; \
    curl -fsSL https://cli.github.com/packages/githubcli-archive-keyring.gpg -o /etc/apt/keyrings/githubcli.gpg; \
    echo "deb [arch=${TARGETARCH} signed-by=/etc/apt/keyrings/githubcli.gpg] https://cli.github.com/packages stable main" \
      > /etc/apt/sources.list.d/github-cli.list; \
    curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc; \
    echo "deb [arch=${TARGETARCH} signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu noble stable" \
      > /etc/apt/sources.list.d/docker.list; \
    apt-get update; \
    apt-get install -y --no-install-recommends gh docker-ce-cli docker-buildx-plugin docker-compose-plugin; \
    rm -rf /var/lib/apt/lists/*

# Node (LTS by default) into /opt/pen/node: Pen's runtime for recipes distributed
# through npm. It isn't on PATH, so sessions don't have node, npm or npx; recipes
# run their programs on it through wrappers. The tarball must match nodejs.org's
# SHASUMS256.txt for that release. npm's check keeps Node's compile cache in the
# temp dir, so nothing is left in /tmp.
RUN case "${TARGETARCH}" in amd64) arch=x64 ;; arm64) arch=arm64 ;; *) echo "unsupported arch ${TARGETARCH}" >&2; exit 1 ;; esac; \
    if [ "${NODE_VERSION}" = lts ]; then \
      version=$(curl -fsSL https://nodejs.org/dist/index.json | jq -r '[.[] | select(.lts)][0].version'); \
    else \
      version="v${NODE_VERSION#v}"; \
    fi; \
    file="node-${version}-linux-${arch}.tar.xz"; \
    tmp=$(mktemp -d); \
    curl -fsSL -o "${tmp}/${file}" "https://nodejs.org/dist/${version}/${file}"; \
    curl -fsSL -o "${tmp}/SHASUMS256.txt" "https://nodejs.org/dist/${version}/SHASUMS256.txt"; \
    sums=$(awk -v f="${file}" '$2 == f' "${tmp}/SHASUMS256.txt"); \
    [ "$(wc -l <<<"${sums}")" = 1 ] && [ -n "${sums}" ] || { echo "no checksum for ${file}" >&2; exit 1; }; \
    (cd "${tmp}" && sha256sum -c --strict - <<<"${sums}"); \
    install -d -m 0755 /opt/pen/node; \
    tar -xJf "${tmp}/${file}" -C /opt/pen/node --strip-components=1 --no-same-owner \
      --exclude='*/CHANGELOG.md' --exclude='*/LICENSE' --exclude='*/README.md'; \
    /opt/pen/node/bin/node --version; \
    TMPDIR="${tmp}" PATH=/opt/pen/node/bin:$PATH npm --version; \
    rm -rf "${tmp}"

# System git config. Git authenticates to GitHub through gh (GH_TOKEN), SSH
# remotes are rewritten to https (inside the sandbox only), bind-mount
# ownership never triggers "dubious ownership", and .codegraph/ is ignored.
COPY gitignore /etc/pen/gitignore
RUN git config --system credential.https://github.com.helper '' ; \
    git config --system --add credential.https://github.com.helper '!gh auth git-credential'; \
    git config --system url.https://github.com/.insteadOf 'git@github.com:'; \
    git config --system --add url.https://github.com/.insteadOf 'ssh://git@github.com/'; \
    git config --system safe.directory '*'; \
    git config --system core.excludesFile /etc/pen/gitignore

# Commit signing off. Env config beats a repo's own .git/config, so a repo with
# signing on doesn't make agent commits fail. The user signs on the host.
ENV GIT_CONFIG_COUNT=2 \
    GIT_CONFIG_KEY_0=commit.gpgsign GIT_CONFIG_VALUE_0=false \
    GIT_CONFIG_KEY_1=tag.gpgsign GIT_CONFIG_VALUE_1=false

# Proxy env (HTTP(S)_PROXY, NO_PROXY, both cases) is set by compose.yaml on the
# agent service rather than here: ENV would also apply to RUN steps of a
# project layer (FROM pen:latest), which build without a proxy.
# NODE_USE_ENV_PROXY: Node's own fetch honours those proxy variables.
ENV NODE_USE_ENV_PROXY=1 \
    GIT_TERMINAL_PROMPT=0 \
    GH_PROMPT_DISABLED=1 \
    GH_NO_UPDATE_NOTIFIER=1

COPY --chmod=0755 entrypoint.sh /usr/local/bin/pen-entrypoint
COPY --chmod=0755 sync-seed.sh docker-proxy.sh /usr/local/lib/pen/

# The image installs nothing into /home/agent and leaves nothing in /tmp or root's home,
# Node stays off PATH, and Python, uv, build tools and the agents come only from tools that
# need them (python, node, go, rust, claude, codex, opencode, pi); fail the build if any of
# that changed.
RUN test ! -e /home/agent && test ! -e /etc/codex && test ! -e /opt/pen/agents; \
    leftover=$(find /tmp /root/.cache /root/.config /root/.local /root/.npm -mindepth 1 2>/dev/null || true); \
    [ -z "${leftover}" ] || { echo "left behind: ${leftover}" >&2; exit 1; }; \
    for c in node npm npx corepack python3 uv make gcc claude codex opencode pi; do ! command -v "$c" >/dev/null || { echo "$c is on PATH" >&2; exit 1; }; done

# Always the last layer. pen upgrade sets PEN_REFRESH to the current time, so it gives a
# new base image ID even when every other layer is cached, and every tools image (the
# agents too, which are tools) rebuilds on its next use, installing latest anew. A
# normal build leaves it at 0 and is cached.
ARG PEN_REFRESH=0
RUN echo "${PEN_REFRESH}" >/etc/pen/refresh

ENTRYPOINT ["/usr/local/bin/pen-entrypoint"]
CMD ["bash", "-l"]
