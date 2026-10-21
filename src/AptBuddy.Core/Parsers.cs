using System.Text.RegularExpressions;

namespace AptBuddy.Core;

/// <summary>
/// Pure functions turning raw apt command output into strongly-typed models.
/// Kept free of I/O so the parsing logic is unit-testable.
/// </summary>
public static partial class Parsers
{
    /// <summary>`apt-cache search` → name/summary rows ("name - summary").</summary>
    public static List<PackageListItem> Search(string output)
    {
        var list = new List<PackageListItem>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var sep = line.IndexOf(" - ", StringComparison.Ordinal);
            if (sep <= 0) continue;
            var name = line[..sep].Trim();
            if (name.Length == 0) continue;
            list.Add(new PackageListItem(name, line[(sep + 3)..].Trim()));
        }
        return list;
    }

    /// <summary>`apt list --installed` → package names.</summary>
    public static List<string> NamesFromAptList(string output)
    {
        var names = new List<string>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("Listing...", StringComparison.Ordinal)) continue;
            var slash = line.IndexOf('/');
            if (slash <= 0) continue;
            names.Add(line[..slash]);
        }
        return names;
    }

    /// <summary>`apt list --installed` → name/version rows.</summary>
    public static List<InstalledPackage> InstalledWithVersions(string output)
    {
        var list = new List<InstalledPackage>();
        var row = InstalledRowPattern();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("Listing...", StringComparison.Ordinal)) continue;
            var m = row.Match(line);
            if (!m.Success) continue;
            list.Add(new InstalledPackage(m.Groups["name"].Value, m.Groups["ver"].Value));
        }
        return list;
    }

    /// <summary>
    /// Heuristic: is this a user-facing application rather than a library, dev
    /// tool, language module, font pack, etc.? Powers the "apps only" filter in
    /// the Installed/Available views ("All" never filters). It's a toggle in the
    /// UI, so false negatives are always recoverable.
    /// </summary>
    public static bool IsApplication(string name)
    {
        if (name.Length == 0) return false;
        // LibreOffice is a UI suite whose upstream package names collide with
        // the generic "lib* = library" heuristic (libreoffice26.8-writer,
        // libobasis26.8-calc, …) — treat the whole family as applications.
        if (name.StartsWith("libreoffice", StringComparison.Ordinal)
            || name.StartsWith("libobasis", StringComparison.Ordinal))
            return true;
        foreach (var prefix in AppLikePrefixes)
            if (name.StartsWith(prefix, StringComparison.Ordinal))
                return false;
        foreach (var suffix in LibraryLikeSuffixes)
            if (name.EndsWith(suffix, StringComparison.Ordinal))
                return false;
        return true;
    }

    private static readonly string[] AppLikePrefixes =
    {
        // C libraries, language bindings/modules, dev toolchains, kernel build
        // artifacts, R/Haskell packages — dependency noise hiding real apps.
        "lib", "gir1.2-", "fonts-", "golang-", "node-", "ruby-", "perl-", "lua-",
        "php-", "python3-", "python2-", "python-", "dh-", "clang-", "llvm-",
        "linux-headers", "linux-modules", "linux-image", "linux-source",
        "r-cran-", "r-bioc-", "r-other-", "haskell-", "ghc-", "apache2-mod-",
    };

    private static readonly string[] LibraryLikeSuffixes =
    {
        "-dev", "-dbg", "-dbgsym", "-doc", "-headers", "-common", "-data",
        "-tests", "-examples", "-static", "-source", "-modules",
    };

    /// <summary>`apt list --upgradable` → rows. Two formats exist:
    /// `name/now VER ARCH [installed,upgradable to: NEW]` and
    /// `name/SUITE NEW ARCH [upgradable from: VER]` (the latter when the installed
    /// version differs from the "now" version in the lists — e.g. Kali rolling).</summary>
    public static List<UpgradablePackage> Upgradable(string output)
    {
        var list = new List<UpgradablePackage>();
        var row = UpgradableRowPattern();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("Listing", StringComparison.Ordinal)) continue;
            var m = row.Match(line);
            if (!m.Success) continue;

            var name = m.Groups["name"].Value;
            var first = m.Groups["first"].Value;   // version right after "name/suffix"
            var rest = m.Groups["rest"].Value;     // text inside the brackets

            string installed, target;
            var to = Regex.Match(rest, @"upgradable to:\s+(\S+)");
            var from = Regex.Match(rest, @"upgradable from:\s+(\S+)");
            if (to.Success)
            {
                installed = first;
                target = to.Groups[1].Value;
            }
            else if (from.Success)
            {
                target = first;
                installed = from.Groups[1].Value;
            }
            else
            {
                continue; // a plain "[installed]" line — not an upgrade
            }
            list.Add(new UpgradablePackage(name, installed, target));
        }
        return list;
    }

    /// <summary>`apt-cache policy <pkg>` → installed/candidate/version table.</summary>
    public static PackagePolicy Policy(string output, string name)
    {
        string? installed = null, candidate = null;
        var versions = new List<VersionInfo>();
        var lines = output.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (t.StartsWith("Installed:", StringComparison.Ordinal))
                installed = NullIfNone(t["Installed:".Length..].Trim());
            else if (t.StartsWith("Candidate:", StringComparison.Ordinal))
                candidate = NullIfNone(t["Candidate:".Length..].Trim());
            else if (t.StartsWith("*** ", StringComparison.Ordinal) || Regex.IsMatch(t, @"^\S+ \d+$"))
            {
                // version table row:  "*** 24.21.0-1nodesource1 500" or " 24.20.0-1nodesource1 500"
                var ver = t.StartsWith("*** ", StringComparison.Ordinal)
                    ? t[4..].Split(' ', 2)[0].Trim()
                    : t.Split(' ', 2)[0].Trim();
                if (ver.Length == 0 || ver == "Version") continue;
                string? origin = null;
                // the following deeper-indented line names the repo: "500 https://... Packages"
                if (i + 1 < lines.Length)
                {
                    var next = lines[i + 1].Trim();
                    var m = OriginPattern().Match(next);
                    if (m.Success) origin = m.Groups["url"].Value;
                }
                versions.Add(new VersionInfo(ver, origin));
            }
        }
        return new PackagePolicy(name, installed, candidate, versions);
    }

    /// <summary>`apt-cache show <pkg>` (first stanza) → key fields.</summary>
    public static PackageDetails Show(string output, string name, string? installedVersion, string? candidateVersion)
    {
        string? section = null, maintainer = null, homepage = null;
        var inFirstStanza = true;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw;
            if (line.Length == 0 || line.StartsWith("Package:", StringComparison.Ordinal))
            {
                if (line.StartsWith("Package:", StringComparison.Ordinal) && line.Trim() != $"Package: {name}")
                    inFirstStanza = false;
                continue;
            }
            if (!inFirstStanza) continue;
            if (line.StartsWith("Section:", StringComparison.Ordinal)) section = line["Section:".Length..].Trim();
            else if (line.StartsWith("Maintainer:", StringComparison.Ordinal)) maintainer = line["Maintainer:".Length..].Trim();
            else if (line.StartsWith("Homepage:", StringComparison.Ordinal)) homepage = line["Homepage:".Length..].Trim();
        }
        return new PackageDetails(name, installedVersion, candidateVersion,
            installedVersion != null, section, maintainer, homepage);
    }

    /// <summary>`apt-cache depends <pkg>` → dependency entries.</summary>
    public static List<DependencyEntry> Depends(string output, IReadOnlySet<string> installed)
    {
        var list = new List<DependencyEntry>();
        var row = DependencyRowPattern();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var m = row.Match(line);
            if (!m.Success) continue;

            var indent = line.Length - line.TrimStart().Length;
            var alt = indent >= 4; // alternatives ("|Depends:") are more indented
            var kindText = m.Groups["kind"].Value.Trim();
            var pkg = m.Groups["pkg"].Value.Trim().Trim('<', '>');
            var arch = pkg.IndexOf(":", StringComparison.Ordinal);
            if (arch > 0) pkg = pkg[..arch];

            if (!Enum.TryParse<DependencyKind>(kindText, true, out var kind)) continue;
            var isInstalled = installed.Contains(pkg);
            list.Add(new DependencyEntry(kind, pkg, alt, isInstalled));
        }
        return list;
    }

    /// <summary>`apt-cache rdepends --installed <pkg>` → installed dependents.</summary>
    public static List<string> RDependents(string output)
    {
        var list = new List<string>();
        var reverse = false;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("Reverse Depends:", StringComparison.Ordinal))
            {
                reverse = true;
                continue;
            }
            if (!reverse || line.Length == 0) continue;
            var pkg = line.Trim();
            var colon = pkg.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0) pkg = pkg[..colon];
            if (pkg.Length > 0 && char.IsLetter(pkg[0]))
                list.Add(pkg);
        }
        return list;
    }

    /// <summary>`apt-get autoremove --dry-run` → the "will be REMOVED" block.</summary>
    public static RemovePreview Autoremove(string output)
    {
        var packages = new List<string>();
        var inBlock = false;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("The following packages will be REMOVED:", StringComparison.Ordinal))
            {
                inBlock = true;
                continue;
            }
            if (inBlock)
            {
                // package rows are indented; an unindented line (summary) ends the block
                if (line.Length == 0 || !raw.StartsWith(' '))
                    break;
                packages.AddRange(line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            }
        }
        // Fallback: lines like "Removing foo (1.0-1) [unused]"
        if (packages.Count == 0)
        {
            foreach (var raw in output.Split('\n'))
            {
                var line = raw.Trim();
                if (!line.StartsWith("Removing ", StringComparison.Ordinal)) continue;
                var pkg = line["Removing ".Length..].Split(' ', 2)[0].Trim();
                if (pkg.Length > 0) packages.Add(pkg);
            }
        }

        var summary = SummaryLine(output) ?? "0 removed";
        return new RemovePreview(packages.Distinct().ToList(), summary);
    }

    private static string? SummaryLine(string output)
    {
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (Regex.IsMatch(line, @"\d+ upgraded, \d+ newly installed, \d+ to remove"))
                return line;
        }
        return null;
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private static string? NullIfNone(string value) =>
        value.Length == 0 || value == "(none)" ? null : value;

    [GeneratedRegex(@"^(?<name>[^\s/]+)/\S+\s+(?<ver>\S+)\s+\S+\s+\[installed[^\]]*\]")]
    private static partial Regex InstalledRowPattern();

    [GeneratedRegex(@"^(?<name>[^\s/]+)/\S+\s+(?<first>\S+)\s+\S+\s+\[(?<rest>[^\]]+)\]")]
    private static partial Regex UpgradableRowPattern();

    [GeneratedRegex(@"^\d+\s+(?<url>https?://\S+)")]
    private static partial Regex OriginPattern();

    [GeneratedRegex(@"^\s*(?<kind>[A-Za-z]+):\s*(?<pkg>.+)")]
    private static partial Regex DependencyRowPattern();
}