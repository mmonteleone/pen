# Tools and agents

Every session includes Node LTS, npm, Python, pip, uv, Git and C build tools. Pen installs agents and other toolchains on request and reuses the image across projects that select the same tools.

- [Adding tools](#adding-tools)
- [Agents](#agents)
- [Node and Python](#node-and-python)
- Toolchain notes: [.NET](#net), [Go](#go), [Rust](#rust), [Java](#java) and [CodeGraph](#codegraph)

## Adding tools

```sh
pen tools                                # list agents, tools and default versions
pen --tool go claude                      # add Go for this session
pen --tool dotnet:8 --tool dotnet:10 codex  # pick versions
```

Use `name` for the default version or `name:version` to choose one. To use the same tools every time, add them to your [user or project config](usage.md#configuration):

```toml
tools = ["go", "rust"]
```

User config, project config and repeatable `--tool` flags add together. Tools requested by a project's `.pen/config.toml` need [approval](usage.md#approving-project-settings).

| Tool | Version format | Multiple versions together |
|---|---|---|
| `dotnet` | SDK major, `6` through `11` | Yes |
| `go` | Major and minor, such as `1.27` | No |
| `rust` | `stable`, a supported minor such as `1.85`, or a patch the recipe lists | Yes |
| `java` | JDK major, `8`, `11`, `17`, `21` or `25` | Yes |
| `codegraph` | Major version `1` | No |

Versions are literal, so `go:1.27` works and `go:1.27.0` is refused. Run `pen tools` for the defaults in your build. The [recipes](../tools/) define the accepted versions and the domains each tool adds to the allowlist.

For software that isn't available as a tool, use a [project Dockerfile](usage.md#extra-software-with-a-dockerfile).

## Agents

The first word of the program selects the agent, so `pen claude` installs Claude Code. Use `--tool` to pin a version, or to include an agent when the first word is something else, such as a wrapper:

```sh
pen --tool claude:2.1.300 claude
pen --tool claude run timeout 60 claude
pen --tool claude                    # open a shell with Claude installed
```

You can also pin an agent in `tools` in either config file. The default version, `latest`, is resolved when the image builds. Run [`pen upgrade`](usage.md#upgrading-and-cleaning-up) to refresh it.

For login, API keys and shared agent settings, see [configuration](usage.md#logins-and-api-keys).

OpenCode and Pi can reach Anthropic and OpenAI by default. Use `pen allow` for another provider's API domain.

## Node and Python

Use `npx` or project dependencies for Node packages. `npm install -g` can't write to the image's installation directory during a session.

For Python packages, use `uv sync` or a virtual environment from `uv venv` or `python3 -m venv`. The system Python is read-only. uv can download a project's Python version and cache it in the sandbox home.

For a different system Node or Python, use a [project Dockerfile](usage.md#extra-software-with-a-dockerfile). Put the new binary ahead of the base image's on `PATH`, or install it in place of the original.

## .NET

SDKs install in `/usr/share/dotnet`, with the newest release of each selected major version. The default is .NET 11. PowerShell (`pwsh`) is included at a version pinned by Pen. The NuGet cache uses a project volume at your host's `NUGET_PACKAGES` path, with or without this tool.

The tool follows the [Ubuntu .NET SDK image's defaults](https://github.com/dotnet/dotnet-docker/tree/main/src/sdk), including an optional `app` user and group with UID/GID `1654`, home `/home/app`, shell `/bin/sh`, and a locked password. Like the SDK image, the tools image defaults to `root`. A project Dockerfile can use `USER app` or `USER $APP_UID` for build steps, then switch back to `USER root` for Pen's entrypoint.

Pen sessions run with the host's UID/GID and home `/home/agent`; if the host UID is `1654`, Pen reuses `app` with that session home, `/bin/bash`, and the host's primary GID. SDK 10 and later also expose `dnx` on `PATH`.

Pen keeps telemetry disabled and omits `DOTNET_VERSION`, `ASPNET_VERSION` and `DOTNET_SDK_VERSION`, since multiple SDK versions can coexist.

The tool sets these environment variables:

| Variable | Value |
|---|---|
| `APP_UID` | `1654` |
| `ASPNETCORE_HTTP_PORTS` | `8080` |
| `DOTNET_RUNNING_IN_CONTAINER` | `true` |
| `DOTNET_GENERATE_ASPNET_CERTIFICATE` | `false` |
| `DOTNET_USE_POLLING_FILE_WATCHER` | `true` |
| `VSTEST_ARTIFACTS_BASE_DIRECTORY` | `/tmp` |
| `NUGET_XMLDOC_MODE` | `skip` |
| `POWERSHELL_DISTRIBUTION_CHANNEL` | `PSDocker-DotnetSDK-Ubuntu-26.04` |
| `DOTNET_NOLOGO` | `1` |
| `DOTNET_CLI_TELEMETRY_OPTOUT` | `1` |

To reach an ASP.NET Core dev server from the host, see [dev servers](usage.md#dev-servers).

## Go

Go installs in `/usr/local/go` from the official tarball, with the newest patch release of the selected version. Ubuntu's `gcc` and `libc6-dev` are included, so cgo and `go test -race` work.

If a module needs a newer Go, Go downloads that toolchain through the module proxy into the home volume. The proxy redirects toolchains and large modules to `storage.googleapis.com`, so the Go tool allows that domain, which includes every bucket on it.

`go install` puts commands in `~/go/bin`, which isn't on `PATH`. Run them by path or add that directory to `PATH` in your shell config.

## Rust

Toolchains install through rustup in `/usr/local/rustup`, with clippy, rustfmt and Ubuntu's `build-essential` for linking and build scripts. The default toolchain is `stable` if selected, otherwise the newest installed version.

A toolchain requested by `rust-toolchain.toml` must be installed in the image; it can't be added during a session. Add it with `--tool` or in config.

Cargo's cache and installed commands go to `~/.cargo` in the home volume. `~/.cargo/bin` isn't on `PATH`; run installed commands by path or add the directory in your shell config.

## Java

Ubuntu's headless JDKs install in `/usr/lib/jvm`. `java` and `javac` use the newest installed version. Maven and Gradle aren't installed, so use the project's wrappers (`mvnw`, `gradlew`).

Java ignores `HTTPS_PROXY`, so Pen configures each JDK to use `proxy:3128` and sets `MAVEN_OPTS` so Maven uses those settings. Dockerfile build steps have no proxy: a step that downloads with Java needs `JAVA_TOOL_OPTIONS="-Dhttp.proxyHost= -Dhttps.proxyHost="` to connect directly.

## CodeGraph

[CodeGraph](https://www.npmjs.com/package/@colbymchenry/codegraph) installs from npm into `/opt/pen/codegraph` and runs on its bundled Node. Git ignores `.codegraph/` in every session.

To use it from an agent, add `tools = ["codegraph"]` to your user config and register its MCP server in the [home seed](usage.md#agent-config-the-home-seed). For Claude, use `.claude.json`:

```json
{ "mcpServers": { "codegraph": { "type": "stdio", "command": "codegraph", "args": ["serve", "--mcp"] } } }
```

For Codex, use `.codex/config.toml`:

```toml
[mcp_servers.codegraph]
command = "codegraph"
args = ["serve", "--mcp"]
```
