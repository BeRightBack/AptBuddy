using AptBuddy.Core;
using Xunit;

namespace AptBuddy.Core.Tests;

public class ParsersTests
{
    [Fact]
    public void Search_ParsesNameAndSummary()
    {
        const string output = """
            code - Code editing. Redefined.
            code-oss - Open Source package of vscode
            nodejs - evented I/O for V8 javascript
            """;
        var items = Parsers.Search(output);

        Assert.Equal(3, items.Count);
        Assert.Equal("code", items[0].Name);
        Assert.Equal("Code editing. Redefined.", items[0].Summary);
        Assert.Equal("nodejs", items[2].Name);
    }

    [Fact]
    public void NamesFromAptList_SkipsListingHeader()
    {
        const string output = """
            Listing...
            code/now 1.139.0-1790100840 amd64 [installed]
            nodejs/now 24.21.0-1nodesource1 amd64 [installed]
            """;
        var names = Parsers.NamesFromAptList(output);

        Assert.Equal(new[] { "code", "nodejs" }, names);
    }

    [Fact]
    public void InstalledWithVersions_ParsesNameAndVersion()
    {
        // Real `apt list --installed` lines on Kali: suffix "suite,now" and the
        // bracket usually says "[installed,automatic]" — both must parse.
        const string output = """
            Listing...
            code/kali-rolling,now 1.139.0-1790100840 amd64 [installed,automatic]
            nodejs/kali-rolling,now 24.21.0-1nodesource1 amd64 [installed,automatic]
            wireshark/kali-rolling,now 4.2.4-1kali1 amd64 [installed]
            """;
        var rows = Parsers.InstalledWithVersions(output);

        Assert.Equal(3, rows.Count);
        Assert.Equal("code", rows[0].Name);
        Assert.Equal("1.139.0-1790100840", rows[0].Version);
        Assert.Equal("wireshark", rows[2].Name);
        Assert.Equal("4.2.4-1kali1", rows[2].Version);
    }

    [Theory]
    [InlineData("nmap", true)]
    [InlineData("burpsuite", true)]
    [InlineData("kali-tools-top10", true)]
    [InlineData("git", true)]
    [InlineData("code", true)]
    [InlineData("php", true)]            // the PHP CLI itself stays visible…
    [InlineData("python3", true)]        // …and so does bare python3
    [InlineData("libc6", false)]
    [InlineData("libmongocrypt0", false)]
    [InlineData("gir1.2-gtk-3.0", false)]
    [InlineData("python3-requests", false)]
    [InlineData("node-express", false)]
    [InlineData("fonts-dejavu-core", false)]
    [InlineData("wireshark-dev", false)]
    [InlineData("libgui-common", false)]
    [InlineData("php-cli", false)]       // …but php-* modules are hidden
    [InlineData("r-cran-ggplot2", false)]
    [InlineData("haskell-Cabal", false)]
    [InlineData("apache2-mod-php8.2", false)]
    [InlineData("linux-headers-amd64", false)]
    [InlineData("linux-image-amd64", false)]
    [InlineData("clang-14", false)]
    [InlineData("doxygen-doc", false)]
    [InlineData("libreoffice", true)]            // LibreOffice family: a UI suite…
    [InlineData("libreoffice26.8-writer", true)] // …whose names start with "lib"
    [InlineData("libobasis26.8-calc", true)]     // but are apps, not libraries
    public void IsApplication_FiltersLibrariesAndDevPackages(string name, bool expected)
    {
        Assert.Equal(expected, Parsers.IsApplication(name));
    }

    [Fact]
    public void Upgradable_ParsesVersions()
    {
        const string output = """
            Listing...
            nodejs/now 24.21.0-1nodesource1 amd64 [installed,upgradable to: 24.22.0-1nodesource1]
            dotnet-sdk-10.0/now 10.0.401-1 amd64 [installed,upgradable to: 10.0.402-1]
            """;
        var rows = Parsers.Upgradable(output);

        Assert.Equal(2, rows.Count);
        Assert.Equal("nodejs", rows[0].Name);
        Assert.Equal("24.21.0-1nodesource1", rows[0].InstalledVersion);
        Assert.Equal("24.22.0-1nodesource1", rows[0].TargetVersion);
        Assert.Equal("dotnet-sdk-10.0", rows[1].Name);
    }

    [Fact]
    public void Upgradable_ParsesSuiteFormatWithUpgradableFrom()
    {
        // Real lines from `apt list --upgradable` on Kali rolling: the suite
        // suffix and the "upgradable from: X" form swap which version is which.
        const string output = """
            Listing...
            libauthen-sasl-perl/kali-rolling 2.2000-2 all [upgradable from: 2.2000-1]
            libbson2-2/kali-rolling 2.5.3-1 amd64 [upgradable from: 2.5.0-1]
            libmongoc2-2/kali-rolling 2.5.3-1 amd64 [upgradable from: 2.5.0-1]
            libmongocrypt0/kali-rolling 1.20.4-1 amd64 [upgradable from: 1.20.2-1]
            """;
        var rows = Parsers.Upgradable(output);

        Assert.Equal(4, rows.Count);
        Assert.Equal("libauthen-sasl-perl", rows[0].Name);
        Assert.Equal("2.2000-1", rows[0].InstalledVersion);
        Assert.Equal("2.2000-2", rows[0].TargetVersion);
        Assert.Equal("libmongocrypt0", rows[3].Name);
        Assert.Equal("1.20.2-1", rows[3].InstalledVersion);
        Assert.Equal("1.20.4-1", rows[3].TargetVersion);
    }

    [Fact]
    public void Upgradable_SkipsPlainInstalledLines()
    {
        const string output = """
            Listing...
            nodejs/now 24.21.0-1nodesource1 amd64 [installed]
            nginx/now 1.24.0-1 amd64 [installed,upgradable to: 1.26.0-1]
            """;
        var rows = Parsers.Upgradable(output);

        Assert.Single(rows);
        Assert.Equal("nginx", rows[0].Name);
    }

    [Fact]
    public void Policy_ParsesInstalledCandidateAndOrigins()
    {
        const string output = """
            dotnet-sdk-10.0:
              Installed: 10.0.401-1
              Candidate: 10.0.401-1
              Version table:
             *** 10.0.401-1 500
                    500 https://packages.microsoft.com/debian/12/prod bookworm/main amd64 Packages
                 10.0.400-1 500
                    500 https://packages.microsoft.com/debian/12/prod bookworm/main amd64 Packages
            """;
        var p = Parsers.Policy(output, "dotnet-sdk-10.0");

        Assert.Equal("10.0.401-1", p.Installed);
        Assert.Equal("10.0.401-1", p.Candidate);
        Assert.Equal(2, p.Versions.Count);
        Assert.Equal("https://packages.microsoft.com/debian/12/prod", p.Versions[0].Origin);
    }

    [Fact]
    public void Policy_NoInstalledVersion()
    {
        const string output = """
            unityhub:
              Installed: (none)
              Candidate: 3.21.3
              Version table:
                 3.21.3 500
                    500 https://hub.unity3d.com/linux/repos/deb stable/main amd64 Packages
            """;
        var p = Parsers.Policy(output, "unityhub");

        Assert.Null(p.Installed);
        Assert.Equal("3.21.3", p.Candidate);
        Assert.Single(p.Versions);
    }

    [Fact]
    public void Depends_ParsesKindsAndInstalledFlag()
    {
        const string output = """
            code
              Depends: libatk1.0-0
              Depends: libc6
              Depends: libcairo2
              Depends: libnss3
                Recommends: ca-certificates
              Recommends: libasound2
            """;
        var installed = new HashSet<string> { "libc6", "ca-certificates" };
        var deps = Parsers.Depends(output, installed);

        Assert.Equal(6, deps.Count);
        Assert.Equal("libatk1.0-0", deps[0].Package);
        Assert.Equal(DependencyKind.Depends, deps[0].Kind);
        Assert.False(deps[0].IsAlternative);
        Assert.True(deps[1].IsInstalled);                 // libc6
        Assert.False(deps[2].IsInstalled);                // libcairo2
        Assert.Equal("libnss3", deps[3].Package);         // Depends, 2-space indent
        Assert.False(deps[3].IsAlternative);
        Assert.Equal(DependencyKind.Recommends, deps[4].Kind);   // 4-space indent = alternative
        Assert.True(deps[4].IsAlternative);
        Assert.True(deps[4].IsInstalled);                 // ca-certificates
        Assert.Equal("libasound2", deps[5].Package);      // 2-space indent = plain
        Assert.False(deps[5].IsAlternative);
    }

    [Fact]
    public void RDependents_ReturnsOnlyReverseBlock()
    {
        const string output = """
            dotnet-sdk-6.0
            Reverse Depends:
              powershell-empire
            """;
        var deps = Parsers.RDependents(output);

        Assert.Equal(new[] { "powershell-empire" }, deps);
    }

    [Fact]
    public void Autoremove_ParsesRemovalBlockAndSummary()
    {
        const string output = """
            Reading package lists... Done
            Building dependency tree... Done
            Reading state information... Done
            0 upgraded, 0 newly installed, 3 to remove and 0 not upgraded.
            The following packages will be REMOVED:
              dotnet-apphost-pack-6.0
              dotnet-hostfxr-6.0
              dotnet-runtime-6.0
            0 upgraded, 0 newly installed, 3 to remove and 0 not upgraded.
            """;
        var preview = Parsers.Autoremove(output);

        Assert.Equal(3, preview.Packages.Count);
        Assert.Contains("dotnet-runtime-6.0", preview.Packages);
        Assert.Contains("3 to remove", preview.Summary);
    }

    [Fact]
    public void Autoremove_FallsBackToRemovingLines()
    {
        const string output = """
            Removing dotnet-apphost-pack-6.0 (6.0.9-1) [unused]
            Removing dotnet-hostfxr-6.0 (6.0.9-1) [unused]
            """;
        var preview = Parsers.Autoremove(output);

        Assert.Equal(2, preview.Packages.Count);
        Assert.Contains("dotnet-hostfxr-6.0", preview.Packages);
    }

    [Fact]
    public void Show_ExtractsFieldsFromFirstStanza()
    {
        const string output = """
            Package: code
            Version: 1.139.0-1790100840
            Section: editors
            Maintainer: Visual Studio Code Team <vscode-linux@microsoft.com>
            Homepage: https://code.visualstudio.com
            Description: Code editing. Redefined.

            Package: code-insiders
            Version: 1.139.0-1790100840
            Description: another stanza, must be ignored
            """;
        var d = Parsers.Show(output, "code", "1.139.0-1790100840", "1.139.0-1790100840");

        Assert.True(d.IsInstalled);
        Assert.Equal("editors", d.Section);
        Assert.Equal("Visual Studio Code Team <vscode-linux@microsoft.com>", d.Maintainer);
        Assert.Equal("https://code.visualstudio.com", d.Homepage);
    }
}

public class RepoServiceTests
{
    [Fact]
    public void ParseOneLine_ExtractsFieldsAndSignedBy()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"aptbuddy-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "extra.list"), """
            # comment
            deb [signed-by=/etc/apt/keyrings/microsoft.gpg] https://packages.microsoft.com/debian/12/prod bookworm main
            deb https://http.kali.org/kali/ kali-rolling main contrib non-free
            """);
        File.WriteAllText(Path.Combine(dir, "deb822.sources"), """
            Types: deb
            URIs: https://hub.unity3d.com/linux/repos/deb
            Suites: stable
            Components: main
            Signed-By: /usr/share/keyrings/Unity_Technologies_ApS.gpg
            """);
        try
        {
            var sources = RepoService.ReadSourcesFrom(Path.Combine(dir, "sources.list"), dir);

            var ms = sources.Single(s => s.Uri == "https://packages.microsoft.com/debian/12/prod");
            Assert.True(ms.Enabled);
            Assert.Equal("bookworm", ms.Suite);
            Assert.Equal("main", ms.Component);
            Assert.Equal("/etc/apt/keyrings/microsoft.gpg", ms.SignedBy);

            var kali = sources.Single(s => s.Uri == "https://http.kali.org/kali/");
            Assert.Equal("kali-rolling", kali.Suite);
            Assert.Equal("main contrib non-free", kali.Component);

            var unity = sources.Single(s => s.Uri == "https://hub.unity3d.com/linux/repos/deb");
            Assert.Equal("deb822", unity.Format);
            Assert.Equal("stable", unity.Suite);
            Assert.Equal("/usr/share/keyrings/Unity_Technologies_ApS.gpg", unity.SignedBy);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}