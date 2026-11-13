using Arrow.Common.Errors;

namespace Arrow.Cli;

/// <summary>Exit codes of P08 §8.</summary>
public static class ExitCodes
{
    /// <summary>Success. <c>gen</c>: every class has a VALID test.</summary>
    public const int Ok = 0;

    /// <summary><c>gen</c>: at least one class did not reach VALID.</summary>
    public const int NotValid = 1;

    /// <summary>Wrong arguments, configuration or environment; every class BLOCKED; run stopped by an LLM error.</summary>
    public const int Usage = 2;

    /// <summary>Cancelled by the user (Ctrl+C) or a cost question answered "no".</summary>
    public const int Cancelled = 3;

    /// <summary>Unexpected exception.</summary>
    public const int Internal = 4;

    /// <summary>
    /// Exit code for an error that reached the CLI. Every coded error that stops a command is about arguments,
    /// configuration or environment (2), except <c>CLI_INTERNAL</c> (4).
    /// </summary>
    public static int For(ArrowException error) => error.Code == ErrorCodes.CliInternal ? Internal : Usage;
}
