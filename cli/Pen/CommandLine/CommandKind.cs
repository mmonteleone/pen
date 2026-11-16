namespace Pen.CommandLine;

/// <summary>The command a command line asks for.</summary>
internal enum CommandKind
{
    /// <summary>Runs a command, or a shell, in the project's sandbox.</summary>
    Run,

    /// <summary>pen upgrade.</summary>
    Upgrade,

    /// <summary>pen allow.</summary>
    Allow,

    /// <summary>pen trust.</summary>
    Trust,

    /// <summary>pen sign.</summary>
    Sign,

    /// <summary>pen clean, or pen clean --stale.</summary>
    Clean,

    /// <summary>pen ps.</summary>
    Ps,

    /// <summary>pen --help, or pen &lt;subcommand&gt; --help.</summary>
    Help,

    /// <summary>pen --version.</summary>
    Version,

    /// <summary>pen --tool list.</summary>
    ToolList,
}
