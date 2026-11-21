using System.Text.RegularExpressions;

namespace Pen.SmokeTests;

/// <summary>The base's Node, Python and C build tools at work in a session with no tools, and the npm agents on the
/// base's Node, with nothing blocked.</summary>
public sealed class BaseRuntimeTests(BaseRuntimeTests.Fixture fixture) : IClassFixture<BaseRuntimeTests.Fixture>
{
    [Fact(DisplayName = "node: npm installs a package from the registry, and node runs it")]
    public void NpmInstallsAPackage() => AssertHas(fixture.Node, "hello node 1m");

    [Fact(DisplayName = "...and npm builds a native addon with node-gyp")]
    public void NpmBuildsAnAddon() => AssertHas(fixture.Node, "hello addon");

    [Fact(DisplayName =
        "...on the base's node, with Node's headers downloaded from nodejs.org through the proxy")]
    public void AddonDownloadsNodesHeaders() =>
        Assert.True(
            HasLine(fixture.Node, "header-download") && HasLine(fixture.Node, "node=/usr/local/bin/node"),
            fixture.Node.Describe());

    [Fact(DisplayName = "...without npm's Unknown env config warning")]
    public void NpmHasNoUnknownConfigWarning() =>
        Assert.True(
            fixture.Node.OutputComplete
                && !fixture.Node.Output.Contains("Unknown env config", StringComparison.Ordinal),
            fixture.Node.Describe());

    [Fact(DisplayName = "...and fetch gets 200 through the proxy")]
    public void FetchGoesThroughTheProxy() =>
        Assert.True(fixture.Node.HasStdoutLine("fetch 200"), fixture.Node.Describe());

    // Only Node's own fetch is checked: node-gyp's header download runs npm's bundled undici, which may print it.
    [Fact(DisplayName = "...without Node's experimental proxy agent warning from fetch")]
    public void FetchHasNoProxyAgentWarning() =>
        Assert.True(
            fixture.Node.OutputComplete && HasLine(fixture.Node, "fetch-no-proxy-warning"),
            fixture.Node.Describe());

    [Fact(DisplayName = "...with nothing blocked")]
    public void NodeHasNothingBlocked() => AssertNothingBlocked(fixture.Node);

    [Fact(DisplayName = "python: pip installs a package from PyPI into a venv from python3 -m venv")]
    public void PipInstallsIntoAVenv() => AssertMatches(fixture.Python, @"^venv six 1\.");

    [Fact(DisplayName = "...uv adds a package and runs it on the system Python, downloading none")]
    public void UvUsesTheSystemPython() =>
        Assert.True(
            Matches(fixture.Python, @"^hello six 1\.")
                && HasLine(fixture.Python, "base=/usr")
                && HasLine(fixture.Python, "no-python-download"),
            fixture.Python.Describe());

    [Fact(DisplayName = "...and gcc builds a C program against Python.h from python3-config --includes")]
    public void PythonConfigGivesHeaders() =>
        Assert.True(HasLine(fixture.Python, "python-h-ok"), fixture.Python.Describe());

    [Fact(DisplayName = "...with nothing blocked")]
    public void PythonHasNothingBlocked() => AssertNothingBlocked(fixture.Python);

    [Fact(DisplayName = "uv venv -p 3.13 downloads that Python into the home volume (~/.local/share/uv/python)")]
    public void UvDownloadsAnotherPython() =>
        AssertMatches(fixture.OtherPython, @"^v13=/home/agent/\.local/share/uv/python/cpython-3\.13");

    [Fact(DisplayName = "...with nothing blocked")]
    public void OtherPythonHasNothingBlocked() => AssertNothingBlocked(fixture.OtherPython);

    [Fact(DisplayName = "codex, pi and codegraph: --version runs for each")]
    public void AgentsRun()
    {
        var stdout = fixture.Agents.Stdout;

        Assert.True(
            Regex.IsMatch(stdout, "^codex-cli ", RegexOptions.Multiline)
                && Regex.IsMatch(stdout, @"^pi=[0-9]+\.[0-9]+\.[0-9]+$", RegexOptions.Multiline)
                && Regex.IsMatch(stdout, @"^codegraph=1\.[0-9]+\.[0-9]+$", RegexOptions.Multiline),
            fixture.Agents.Describe());
    }

    [Fact(DisplayName = "...codex's and pi's wrappers exec /usr/local/bin/node")]
    public void AgentsUseTheBasesNode() =>
        Assert.True(fixture.Agents.HasStdoutLine("base-node=2"), fixture.Agents.Describe());

    [Fact(DisplayName = "...with nothing blocked")]
    public void AgentsHaveNothingBlocked() => AssertNothingBlocked(fixture.Agents);

    private static bool HasLine(CommandResult result, string line) => result.OutputLines.Contains(line);

    private static bool Matches(CommandResult result, string pattern) =>
        Regex.IsMatch(result.Output, pattern, RegexOptions.Multiline);

    private static void AssertHas(CommandResult result, string text) =>
        Assert.True(result.Output.Contains(text, StringComparison.Ordinal), $"no \"{text}\"; {result.Describe()}");

    private static void AssertMatches(CommandResult result, string pattern) =>
        Assert.True(Matches(result, pattern), $"no line matching {pattern}; {result.Describe()}");

    private static void AssertNothingBlocked(CommandResult result) =>
        Assert.True(
            result.OutputComplete && !result.Output.Contains("blocked:", StringComparison.Ordinal),
            result.Describe());

    /// <summary>Runs a Node session, a Python one and one that downloads another Python, all with no tools, and
    /// one with codex, pi and codegraph; the user's config names no tools.</summary>
    public sealed class Fixture : SmokeFixture
    {
        /// <summary>Gets the Node session, its stdout and stderr apart as well as together.</summary>
        internal CommandResult Node { get; private set; } = null!;

        internal CommandResult Python { get; private set; } = null!;

        internal CommandResult OtherPython { get; private set; } = null!;

        internal CommandResult Agents { get; private set; } = null!;

        protected override void Prepare()
        {
            Home.MakeToolFree();
            RunNode();
            RunPython();
            Agents = Cli.Run("--tool", "codex", "--tool", "pi", "--tool", "codegraph", "run", "bash", "-c", """
                codex --version; echo "pi=$(pi --version)"; echo "codegraph=$(codegraph --version)"
                head -2 /usr/local/bin/codex /usr/local/bin/pi | grep -c "^exec /usr/local/bin/node " | sed "s/^/base-node=/"
                """);
        }

        private void Delete(string relative) => Files.DeleteTree(Path.Combine(Project.Dir, relative));

        private void WriteFile(string relative, string text)
        {
            var path = Path.Combine(Project.Dir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        /// <summary>Installs a package from the registry, builds a native addon with node-gyp against Node's headers,
        /// which it downloads, and fetches through the proxy that NODE_USE_ENV_PROXY has Node use.</summary>
        private void RunNode()
        {
            WriteFile(
                "nodehi/addon/package.json",
                "{ \"name\": \"addon\", \"version\": \"1.0.0\", \"main\": \"build/Release/hello.node\" }\n");
            WriteFile("nodehi/addon/binding.gyp", """
                { 'targets': [ { 'target_name': 'hello', 'sources': [ 'hello.c' ] } ] }

                """);
            WriteFile("nodehi/addon/hello.c", """
                #include <node_api.h>
                static napi_value Hello(napi_env env, napi_callback_info info) {
                  napi_value s;
                  napi_create_string_utf8(env, "hello addon", NAPI_AUTO_LENGTH, &s);
                  return s;
                }
                NAPI_MODULE_INIT() {
                  napi_value fn;
                  napi_create_function(env, "hello", NAPI_AUTO_LENGTH, Hello, NULL, &fn);
                  napi_set_named_property(env, exports, "hello", fn);
                  return exports;
                }

                """);
            Node = Cli.Run("run", "bash", "-c", """
                cd nodehi && npm init -y >/dev/null && npm install --no-fund --no-audit ms@2 >/dev/null &&
                node -e "console.log(\"hello node \" + require(\"ms\")(60000))" &&
                rm -rf ~/.cache/node-gyp && (cd addon && npm install --no-fund --no-audit >/dev/null) &&
                node -e "console.log(require(\"./addon\").hello())" && { [ -e ~/.cache/node-gyp ] && echo header-download; echo "node=$(command -v node)"; }
                npm config list >/dev/null
                err=$(mktemp)
                node -e "fetch(\"https://registry.yarnpkg.com/ms\").then(r => console.log(\"fetch \" + r.status))" 2>"$err"
                grep -qE "UNDICI-EHPA|EnvHttpProxyAgent" "$err" || echo fetch-no-proxy-warning
                cat "$err" >&2; rm -f "$err"
                """);
            Delete("nodehi");
        }

        /// <summary>Runs the system Python's checks (pip in a venv, uv on that interpreter by sys.base_prefix,
        /// Python.h), then has uv download a Python the image lacks.</summary>
        private void RunPython()
        {
            Python = Cli.Run("run", "bash", "-c", """
                python3 -m venv pyvenv && pyvenv/bin/pip install -q --disable-pip-version-check six &&
                pyvenv/bin/python -c "import six; print(\"venv six\", six.__version__)"
                uv init -q pyhi && cd pyhi && uv add -q six &&
                uv run python -c "import six, sys; print(\"hello six\", six.__version__); print(\"base=\" + sys.base_prefix)" && cd ..
                [ -e ~/.local/share/uv/python ] || echo no-python-download
                printf "#include <Python.h>\nint main(void) { return PY_MINOR_VERSION; }\n" >pyhdr.c &&
                gcc $(python3-config --includes) -o pyhdr pyhdr.c && { ./pyhdr; minor=$?; } &&
                [ "$minor" = "$(python3 -c "import sys; print(sys.version_info[1])")" ] && echo python-h-ok
                """);
            foreach (var name in new[] { "pyhi", "pyvenv", "pyhdr", "pyhdr.c" })
                Delete(name);

            OtherPython = Cli.Run("run", "bash", "-c", """
                uv venv -q -p 3.13 v13 && v13/bin/python -c "import sys; print(\"v13=\" + sys.base_prefix)"; uv python uninstall -q 3.13
                rm -rf v13
                """);
        }
    }
}
