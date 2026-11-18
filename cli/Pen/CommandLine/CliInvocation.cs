using System.CommandLine;
using Pen.Tools;

namespace Pen.CommandLine;

/// <summary>A valid command line: what Pen is asked to do, and only what that needs from the arguments.</summary>
/// <remarks>
/// <see cref="CommandParser.Parse"/> makes one from a parse it has validated. Each variant names one operation,
/// so a listing carries no domains and a run no subcommand's flags.
/// </remarks>
internal abstract record CliInvocation;

/// <summary>Runs a command, or a shell, in the project's sandbox: pen [options] [command], or pen run.</summary>
/// <param name="Command">The command to run in the sandbox, exactly as given; empty for a shell.</param>
/// <param name="Docker">Whether --docker was given, before the command or after run.</param>
/// <param name="Ports">The --port values, in order without duplicates.</param>
/// <param name="Tools">The --tool values, canonical.</param>
internal sealed record RunInvocation(
    IReadOnlyList<string> Command,
    bool Docker,
    IReadOnlyList<int> Ports,
    IReadOnlyList<ToolSpec> Tools) : CliInvocation;

/// <summary>pen allow &lt;domain…&gt;: adds domains to allow and applies them to running sessions.</summary>
/// <param name="Domains">The domains, lowercased and without duplicates; never empty.</param>
/// <param name="Global">Whether --global was given: the user's config.toml instead of the project's.</param>
internal sealed record AllowDomainsInvocation(IReadOnlyList<string> Domains, bool Global) : CliInvocation;

/// <summary>pen deny &lt;domain…&gt;: adds domains to deny and applies them to running sessions.</summary>
/// <param name="Domains">The domains, lowercased and without duplicates; never empty.</param>
/// <param name="Global">Whether --global was given: the user's config.toml instead of the project's.</param>
internal sealed record DenyDomainsInvocation(IReadOnlyList<string> Domains, bool Global) : CliInvocation;

/// <summary>pen allow without domains: lists the recently blocked domains.</summary>
internal sealed record ListBlockedInvocation : CliInvocation;

/// <summary>pen deny without domains: lists the denied domains.</summary>
internal sealed record ListDeniedInvocation : CliInvocation;

/// <summary>pen allow --defaults: lists the domains Pen allows out of the box.</summary>
internal sealed record ListDefaultDomainsInvocation : CliInvocation;

/// <summary>pen trust: reviews, approves or lists what the project's .pen/ asks for.</summary>
/// <param name="Mode">Review (no flag), approve all (--yes) or list (--list).</param>
internal sealed record TrustInvocation(TrustMode Mode) : CliInvocation;

/// <summary>What pen trust does with what .pen/ asks for.</summary>
internal enum TrustMode
{
    /// <summary>Show it and approve it if the user agrees (pen trust).</summary>
    Review,

    /// <summary>Approve it without asking, for scripts (pen trust --yes).</summary>
    ApproveAll,

    /// <summary>List all of it, however long, and approve nothing (pen trust --list).</summary>
    List,
}

/// <summary>pen clean: starts this project's sandbox fresh.</summary>
internal sealed record CleanInvocation : CliInvocation;

/// <summary>pen clean --stale: removes what moved or deleted projects and old upgrades left, in every project.
/// </summary>
internal sealed record CleanStaleInvocation : CliInvocation;

/// <summary>pen sign: re-signs unpushed commits on the host.</summary>
/// <param name="Base">The base given, exactly as given; null for the default one.</param>
internal sealed record SignInvocation(string? Base) : CliInvocation;

/// <summary>pen upgrade: rebuilds the base image, agents and tools.</summary>
/// <param name="NoCache">Whether --no-cache was given.</param>
internal sealed record UpgradeInvocation(bool NoCache) : CliInvocation;

/// <summary>pen ps, or pen ls: lists running sessions in every project.</summary>
internal sealed record PsInvocation : CliInvocation;

/// <summary>pen tools: lists the agents and tools Pen can install.</summary>
internal sealed record ToolsInvocation : CliInvocation;

/// <summary>pen init: writes a starting .pen/config.toml.</summary>
internal sealed record InitInvocation : CliInvocation;

/// <summary>Help or the version: pen --help, pen &lt;subcommand&gt; --help, pen --version.</summary>
/// <param name="ParseResult">The parse to invoke (<see cref="CommandTree.InvokeBuiltin"/>) for what it asks for.
/// </param>
internal sealed record BuiltinInvocation(ParseResult ParseResult) : CliInvocation;
