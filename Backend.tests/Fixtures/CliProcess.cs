using System.Diagnostics;

namespace Backend.tests.Fixtures;

/// <summary>
/// runs the built backend executable as a child process
/// </summary>
public static class CliProcess {

    /// <summary>
    /// runs Backend.dll with the given arguments and waits for it to exit
    /// </summary>
    /// <param name="arguments">command line arguments, verb first</param>
    /// <param name="configureEnvironment">changes the child's environment variables before the start</param>
    /// <returns>exit code and captured output</returns>
    public static async Task<CliResult> RunAsync(IEnumerable<string> arguments, Action<IDictionary<string, string?>> configureEnvironment) {
        ProcessStartInfo start = new("dotnet") {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Backend.dll"));
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        configureEnvironment(start.Environment);

        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new CliResult(process.ExitCode, await stdout, await stderr);
    }
}
