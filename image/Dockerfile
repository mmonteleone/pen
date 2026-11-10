# syntax=docker/dockerfile:1
# Pen base image. Builds natively on arm64 (Mac/OrbStack) and amd64 (server).
# Nothing is installed into /home/agent: all of $HOME is a per-project volume.

ARG UV_VERSION=latest
FROM ghcr.io/astral-sh/uv:${UV_VERSION} AS uv

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

# Base tools.
RUN apt-get update; \
    apt-get install -y --no-install-recommends \
      ca-certificates curl wget gnupg git less vim unzip xz-utils jq ripgrep fd-find \
      tzdata netbase openssl build-essential python3 python3-venv tini libcap2-bin bash-completion socat; \
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

# uv / uvx.
COPY --from=uv /uv /uvx /usr/local/bin/

# Node (LTS by default) into /opt/pen/node: Pen's runtime for recipes distributed
# through npm. It isn't on PATH, so sessions don't have node, npm or npx; recipes
# run their programs on it through wrappers. The tarball must match nodejs.org's
# SHASUMS256.txt for that release.
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
    rm -rf "${tmp}"; \
    /opt/pen/node/bin/node --version; PATH=/opt/pen/node/bin:$PATH npm --version

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

# ---------------------------------------------------------------------------
# Agents: always the last layers. `pen upgrade` sets PEN_REFRESH
# to the current time so these reinstall even when the base image is cached.
# All go into /opt/pen/agents, never $HOME, and auto-updaters are off: the only
# upgrade path is rebuilding the image. (The agent user can't write /opt/pen.)
# Claude Code and OpenCode are native binaries; Codex and Pi run on Pen's Node.
ARG PEN_REFRESH=0
ARG CLAUDE_CODE_VERSION=latest
ARG CODEX_VERSION=latest
ARG OPENCODE_VERSION=latest
ARG PI_VERSION=latest

ENV DISABLE_AUTOUPDATER=1 \
    CLAUDE_CODE_DISABLE_AUTOUPDATE=1 \
    OPENCODE_DISABLE_AUTOUPDATE=1 \
    PI_SKIP_VERSION_CHECK=1 \
    NPM_CONFIG_UPDATE_NOTIFIER=false

# claude-code and opencode-ai need their postinstall: it puts the native binary
# in place of the bin stub.
RUN echo "refresh ${PEN_REFRESH}"; \
    export PATH=/opt/pen/node/bin:$PATH; \
    npm install -g --prefix /opt/pen/agents --no-fund --no-audit --allow-scripts=@anthropic-ai/claude-code,opencode-ai \
      "@anthropic-ai/claude-code@${CLAUDE_CODE_VERSION}" \
      "@openai/codex@${CODEX_VERSION}" \
      "opencode-ai@${OPENCODE_VERSION}" \
      "@earendil-works/pi-coding-agent@${PI_VERSION}"; \
    npm cache clean --force; \
    rm -rf /root/.npm /root/.cache; \
    lib=/opt/pen/agents/lib/node_modules; \
    ln -sf "${lib}/@anthropic-ai/claude-code/bin/claude.exe" /usr/local/bin/claude; \
    ln -sf "${lib}/opencode-ai/bin/opencode.exe" /usr/local/bin/opencode; \
    wrap() { printf '#!/bin/sh\nexec /opt/pen/node/bin/node %s "$@"\n' "$2" > "/usr/local/bin/$1"; chmod 0755 "/usr/local/bin/$1"; }; \
    wrap codex "${lib}/@openai/codex/bin/codex.js"; \
    wrap pi "${lib}/@earendil-works/pi-coding-agent/dist/bundle/cli.js"; \
    for c in claude opencode codex pi; do test -x "$(readlink -f "/usr/local/bin/${c}")"; done; \
    for f in "${lib}/@openai/codex/bin/codex.js" "${lib}/@earendil-works/pi-coding-agent/dist/bundle/cli.js"; do test -f "${f}"; done

COPY codex-managed-config.toml /etc/codex/managed_config.toml

# The image installs nothing into /home/agent, and Node stays off PATH; fail the
# build if either changed.
RUN test ! -e /home/agent; \
    for c in node npm npx corepack; do ! command -v "$c" >/dev/null || { echo "$c is on PATH" >&2; exit 1; }; done

ENTRYPOINT ["/usr/local/bin/pen-entrypoint"]
CMD ["bash", "-l"]
