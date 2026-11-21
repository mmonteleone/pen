# syntax=docker/dockerfile:1
# Pen base image. Builds natively on arm64 (Mac/OrbStack) and amd64 (server).
# Nothing is installed into /home/agent: all of $HOME is a per-project volume.

FROM ubuntu:26.04

ARG TARGETARCH

SHELL ["/bin/bash", "-euo", "pipefail", "-c"]
ENV DEBIAN_FRONTEND=noninteractive

# Remove every regular user (and group) with an ID in 1000–60000, so none can
# collide with the host UID/GID the entrypoint creates at start. Matches by ID,
# not name: today that's `ubuntu` (1000).
RUN in_range() { awk -F: '$3 >= 1000 && $3 <= 60000 { print $1 }' "$1"; }; \
    for u in $(in_range /etc/passwd); do userdel -r "$u" 2>/dev/null || userdel "$u"; done; \
    for g in $(in_range /etc/group); do groupdel "$g"; done; \
    test -z "$(in_range /etc/passwd)$(in_range /etc/group)"

# Base tools, with C build tools and Ubuntu's Python 3 (pip, venv, headers) for native
# modules and C extensions, and fish and zsh for a session's shell. Node (nodejs.org's
# build) needs libatomic1. zsh's new-user script goes: with a fresh home volume it would
# stop a login shell at the zsh-newuser-install menu.
RUN apt-get update; \
    apt-get install -y --no-install-recommends \
      ca-certificates curl wget gnupg git less vim unzip xz-utils bzip2 patch jq ripgrep fd-find \
      tzdata netbase openssl tini libcap2-bin bash-completion socat libatomic1 \
      build-essential python3 python3-venv python3-pip python3-dev python-is-python3 \
      fish zsh openssh-client; \
    ln -s /usr/bin/fdfind /usr/local/bin/fd; \
    newuser=(/usr/share/zsh/*/scripts/newuser); \
    [ -f "${newuser[0]}" ] || { echo "no zsh newuser script" >&2; exit 1; }; \
    rm "${newuser[@]}"; \
    rm -rf /var/lib/apt/lists/*

# gh and the docker CLI + buildx and compose plugins (client only) from their apt repos.
RUN install -d -m 0755 /etc/apt/keyrings; \
    curl -fsSL https://cli.github.com/packages/githubcli-archive-keyring.gpg -o /etc/apt/keyrings/githubcli.gpg; \
    echo "deb [arch=${TARGETARCH} signed-by=/etc/apt/keyrings/githubcli.gpg] https://cli.github.com/packages stable main" \
      > /etc/apt/sources.list.d/github-cli.list; \
    curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc; \
    echo "deb [arch=${TARGETARCH} signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu resolute stable" \
      > /etc/apt/sources.list.d/docker.list; \
    apt-get update; \
    apt-get install -y --no-install-recommends gh docker-ce-cli docker-buildx-plugin docker-compose-plugin; \
    rm -rf /var/lib/apt/lists/*

# uv and uvx into /usr/local/bin, at the pinned version, checked against the pinned
# checksum for this architecture. uv uses Ubuntu's python3, and downloads any other Python
# a project asks for into the home volume.
ARG UV_VERSION=0.13.7
ARG UV_SHA256_X64=c5221ff41b7892bfc223543883600125f6a47fe845093e0f5bdff4f359196d83
ARG UV_SHA256_ARM64=71279f8d3c67241e807b9d8db872d1bf541579fb9f08b7d1eb1682196d1e57dc
RUN case "${TARGETARCH}" in \
      amd64) target=x86_64-unknown-linux-gnu sha="${UV_SHA256_X64}" ;; \
      arm64) target=aarch64-unknown-linux-gnu sha="${UV_SHA256_ARM64}" ;; \
      *) echo "unsupported arch ${TARGETARCH}" >&2; exit 1 ;; \
    esac; \
    work=$(mktemp -d); \
    file="uv-${target}.tar.gz"; \
    curl -fsSL --retry 3 -o "${work}/${file}" "https://github.com/astral-sh/uv/releases/download/${UV_VERSION}/${file}"; \
    echo "${sha}  ${work}/${file}" | sha256sum --check --strict --quiet; \
    tar -xzf "${work}/${file}" -C "${work}" --no-same-owner; \
    install -m 755 "${work}/uv-${target}/uv" "${work}/uv-${target}/uvx" /usr/local/bin/; \
    rm -rf "${work}"; \
    [ "$(uv --version | cut -d' ' -f2)" = "${UV_VERSION}" ] || { echo "uv is $(uv --version), not ${UV_VERSION}" >&2; exit 1; }; \
    uvx --version

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
# NPM_CONFIG_UPDATE_NOTIFIER: no "new version of npm available" notices, since npm comes
# with the image.
# UV_LINK_MODE: uv's cache (~/.cache/uv, the home volume) and a project's .venv are never on
# the same filesystem, so uv would warn on every install that it can't hardlink, and then copy.
ENV NODE_USE_ENV_PROXY=1 \
    NPM_CONFIG_UPDATE_NOTIFIER=false \
    UV_LINK_MODE=copy \
    GIT_TERMINAL_PROMPT=0 \
    GH_PROMPT_DISABLED=1 \
    GH_NO_UPDATE_NOTIFIER=1

COPY --chmod=0755 entrypoint.sh /usr/local/bin/pen-entrypoint
COPY --chmod=0755 sync-seed.sh docker-proxy.sh /usr/local/lib/pen/
# The install helper for agent recipes from npm (claude, codex, opencode, pi). It's off
# PATH, in /opt/pen where the agents go, and root's like the rest of /opt/pen. Changing it
# gives the base a new ID and so every tools image a new tag (ToolsImage.Tag covers that ID),
# so the agents reinstall with it.
COPY --chmod=0755 npm-agent.sh /opt/pen/lib/npm-agent.sh

# Node (the newest LTS by default, or a major version) into /usr/local, from the official
# tarball checked against the release keys' signature. It follows PEN_REFRESH, so every
# pen upgrade installs the newest LTS anew while a normal build is cached. The script is
# only mounted for the build.
ARG PEN_REFRESH=0
ARG NODE_VERSION=lts
RUN --mount=type=bind,source=install-node.sh,target=/pen-build/install-node.sh \
    bash /pen-build/install-node.sh "${NODE_VERSION}"

# The image installs nothing into /home/agent and leaves nothing in /tmp or root's home or
# from the build's mounts, the agents and the npm agent helper stay off PATH, and the agents
# come only from their recipes (claude, codex, opencode, pi); fail the build if any of that
# changed.
RUN for p in /home/agent /etc/codex /opt/pen/agents /opt/pen/node /pen-build /root/.gnupg; do \
      [ ! -e "$p" ] || { echo "$p exists" >&2; exit 1; }; \
    done; \
    leftover=$(find /tmp /root/.cache /root/.config /root/.local /root/.npm -mindepth 1 2>/dev/null || true); \
    [ -z "${leftover}" ] || { echo "left behind: ${leftover}" >&2; exit 1; }; \
    for c in claude codex opencode pi npm-agent.sh; do ! command -v "$c" >/dev/null || { echo "$c is on PATH" >&2; exit 1; }; done

# Always the last layer. pen upgrade sets PEN_REFRESH to the current time, so it gives a
# new base image ID even when every other layer is cached (and reinstalls Node above), and
# every tools image (the agents too, which are tools) rebuilds on its next use, installing
# latest anew. A normal build leaves it at 0 and is cached.
RUN echo "${PEN_REFRESH}" >/etc/pen/refresh

ENTRYPOINT ["/usr/local/bin/pen-entrypoint"]
CMD ["bash", "-l"]
