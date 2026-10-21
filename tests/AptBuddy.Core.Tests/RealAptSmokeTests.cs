using AptBuddy.Core;
using Xunit;

namespace AptBuddy.Core.Tests;

/// <summary>
/// End-to-end smoke tests that talk to the real `apt` on this machine (Kali,
/// apt 3.3.3). They cover exactly the code paths that crashed at startup
/// (Upgradable + Autoremove preview) through the fixed process runner.
/// Machine-dependent, but on this box apt is always installed.
/// </summary>
public class RealAptSmokeTests
{
    private readonly AptService _apt = new();

    [Fact]
    public async Task UpgradableAsync_RunsAgainstRealApt()
    {
        var rows = await _apt.UpgradableAsync();   // apt list --upgradable

        Assert.NotNull(rows);
        foreach (var r in rows)
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Name));
            Assert.False(string.IsNullOrWhiteSpace(r.TargetVersion));
        }
    }

    [Fact]
    public async Task AutoremovePreviewAsync_RunsAgainstRealApt()
    {
        var preview = await _apt.AutoremovePreviewAsync();   // apt-get autoremove --dry-run

        Assert.NotNull(preview);
        Assert.NotNull(preview.Packages);
        Assert.False(string.IsNullOrWhiteSpace(preview.Summary));
    }

    [Fact]
    public async Task InstalledSetAsync_RunsAgainstRealApt()
    {
        var installed = await _apt.GetInstalledAsync();   // apt list --installed

        Assert.NotNull(installed);
        Assert.Contains("apt", installed);   // apt obviously depends on apt
    }

    [Fact]
    public async Task InstalledRowsAsync_RunsAgainstRealApt()
    {
        var rows = await _apt.InstalledRowsAsync();   // apt list --installed with versions

        Assert.NotEmpty(rows);
        foreach (var r in rows)
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Name));
            Assert.False(string.IsNullOrWhiteSpace(r.Version));
        }
    }
}