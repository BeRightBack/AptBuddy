namespace AptBuddy.Core;

public static class CommandResultExtensions
{
    public static CommandResult ThrowIfFailed(this CommandResult result, string what)
    {
        if (!result.Success)
            throw new InvalidOperationException($"{what} failed (exit {result.ExitCode}):\n{result.Output}");
        return result;
    }
}

/// <summary>
/// High-level wrapper around the apt toolchain.
/// Read-only queries run as the current user; mutations run through `pkexec`
/// (PolicyKit password prompt — the app never sees the password).
/// </summary>
public sealed class AptService
{
    private HashSet<string>? _installed;
    private List<InstalledPackage>? _installedRows;

    // ---- read-only queries -------------------------------------------------

    public async Task<List<PackageListItem>> SearchAsync(string query, CancellationToken ct = default)
    {
        var r = await AptProcess.RunAsync("apt-cache", new[] { "search", query }, ct).ConfigureAwait(false);
        return Parsers.Search(r.Output);
    }

    /// <summary>Installed packages with versions (`apt list --installed`), cached.</summary>
    public async Task<List<InstalledPackage>> InstalledRowsAsync(bool refresh = false, CancellationToken ct = default)
    {
        if (_installedRows is not null && !refresh && _installed is not null)
            return _installedRows;
        var result = await AptProcess.RunAsync("apt", new[] { "list", "--installed" }, ct).ConfigureAwait(false);
        _installedRows = Parsers.InstalledWithVersions(result.Output);
        _installed = new HashSet<string>(_installedRows.Select(r => r.Name), StringComparer.Ordinal);
        return _installedRows;
    }

    public async Task<HashSet<string>> GetInstalledAsync(bool refresh = false, CancellationToken ct = default)
    {
        await InstalledRowsAsync(refresh, ct).ConfigureAwait(false);
        return _installed ?? new HashSet<string>(StringComparer.Ordinal);
    }

    public async Task<PackagePolicy> PolicyAsync(string name, CancellationToken ct = default)
    {
        var r = await AptProcess.RunAsync("apt-cache", new[] { "policy", name }, ct).ConfigureAwait(false);
        return Parsers.Policy(r.Output, name);
    }

    public async Task<PackageDetails> DetailsAsync(string name, CancellationToken ct = default)
    {
        var policy = await PolicyAsync(name, ct).ConfigureAwait(false);
        var r = await AptProcess.RunAsync("apt-cache", new[] { "show", name }, ct).ConfigureAwait(false);
        return Parsers.Show(r.Output, name, policy.Installed, policy.Candidate);
    }

    public async Task<List<DependencyEntry>> DependsAsync(string name, CancellationToken ct = default)
    {
        var r = await AptProcess.RunAsync("apt-cache", new[] { "depends", name }, ct).ConfigureAwait(false);
        var installed = await GetInstalledAsync(refresh: false, ct).ConfigureAwait(false);
        return Parsers.Depends(r.Output, installed);
    }

    public async Task<List<string>> RDependentsAsync(string name, CancellationToken ct = default)
    {
        var r = await AptProcess.RunAsync("apt-cache", new[] { "rdepends", "--installed", name }, ct).ConfigureAwait(false);
        return Parsers.RDependents(r.Output);
    }

    public async Task<List<UpgradablePackage>> UpgradableAsync(CancellationToken ct = default)
    {
        var r = await AptProcess.RunAsync("apt", new[] { "list", "--upgradable" }, ct).ConfigureAwait(false);
        return Parsers.Upgradable(r.Output);
    }

    public async Task<RemovePreview> AutoremovePreviewAsync(CancellationToken ct = default)
    {
        var r = await AptProcess.RunAsync("apt-get", new[] { "autoremove", "--dry-run" }, ct).ConfigureAwait(false);
        return Parsers.Autoremove(r.Output);
    }

    public async Task<List<string>> HoldsAsync(CancellationToken ct = default)
    {
        var r = await AptProcess.RunAsync("apt-mark", new[] { "showhold" }, ct).ConfigureAwait(false);
        return r.Output.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
    }

    public async Task<List<RepoSource>> RepositoriesAsync(CancellationToken ct = default)
    {
        var _ = ct; // cheap local read
        return RepoService.ReadSources();
    }

    // ---- privileged mutations (pkexec → PolicyKit prompt) ------------------

    public Task<CommandResult> InstallAsync(string name, CancellationToken ct = default) =>
        RunPrivilegedAsync(new[] { "env", "DEBIAN_FRONTEND=noninteractive", "apt-get", "install", "-y", name }, ct);

    public Task<CommandResult> ReinstallAsync(string name, CancellationToken ct = default) =>
        RunPrivilegedAsync(new[] { "env", "DEBIAN_FRONTEND=noninteractive", "apt-get", "install", "-y", "--reinstall", name }, ct);

    public Task<CommandResult> RemoveAsync(string name, CancellationToken ct = default) =>
        RunPrivilegedAsync(new[] { "env", "DEBIAN_FRONTEND=noninteractive", "apt-get", "remove", "-y", name }, ct);

    /// <summary>Installs one or more locally-downloaded .deb files; apt resolves
    /// their dependencies from the repositories (works for upstream LibreOffice
    /// bundles, which are tarballs holding many .debs). Each path is a separate
    /// argv element, so filenames with spaces are safe (no shell involved).</summary>
    public Task<CommandResult> InstallDebsAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        var args = new List<string> { "env", "DEBIAN_FRONTEND=noninteractive", "apt-get", "install", "-y" };
        args.AddRange(paths);
        return RunPrivilegedAsync(args.ToArray(), ct);
    }

    /// <summary>Simulate installing .deb files without changing the system.
    /// Reports unmet dependencies without attempting the install.</summary>
    public Task<CommandResult> InstallDebsDryRunAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        var args = new List<string> { "env", "DEBIAN_FRONTEND=noninteractive", "apt-get", "install", "-s", "-y" };
        args.AddRange(paths);
        return RunPrivilegedAsync(args.ToArray(), ct);
    }

    public Task<CommandResult> InstallDebAsync(string path, CancellationToken ct = default) =>
        InstallDebsAsync(new[] { path }, ct);

    public Task<CommandResult> AutoremoveAsync(CancellationToken ct = default) =>
        RunPrivilegedAsync(new[] { "env", "DEBIAN_FRONTEND=noninteractive", "apt-get", "autoremove", "-y" }, ct);

    public Task<CommandResult> UpgradeAllAsync(CancellationToken ct = default) =>
        RunPrivilegedAsync(new[] { "env", "DEBIAN_FRONTEND=noninteractive", "apt-get", "upgrade", "-y" }, ct);

    public Task<CommandResult> RefreshAsync(CancellationToken ct = default) =>
        RunPrivilegedAsync(new[] { "apt-get", "update" }, ct);

    public Task<CommandResult> HoldAsync(string name, CancellationToken ct = default) =>
        RunPrivilegedAsync(new[] { "apt-mark", "hold", name }, ct);

    public Task<CommandResult> UnholdAsync(string name, CancellationToken ct = default) =>
        RunPrivilegedAsync(new[] { "apt-mark", "unhold", name }, ct);

    /// <summary>Adds a repository by running the verified add-repo.sh under pkexec.</summary>
    public Task<CommandResult> AddRepoAsync(
        string scriptPath,
        IReadOnlyList<string> scriptArgs,
        CancellationToken ct = default)
    {
        var args = new List<string> { "bash", scriptPath };
        args.AddRange(scriptArgs);
        return RunPrivilegedAsync(args.ToArray(), ct);
    }

    public static Task<CommandResult> RunPrivilegedAsync(string[] args, CancellationToken ct = default) =>
        AptProcess.RunAsync("pkexec", args, ct);
}