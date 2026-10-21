using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace AptBuddy.Core;

/// <summary>Runs an external process (apt/apt-cache/gpg/pkexec) and captures output.</summary>
public static class AptProcess
{
    public static Task<CommandResult> RunAsync(
        string fileName,
        IEnumerable<string> args,
        CancellationToken cancellationToken = default) =>
        RunAsync(fileName, args.ToArray(), cancellationToken);

    public static async Task<CommandResult> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not start '{fileName}': {ex.Message}. Is it installed and on PATH?", ex);
        }

        // Start() MUST happen before touching StandardOutput/StandardError —
        // the accessors throw until the process has started. Reading both
        // streams as concurrent tasks (instead of sequential ReadToEnd calls)
        // still avoids the classic output-buffer deadlock.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        var combined = string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stdout}\n{stderr}";
        return new CommandResult(process.ExitCode, combined);
    }

    /// <summary>Runs a list of process invocations (e.g. curl, then gpg) sequentially.</summary>
    public static async Task<CommandResult> PipelineAsync(
        IReadOnlyList<(string File, IReadOnlyList<string> Args)> steps,
        CancellationToken cancellationToken = default)
    {
        string lastOutput = "";
        foreach (var (file, args) in steps)
        {
            // Last step of a pipe reads the previous step's stdout from stdin.
            var result = await RunAsync(file, args, cancellationToken).ConfigureAwait(false);
            lastOutput = result.Output;
            if (!result.Success)
                return result;
        }
        return new CommandResult(0, lastOutput);
    }
}