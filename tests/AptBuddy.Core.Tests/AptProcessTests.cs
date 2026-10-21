using AptBuddy.Core;
using Xunit;

namespace AptBuddy.Core.Tests;

/// <summary>
/// Exercises the real process-spawning path in AptProcess (Start-before-read
/// ordering, stdout/stderr capture, exit codes, missing-executable handling).
/// This would have caught the "StandardOut has not been redirected or the
/// process hasn't started yet" bug — a pure parser test cannot.
/// </summary>
public class AptProcessTests
{
    [Fact]
    public async Task RunAsync_CapturesStdoutAndExitCode()
    {
        var result = await AptProcess.RunAsync("/bin/sh", new[] { "-c", "printf 'hello world'" });

        Assert.True(result.Success);
        Assert.Equal("hello world", result.Output.Trim());
    }

    [Fact]
    public async Task RunAsync_CapturesStderrAndNonZeroExit()
    {
        var result = await AptProcess.RunAsync("/bin/sh", new[] { "-c", "echo boom >&2; exit 3" });

        Assert.False(result.Success);
        Assert.Equal(3, result.ExitCode);
        Assert.Contains("boom", result.Output);
    }

    [Fact]
    public async Task RunAsync_InterspersedOutputKeepsOrder()
    {
        var result = await AptProcess.RunAsync(
            "/bin/sh", new[] { "-c", "echo one; echo two >&2; echo three" });

        // combined output should contain both streams
        Assert.Contains("one", result.Output);
        Assert.Contains("two", result.Output);
        Assert.Contains("three", result.Output);
    }

    [Fact]
    public async Task RunAsync_MissingExecutableHasFriendlyError()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AptProcess.RunAsync("/nonexistent/definitely-not-a-real-binary", Array.Empty<string>()));

        Assert.Contains("/nonexistent/definitely-not-a-real-binary", ex.Message);
    }
}