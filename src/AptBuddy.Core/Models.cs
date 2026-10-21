namespace AptBuddy.Core;

/// <summary>One package list row (`apt-cache search`, or synthesized from
/// `apt list --installed`). Version is set for installed rows; IsInstalled marks
/// installed packages wherever they appear.</summary>
public sealed record PackageListItem(string Name, string Summary, string? Version = null, bool IsInstalled = false);

/// <summary>An installed package with its current version (`apt list --installed`).</summary>
public sealed record InstalledPackage(string Name, string Version);

/// <summary>A version + the repo it comes from (from `apt-cache policy`).</summary>
public sealed record VersionInfo(string Version, string? Origin);

/// <summary>Parsed `apt-cache policy` output.</summary>
public sealed record PackagePolicy(
    string Name,
    string? Installed,
    string? Candidate,
    IReadOnlyList<VersionInfo> Versions);

/// <summary>Parsed `apt-cache show` (first stanza) for the details pane.</summary>
public sealed record PackageDetails(
    string Name,
    string? InstalledVersion,
    string? CandidateVersion,
    bool IsInstalled,
    string? Section,
    string? Maintainer,
    string? Homepage);

public enum DependencyKind { Depends, PreDepends, Recommends, Suggests, Conflicts, Replaces, Provides, Breaks, Enhances }

/// <summary>One line from `apt-cache depends`.</summary>
public sealed record DependencyEntry(DependencyKind Kind, string Package, bool IsAlternative, bool IsInstalled);

/// <summary>A configured apt source (either a one-line `.list` entry or a deb822 `.sources` stanza).</summary>
public sealed record RepoSource(
    string File,
    string Format,
    bool Enabled,
    string? Uri,
    string? Suite,
    string? Component,
    string? SignedBy);

/// <summary>One row from `apt list --upgradable`.</summary>
public sealed record UpgradablePackage(string Name, string InstalledVersion, string TargetVersion);

/// <summary>Parsed `apt-get autoremove --dry-run` preview.</summary>
public sealed record RemovePreview(IReadOnlyList<string> Packages, string Summary);

/// <summary>Result of running an external command.</summary>
public sealed record CommandResult(int ExitCode, string Output)
{
    public bool Success => ExitCode == 0;
}