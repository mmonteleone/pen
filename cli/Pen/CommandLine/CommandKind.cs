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

    /// <summary>pen deny.</summary>
    Deny,

    /// <summary>pen init.</summary>
    Init,

    /// <summary>pen trust.</summary>
    Trust,

    /// <summary>pen sign.</summary>
    Sign,

    /// <summary>pen clean, or pen clean --stale.</summary>
    Clean,

    /// <summary>pen ps.</summary>
    Ps,

    /// <summary>pen tools.</summary>
    Tools,

    /// <summary>Help or the version: pen --help, pen &lt;subcommand&gt; --help, pen --version.</summary>
    Builtin,
}
