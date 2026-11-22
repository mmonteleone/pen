using System.Text.RegularExpressions;

namespace Pen.IntegrationTests;

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

    [Fact(DisplayName = "...and its build script compiles C++ with the cc crate")]
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

    /// <summary>Runs one session per recipe that builds a small project, stdout and stderr together; the user's
    /// config names no tools.</summary>
    public sealed class Fixture : IntegrationFixture
    {
        internal CommandResult Go { get; private set; } = null!;

        internal CommandResult Cgo { get; private set; } = null!;

        internal CommandResult Rust { get; private set; } = null!;

        internal CommandResult Java { get; private set; } = null!;

        internal CommandResult CodeGraph { get; private set; } = null!;

        protected override void Prepare()
        {
            Home.MakeToolFree();
            RunGo();
            RunRust();
            RunJava();
            RunCodeGraph();
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

            // gcc (the base's, and the recipe's) keeps cgo on, which -race needs too.
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
                set -euo pipefail
                java -version
                java -XshowSettings:properties -version 2>&1 | grep -E "^ +user\.home = "
                work=$(mktemp -d)
                trap 'rm -rf -- "$work"' EXIT
                mvn=https://repo.maven.apache.org/maven2/org/apache/maven/apache-maven/3.9.16/apache-maven-3.9.16-bin.tar.gz
                # Maven Central rate-limits shared CI addresses with 429s: back off for up to 5 minutes,
                # honoring Retry-After, before failing.
                curl -fsSL --retry 7 --retry-all-errors --retry-max-time 300 --max-time 60 -o "$work/maven.tar.gz" "$mvn"
                tar -xzf "$work/maven.tar.gz" -C "$work"
                cd jv
                "$work/apache-maven-3.9.16/bin/mvn" -B -q package
                java -cp "target/hi-1.jar:$HOME/.m2/repository/org/apache/commons/commons-lang3/3.17.0/commons-lang3-3.17.0.jar" hi.Hi
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
    }
}
