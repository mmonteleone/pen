using System.Text.RegularExpressions;

namespace Pen.SmokeTests;

/// <summary>The built-in tool recipes: each builds a small project through the proxy, with nothing blocked.</summary>
public sealed class ToolRecipeTests(ToolRecipeTests.Fixture fixture) : IClassFixture<ToolRecipeTests.Fixture>
{
    [Fact(DisplayName = "go: a module with dependencies builds and runs")]
    public void GoBuildsAndRuns() => AssertHas(fixture.Go, "Hello Go");

    [Fact(DisplayName = "...with the recipe's default, the newest Go 1.27")]
    public void GoDefaultIs127() => AssertMatches(fixture.Go, @"^go version go1\.27(\.[0-9]+)? linux/");

    [Fact(DisplayName = "...with nothing blocked")]
    public void GoHasNothingBlocked() => AssertNothingBlocked(fixture.Go);

    [Fact(DisplayName = "go: cgo is on, and a package that imports \"C\" passes go test -race")]
    public void GoCgoPassesRace() =>
        Assert.True(HasLine(fixture.Cgo, "cgo=1") && HasLine(fixture.Cgo, "race-ok"), fixture.Cgo.Describe());

    [Fact(DisplayName = "rust: a crate with a dependency builds, runs and passes clippy and rustfmt")]
    public void RustBuildsAndRuns() => AssertHas(fixture.Rust, "hello rust 42");

    [Fact(DisplayName =
        "...and its build script compiles C++ with the cc crate, and make is there (the base has neither)")]
    public void RustBuildsCpp() =>
        Assert.True(
            HasLine(fixture.Rust, "hello rust 42 5") && fixture.Rust.HasLineStartingWith("GNU Make "),
            fixture.Rust.Describe());

    [Fact(DisplayName = "...with rustup in the image and cargo's cache in the home volume")]
    public void RustupInImageCargoCacheInHome()
    {
        var last = fixture.Rust.OutputLines.LastOrDefault(line => !line.StartsWith("pen:", StringComparison.Ordinal));

        Assert.True(
            HasLine(fixture.Rust, "RUSTUP_HOME=/usr/local/rustup CARGO_HOME=unset") && last == "1",
            fixture.Rust.Describe());
    }

    [Fact(DisplayName = "...with nothing blocked")]
    public void RustHasNothingBlocked() => AssertNothingBlocked(fixture.Rust);

    [Fact(DisplayName = "java: a Maven project with a dependency builds and runs")]
    public void JavaBuildsAndRuns() => AssertHas(fixture.Java, "Hello java");

    [Fact(DisplayName = "...with the recipe's default, JDK 21")]
    public void JavaDefaultIs21() => AssertMatches(fixture.Java, "^openjdk version \"21\\.");

    [Fact(DisplayName = "...with Java's user.home the home volume, even as rootless Docker's root")]
    public void JavaUserHomeIsTheHomeVolume() => AssertMatches(fixture.Java, @"^ +user\.home = /home/agent$");

    [Fact(DisplayName = "...with nothing blocked")]
    public void JavaHasNothingBlocked() => AssertNothingBlocked(fixture.Java);

    [Fact(DisplayName = "codegraph: indexes a project and finds a symbol")]
    public void CodeGraphFindsASymbol() =>
        Assert.Multiple(() => AssertHas(fixture.CodeGraph, "greet"), () => AssertHas(fixture.CodeGraph, "a.ts:1"));

    [Fact(DisplayName = "...and git ignores its .codegraph/")]
    public void GitIgnoresCodeGraph() => AssertHas(fixture.CodeGraph, "!! cg/.codegraph/");

    [Fact(DisplayName = "...with nothing blocked")]
    public void CodeGraphHasNothingBlocked() => AssertNothingBlocked(fixture.CodeGraph);

    [Fact(DisplayName =
        "without the node tool there's no node, npm or npx, and npm's and Yarn's registries are blocked")]
    public void WithoutNodeRegistriesAreBlocked()
    {
        var lines = fixture.Registries.StdoutLines;

        Assert.True(
            lines[0] == "none" && FieldOf(lines.ElementAtOrDefault(1), 1) == "403"
                && FieldOf(lines.ElementAtOrDefault(2), 1) == "403",
            fixture.Registries.Describe());
    }

    [Fact(DisplayName = "node: a package from the registry installs and runs")]
    public void NodeInstallsAPackage() => AssertHas(fixture.Node, "hello node 1m");

    [Fact(DisplayName = "...and npm builds a native addon with node-gyp")]
    public void NodeBuildsAnAddon() => AssertHas(fixture.Node, "hello addon");

    [Fact(DisplayName = "...against the tarball's headers, with node in /usr/local")]
    public void NodeAddonUsesTheTarballsHeaders() =>
        Assert.True(
            HasLine(fixture.Node, "no-header-download")
                && HasLine(fixture.Node, "nodedir=/usr/local node=/usr/local/bin/node"),
            fixture.Node.Describe());

    [Fact(DisplayName = "...with the recipe's default, Node 26")]
    public void NodeDefaultIs26() => AssertMatches(fixture.Node, @"^v26\.[0-9]+\.[0-9]+$");

    [Fact(DisplayName = "...with nothing blocked")]
    public void NodeHasNothingBlocked() => AssertNothingBlocked(fixture.Node);

    [Fact(DisplayName = "node:22: node -v is 22, and fetch goes through the proxy")]
    public void Node22FetchesThroughTheProxy() =>
        Assert.True(
            Regex.IsMatch(fixture.Node22.Stdout, @"^v22\.[0-9]+\.[0-9]+$", RegexOptions.Multiline)
                && fixture.Node22.HasStdoutLine("fetch 200"),
            fixture.Node22.Describe());

    [Fact(DisplayName = "...without Node's experimental proxy agent warning")]
    public void Node22HasNoProxyAgentWarning() =>
        Assert.True(
            fixture.Node22.OutputComplete && !Regex.IsMatch(fixture.Node22.Stderr, "UNDICI-EHPA|EnvHttpProxyAgent"),
            fixture.Node22.Describe());

    [Fact(DisplayName = "...and codex, pi and codegraph still run, unaffected by it")]
    public void AgentsStillRunWithNode22()
    {
        var stdout = fixture.Node22.Stdout;

        Assert.True(
            Regex.IsMatch(stdout, "^codex-cli ", RegexOptions.Multiline)
                && Regex.IsMatch(stdout, @"^pi=[0-9]+\.[0-9]+\.[0-9]+$", RegexOptions.Multiline)
                && Regex.IsMatch(stdout, @"^1\.[0-9]+\.[0-9]+$", RegexOptions.Multiline),
            fixture.Node22.Describe());
    }

    [Fact(DisplayName = "...codex and pi on Pen's private Node")]
    public void AgentsUseThePrivateNode() =>
        Assert.True(fixture.Node22.HasStdoutLine("private-node=2"), fixture.Node22.Describe());

    [Fact(DisplayName = "...with nothing blocked")]
    public void Node22HasNothingBlocked() => AssertNothingBlocked(fixture.Node22);

    [Fact(DisplayName = "python: python3 and python are the recipe's, its default, Python 3.15")]
    public void PythonIsTheRecipes() =>
        Assert.True(
            HasLine(fixture.Python, "python3=/usr/local/bin/python3 python=/usr/local/bin/python")
                && Matches(fixture.Python, @"^Python 3\.15\.[0-9]+$"),
            fixture.Python.Describe());

    [Fact(DisplayName = "...uv adds a package from PyPI and runs it on that interpreter, downloading none")]
    public void UvUsesTheRecipesPython() =>
        Assert.True(
            Matches(fixture.Python, @"^hello six 1\.")
                && Matches(fixture.Python, @"^base=/usr/local/share/uv/python/cpython-3\.15[.0-9]*-linux-")
                && HasLine(fixture.Python, "no-python-download"),
            fixture.Python.Describe());

    [Fact(DisplayName = "...and pip installs into a venv from python3 -m venv")]
    public void PipInstallsIntoAVenv() => AssertMatches(fixture.Python, @"^venv six 1\.");

    [Fact(DisplayName = "...and python3-config gives its headers to build a C program against")]
    public void PythonConfigGivesHeaders() =>
        Assert.True(HasLine(fixture.Python, "minor=15"), fixture.Python.Describe());

    [Fact(DisplayName = "...with nothing blocked")]
    public void PythonHasNothingBlocked() => AssertNothingBlocked(fixture.Python);

    [Fact(DisplayName = "python:3.12 with python:3.13: python3 is 3.13, and python3.12 is the recipe's")]
    public void TwoPythons() =>
        Assert.True(
            Matches(fixture.TwoPythons, @"^Python 3\.13\.[0-9]+$")
                && Matches(fixture.TwoPythons, @"^Python 3\.12\.[0-9]+$")
                && HasLine(fixture.TwoPythons, "python3.12=/usr/local/bin/python3.12"),
            fixture.TwoPythons.Describe());

    [Fact(DisplayName =
        "...which uv venv uses for 3.12, and a Python not in the image downloads into the home volume")]
    public void UvVenvUsesTheImagesPython() =>
        Assert.True(
            Matches(fixture.TwoPythons, @"^v12=/usr/local/share/uv/python/cpython-3\.12")
                && Matches(fixture.TwoPythons, @"^v14=/home/agent/\.local/share/uv/python/cpython-3\.14"),
            fixture.TwoPythons.Describe());

    [Fact(DisplayName = "...with nothing blocked")]
    public void TwoPythonsHaveNothingBlocked() => AssertNothingBlocked(fixture.TwoPythons);

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

    /// <summary>Gets a field of a line split on spaces, as cut -d' ' -f does (fields count from 0 here).</summary>
    private static string FieldOf(string? line, int field) => line?.Split(' ').ElementAtOrDefault(field) ?? "";

    /// <summary>Runs one session per recipe that builds a small project, stdout and stderr together, and one
    /// without the node tool; the user's config names no tools.</summary>
    public sealed class Fixture : SmokeFixture
    {
        internal CommandResult Go { get; private set; } = null!;

        internal CommandResult Cgo { get; private set; } = null!;

        internal CommandResult Rust { get; private set; } = null!;

        internal CommandResult Java { get; private set; } = null!;

        internal CommandResult CodeGraph { get; private set; } = null!;

        internal CommandResult Registries { get; private set; } = null!;

        internal CommandResult Node { get; private set; } = null!;

        /// <summary>Gets the node:22 session, its stdout and stderr apart.</summary>
        internal CommandResult Node22 { get; private set; } = null!;

        internal CommandResult Python { get; private set; } = null!;

        internal CommandResult TwoPythons { get; private set; } = null!;

        protected override void Prepare()
        {
            Home.MakeToolFree();
            RunGo();
            RunRust();
            RunJava();
            RunCodeGraph();
            RunNode();
            RunPython();
        }

        private CommandResult RunWithTool(string tool, string script) =>
            Cli.Run("--tool", tool, "run", "bash", "-c", script);

        private void Delete(string relative) => Files.DeleteTree(Path.Combine(Project.Dir, relative));

        private string WriteFile(string relative, string text)
        {
            var path = Path.Combine(Project.Dir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            return path;
        }

        private void RunGo()
        {
            Go = RunWithTool("go", """
                mkdir -p gohi && cd gohi && go mod init example.com/hi >/dev/null 2>&1
                printf "package main\nimport (\"fmt\"; \"github.com/google/uuid\"; \"golang.org/x/text/cases\"; \"golang.org/x/text/language\")\nfunc main() { _ = uuid.New(); fmt.Println(cases.Title(language.English).String(\"hello go\")) }\n" >main.go
                go mod tidy && go build -o hi . && ./hi && go version
                """);
            Delete("gohi");

            // The base has no C compiler: the recipe's gcc keeps cgo on, which -race needs too.
            Cgo = RunWithTool("go", """
                mkdir -p gocgo && cd gocgo && go mod init example.com/cgo >/dev/null 2>&1 &&
                printf "package cgo\n\n// int twice(int x) { return 2 * x; }\nimport \"C\"\n\nfunc Twice(x int) int { return int(C.twice(C.int(x))) }\n" >cgo.go &&
                printf "package cgo\n\nimport \"testing\"\n\nfunc TestTwice(t *testing.T) {\n\tdone := make(chan int)\n\tgo func() { done <- Twice(21) }()\n\tif <-done != 42 {\n\t\tt.Fatal(\"twice\")\n\t}\n}\n" >cgo_test.go &&
                echo "cgo=$(go env CGO_ENABLED)" && go test -race . && echo race-ok
                """);
            Delete("gocgo");
        }

        private void RunRust()
        {
            Rust = RunWithTool("rust", """
                cargo new --quiet rshi && cd rshi && cargo add --quiet itoa@1 && cargo add --quiet --build cc@1 &&
                printf "#include <string>\nextern \"C\" int hi_len() { return std::string(\"hello\").size(); }\n" >hi.cpp &&
                printf "fn main() {\n    cc::Build::new().cpp(true).file(\"hi.cpp\").compile(\"hi\");\n}\n" >build.rs &&
                printf "unsafe extern \"C\" {\n    fn hi_len() -> i32;\n}\n\nfn main() {\n    let n = unsafe { hi_len() };\n    println!(\"hello rust {} {}\", itoa::Buffer::new().format(42), n);\n}\n" >src/main.rs &&
                cargo run --quiet && cargo clippy --quiet && cargo fmt --check && make --version | head -1 &&
                echo "RUSTUP_HOME=$RUSTUP_HOME CARGO_HOME=${CARGO_HOME-unset}" && ls ~/.cargo/registry/cache/*/ | grep -c "^itoa-"
                """);
            Delete("rshi");
        }

        /// <summary>Builds a Maven project, with Maven fetched as a wrapper would and a dependency from Maven
        /// Central, through the proxy the recipe tells Java about.</summary>
        private void RunJava()
        {
            WriteFile("jv/pom.xml", """
                <project xmlns="http://maven.apache.org/POM/4.0.0">
                  <modelVersion>4.0.0</modelVersion>
                  <groupId>hi</groupId><artifactId>hi</artifactId><version>1</version>
                  <properties><maven.compiler.release>21</maven.compiler.release><project.build.sourceEncoding>UTF-8</project.build.sourceEncoding></properties>
                  <dependencies><dependency><groupId>org.apache.commons</groupId><artifactId>commons-lang3</artifactId><version>3.17.0</version></dependency></dependencies>
                </project>

                """);
            WriteFile("jv/src/main/java/hi/Hi.java", """
                package hi;
                public class Hi { public static void main(String[] a) { System.out.println(org.apache.commons.lang3.StringUtils.capitalize("hello java")); } }

                """);
            Java = RunWithTool("java", """
                mvn=https://repo.maven.apache.org/maven2/org/apache/maven/apache-maven/3.9.16/apache-maven-3.9.16-bin.tar.gz
                curl -fsSL "$mvn" | tar -xz -C /tmp && cd jv && /tmp/apache-maven-3.9.16/bin/mvn -B -q package &&
                java -cp "target/hi-1.jar:$(ls ~/.m2/repository/org/apache/commons/commons-lang3/3.17.0/*.jar)" hi.Hi && java -version &&
                java -XshowSettings:properties -version 2>&1 | grep -E "^ +user\.home = "
                """);
            Delete("jv");
        }

        private void RunCodeGraph()
        {
            WriteFile("cg/a.ts", """
                export function greet(name: string) { return helper(name); }
                function helper(n: string) { return "hi " + n; }

                """);
            CodeGraph = RunWithTool("codegraph", """
                cd cg && codegraph init . >/dev/null 2>&1 && codegraph query greet && git status --porcelain --ignored .
                """);
            Delete("cg");
        }

        private void RunNode()
        {
            // Without the node tool npm's registries are blocked (registry.npmjs.org comes with node or opencode).
            Registries = Cli.Run("run", "bash", "-c", """
                command -v node npm npx || echo none; registries() { for r in registry.yarnpkg.com registry.npmjs.org; do curl -s -o /dev/null -w "%{http_code} %{http_connect}\n" -I "https://$r/ms"; done; }; registries
                """);

            // A package from the registry, and a native addon that npm builds with node-gyp against the tarball's
            // headers (npm_config_nodedir).
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
            Node = RunWithTool("node", """
                cd nodehi && npm init -y >/dev/null && npm install --no-fund --no-audit ms@2 >/dev/null &&
                node -e "console.log(\"hello node \" + require(\"ms\")(60000))" &&
                rm -rf ~/.cache/node-gyp && (cd addon && npm install --no-fund --no-audit >/dev/null) &&
                node -e "console.log(require(\"./addon\").hello())" && { [ -e ~/.cache/node-gyp ] || echo no-header-download; } &&
                echo "nodedir=$(npm config get nodedir) node=$(command -v node)" && node -v
                """);
            Delete("nodehi");

            // Node 22 warns that the proxy agent NODE_USE_ENV_PROXY turns on is experimental, unless the recipe's
            // NODE_OPTIONS turns that warning off.
            Node22 = Cli.Run(
                "--tool", "node:22", "--tool", "codegraph", "--tool", "codex", "--tool", "pi", "run", "bash", "-c", """
                node -v; node -e "fetch(\"https://registry.yarnpkg.com/ms\").then(r => console.log(\"fetch \" + r.status))"; npm -v >/dev/null
                codex --version; echo "pi=$(pi --version)"; codegraph --version
                head -2 /usr/local/bin/codex /usr/local/bin/pi | grep -c "^exec /opt/pen/node/bin/node " | sed "s/^/private-node=/"
                """);
        }

        /// <summary>Runs the python recipe's checks: its interpreters are the ones used, by path, version and
        /// sys.base_prefix.</summary>
        private void RunPython()
        {
            Python = RunWithTool("python", """
                echo "python3=$(command -v python3) python=$(command -v python)"; python3 --version
                uv init -q pyhi && cd pyhi && uv add -q six &&
                uv run python -c "import six, sys; print(\"hello six\", six.__version__); print(\"base=\" + sys.base_prefix)" && cd .. &&
                python3 -m venv pyvenv && pyvenv/bin/pip install -q --disable-pip-version-check six &&
                pyvenv/bin/python -c "import six; print(\"venv six\", six.__version__)" &&
                { [ -e ~/.local/share/uv/python ] || echo no-python-download; } &&
                printf "#include <Python.h>\nint main(void) { return PY_MINOR_VERSION; }\n" >pyhdr.c &&
                gcc $(python3-config --includes) -o pyhdr pyhdr.c && { ./pyhdr; echo "minor=$?"; }
                """);
            foreach (var name in new[] { "pyhi", "pyvenv", "pyhdr", "pyhdr.c" })
                Delete(name);

            TwoPythons = Cli.Run("--tool", "python:3.12", "--tool", "python:3.13", "run", "bash", "-c", """
                echo "python3.12=$(command -v python3.12)"; python3 --version; python3.12 --version
                uv venv -q -p 3.12 v12 && v12/bin/python -c "import sys; print(\"v12=\" + sys.base_prefix)"
                uv venv -q -p 3.14 v14 && v14/bin/python -c "import sys; print(\"v14=\" + sys.base_prefix)"; uv python uninstall -q 3.14
                rm -rf v12 v14
                """);
        }
    }
}
