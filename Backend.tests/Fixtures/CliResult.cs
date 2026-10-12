namespace Backend.tests.Fixtures;

/// <summary>
/// exit code and captured output of one run of the backend executable
/// </summary>
/// <param name="ExitCode">process exit code</param>
/// <param name="StdOut">captured standard output</param>
/// <param name="StdErr">captured standard error</param>
public sealed record CliResult(int ExitCode, string StdOut, string StdErr);
