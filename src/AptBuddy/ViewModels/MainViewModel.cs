using System.Collections.ObjectModel;
using System.Formats.Tar;
using System.IO.Compression;
using AptBuddy.Core;

namespace AptBuddy.ViewModels;

/// <summary>What the Packages tab is showing: installed packages, the available
/// catalog, or literally everything (installed ∪ available, unfiltered).</summary>
public enum PkgView { Installed, Available, All }

public sealed class MainViewModel : ViewModelBase
{
    private readonly AptService _apt = new();

    private string _searchTerm = "";
    private string? _pendingSearch;
    private int _busyCount;
    private string _status = "Ready.";
    private string _outputLog = "";
    private PackageListItem? _selected;
    private PackageDetails? _details;
    private bool _isHeld;
    private string _holdLabel = "Hold";
    private string _cleanupSummary = "";
    private string _updatesSummary = "";
    private PkgView _view = PkgView.Available;
    private bool _appsOnly = true;
    private List<InstalledPackage> _installedRows = new();
    private HashSet<string> _installedNames = new(StringComparer.Ordinal);
    private List<PackageListItem> _allAvailable = new();
    private bool _packagesInitialized;
    private bool _showAll;
    private int _moreCount;
    private Task? _initInFlight;
    private CancellationTokenSource? _filterCts;

    public ObservableCollection<PackageListItem> SearchResults { get; } = new();
    public ObservableCollection<DepRow> Dependencies { get; } = new();
    public ObservableCollection<string> RDependents { get; } = new();
    public ObservableCollection<UpgradablePackage> Upgradable { get; } = new();
    public ObservableCollection<RepoRow> Repositories { get; } = new();
    public ObservableCollection<string> AutoremoveList { get; } = new();
    public ObservableCollection<string> Holds { get; } = new();

    public string SearchTerm
    {
        get => _searchTerm;
        set
        {
            if (Set(ref _searchTerm, value)) DebounceFilter();
        }
    }

    /// <summary>Which package view is active. Switching re-renders from cache.</summary>
    public PkgView View
    {
        get => _view;
        set
        {
            if (Set(ref _view, value))
            {
                _showAll = false; // a fresh view starts from its cap again
                Raise(nameof(ViewIsInstalled));
                Raise(nameof(ViewIsAvailable));
                Raise(nameof(ViewIsAll));
                _ = FilterCurrentAsync();
            }
        }
    }

    /// <summary>Hides libraries/dev packages (Parsers.IsApplication) in the
    /// Installed/Available views. The "All" view is always unfiltered.</summary>
    public bool AppsOnly
    {
        get => _appsOnly;
        set { if (Set(ref _appsOnly, value)) _ = FilterCurrentAsync(); }
    }

    public bool ViewIsInstalled { get => View == PkgView.Installed; set { if (value) View = PkgView.Installed; } }
    public bool ViewIsAvailable { get => View == PkgView.Available; set { if (value) View = PkgView.Available; } }
    public bool ViewIsAll { get => View == PkgView.All; set { if (value) View = PkgView.All; } }

    public string ShownCount => $"{SearchResults.Count:N0} shown";
    public bool MoreVisible => !_showAll && _moreCount > 0;
    public string ShowAllLabel => _moreCount > 0 ? $"Show all {_moreCount:N0}" : "";
    public bool HasNoResults => _packagesInitialized && SearchResults.Count == 0;

    public bool HasSelection => Selected is not null;
    public bool NoSelection => Selected is null;

    /// <summary>Single multiline summary shown in the idle details pane.</summary>
    public string IdleStats { get; private set; } = "Loading…";

    private void RebuildIdleStats()
    {
        if (!_packagesInitialized)
        {
            IdleStats = "Loading…";
            Raise(nameof(IdleStats));
            return;
        }
        var appInstalled = _installedRows.Count(r => Parsers.IsApplication(r.Name));
        var appAvailable = _allAvailable.Count(a => Parsers.IsApplication(a.Name));
        IdleStats =
            $"{_installedRows.Count:N0} packages installed · {appInstalled:N0} apps\n" +
            $"{_allAvailable.Count:N0} packages available · {appAvailable:N0} apps\n" +
            $"{Upgradable.Count:N0} upgrades ready · {Holds.Count:N0} held · {AutoremoveList.Count:N0} removable";
        Raise(nameof(IdleStats));
    }

    /// <summary>Removes the render cap and shows every matching row.</summary>
    public async Task ShowAllAsync()
    {
        if (_moreCount == 0) return;
        _showAll = true;
        await FilterCurrentAsync();
    }

    /// <summary>Records a search term supplied on the command line (e.g. `aptbuddy nmap`)
    /// and applies it to the search box once the package catalog has loaded.</summary>
    public void SetInitialSearch(string term)
    {
        _pendingSearch = term;
        if (_packagesInitialized) ApplyPendingSearch();
    }

    private void ApplyPendingSearch()
    {
        if (string.IsNullOrWhiteSpace(_pendingSearch)) return;
        SearchTerm = _pendingSearch.Trim(); // setter triggers the debounced filter
        _pendingSearch = null;
    }

    // Reference-counted so several read-only loads can overlap (startup loads
    // all tabs in parallel) — previously an in-flight op silently dropped a new one.
    public bool IsBusy => _busyCount > 0;

    public bool CanAct => !IsBusy;

    public string Status { get => _status; set => Set(ref _status, value); }

    public string OutputLog { get => _outputLog; set => Set(ref _outputLog, value); }

    public PackageListItem? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
            {
                Raise(nameof(HasSelection));
                Raise(nameof(NoSelection));
                _ = LoadDetailsAsync(value);
            }
        }
    }

    public PackageDetails? Details { get => _details; set => Set(ref _details, value); }

    public bool IsHeld { get => _isHeld; set => Set(ref _isHeld, value); }

    public string HoldLabel
    {
        get => _holdLabel;
        set => Set(ref _holdLabel, value);
    }

    public string CleanupSummary { get => _cleanupSummary; set => Set(ref _cleanupSummary, value); }
    public string UpdatesSummary { get => _updatesSummary; set => Set(ref _updatesSummary, value); }

    public string SelectedSummary => Selected?.Summary ?? "";
    public string SelectedInstalled => Details is null ? "" : Details.IsInstalled ? $"Installed: {Details.InstalledVersion}" : "Not installed";
    public string SelectedCandidate => Details?.CandidateVersion is null ? "" : $"Candidate: {Details.CandidateVersion}";
    public string SelectedSection => Details?.Section is null ? "" : $"Section: {Details.Section}";
    public string SelectedMaintainer => Details?.Maintainer is null ? "" : $"Maintainer: {Details.Maintainer}";

    // ---- packages tab ------------------------------------------------------

    /// <summary>Keeps the browse catalog in sync with reality: packages installed
    /// from a local .deb have no repo entry, so `apt-cache search` never lists
    /// them — merge those in so Installed/All views and searches find them
    /// (e.g. google-chrome-stable).</summary>
    private void MergeLocalOnlyInstalled()
    {
        var known = new HashSet<string>(_allAvailable.Count, StringComparer.Ordinal);
        foreach (var p in _allAvailable) known.Add(p.Name);
        foreach (var inst in _installedRows)
        {
            if (!known.Add(inst.Name)) continue;
            _allAvailable.Add(new PackageListItem(inst.Name, string.Empty, inst.Version, true));
        }
        _allAvailable = _allAvailable.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>Loads the two master lists (installed rows + the full available
    /// catalog) into memory, then renders the current view. Only real process
    /// spawns in the whole packages tab; everything after is in-memory.
    /// Reentrancy-safe: concurrent callers (e.g. user toggling a view mid-load)
    /// share the in-flight load instead of double-spawning.</summary>
    public Task InitializePackagesAsync(bool refresh = false)
    {
        if (!refresh && _initInFlight is not null) return _initInFlight;
        _initInFlight = RunInitAsync();
        return _initInFlight;
    }

    private async Task RunInitAsync()
    {
        await RunBusyAsync(async () =>
        {
            var installed = _apt.InstalledRowsAsync(refresh: true);
            var all = _apt.SearchAsync("");
            await Task.WhenAll(installed, all).ConfigureAwait(true);

            _installedRows = installed.Result;
            _installedNames = new HashSet<string>(_installedRows.Select(r => r.Name), StringComparer.Ordinal);
            _allAvailable = all.Result
                .Select(a => new PackageListItem(a.Name, a.Summary, null, _installedNames.Contains(a.Name)))
                .ToList();
            MergeLocalOnlyInstalled();
            _packagesInitialized = true;
            Raise(nameof(HasNoResults));
            await FilterCurrentAsync();
            ApplyPendingSearch(); // CLI-supplied search term, if any
        });
    }

    /// <summary>Search / immediate re-filter (Enter key or explicit action).</summary>
    public async Task DoSearchAsync()
    {
        _filterCts?.Cancel();
        if (!_packagesInitialized)
        {
            await InitializePackagesAsync();
            return;
        }
        await FilterCurrentAsync();
    }

    /// <summary>Re-renders the current view applying the search term and the
    /// apps-only filter entirely in memory — instant, no process spawns.</summary>
    public async Task FilterCurrentAsync()
    {
        if (!_packagesInitialized)
        {
            await InitializePackagesAsync();
            return;
        }

        SearchResults.Clear();
        var query = SearchTerm.Trim();

        IEnumerable<PackageListItem> source = View switch
        {
            PkgView.Installed => _installedRows.Select(r => new PackageListItem(r.Name, "", r.Version, true)),
            PkgView.Available => _allAvailable,
            _ => AllCatalog(),
        };

        if (query.Length > 0)
            source = source.Where(p =>
                p.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || p.Summary.Contains(query, StringComparison.OrdinalIgnoreCase));

        if (View != PkgView.All && AppsOnly)
            source = source.Where(p => Parsers.IsApplication(p.Name));

        var matched = source.ToList();

        // Cap what we bind so the UI stays snappy on the huge "All" catalog,
        // unless the user explicitly asked to see every result.
        const int renderCap = 1000;
        var shown = _showAll || matched.Count <= renderCap
            ? matched
            : matched.Take(renderCap).ToList();
        foreach (var item in shown)
            SearchResults.Add(item);
        _moreCount = shown.Count < matched.Count ? matched.Count : 0;

        Raise(nameof(ShownCount));
        Raise(nameof(MoreVisible));
        Raise(nameof(ShowAllLabel));
        Raise(nameof(HasNoResults));
        RebuildIdleStats();

        Status = BuildCountStatus(matched.Count, shown.Count, query);
    }

    private IEnumerable<PackageListItem> AllCatalog()
    {
        var known = new HashSet<string>(_allAvailable.Select(a => a.Name), StringComparer.Ordinal);
        foreach (var a in _allAvailable)
            yield return a;
        foreach (var r in _installedRows)
            if (!known.Contains(r.Name))
                yield return new PackageListItem(r.Name, "", r.Version, true);
    }

    private string BuildCountStatus(int matched, int shown, string query)
    {
        var kind = View switch
        {
            PkgView.Installed => "installed",
            PkgView.Available => "available",
            _ => "packages in every repository",
        };
        var q = query.Length == 0 ? "" : $" matching '{query}'";
        var filter = View != PkgView.All && AppsOnly ? " (apps only)" : "";
        var trunc = shown < matched ? $"; showing the first {shown:N0}" : "";
        return $"{matched:N0} {kind}{q}{filter}{trunc}. Type to narrow…";
    }

    private void DebounceFilter()
    {
        _filterCts?.Cancel();
        var cts = new CancellationTokenSource();
        _filterCts = cts;
        _ = DebounceFilterAsync(cts.Token);
    }

    private async Task DebounceFilterAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(280, ct);
        }
        catch (TaskCanceledException)
        {
            return;
        }
        if (ct.IsCancellationRequested) return;
        await FilterCurrentAsync();
    }

    public async Task LoadDetailsAsync(PackageListItem? item)
    {
        if (item is null) return;
        await RunBusyAsync(async () => await LoadDetailsCoreAsync(item));
    }

    /// <summary>The actual detail load: the four queries run concurrently (one
    /// spawn-round instead of five sequential), and a stale-selection guard keeps
    /// fast click-throughs from flashing wrong details.</summary>
    private async Task LoadDetailsCoreAsync(PackageListItem item)
    {
        var detailsT = _apt.DetailsAsync(item.Name);
        var depsT = _apt.DependsAsync(item.Name);
        var dependentsT = _apt.RDependentsAsync(item.Name);
        var holdsT = _apt.HoldsAsync();
        await Task.WhenAll(detailsT, depsT, dependentsT, holdsT).ConfigureAwait(true);

        if (!ReferenceEquals(Selected, item)) return; // user moved on meanwhile

        var held = holdsT.Result.Contains(item.Name, StringComparer.Ordinal);
        Details = detailsT.Result;
        IsHeld = held;
        HoldLabel = held ? "Unhold" : "Hold";

        Dependencies.Clear();
        foreach (var d in depsT.Result)
            Dependencies.Add(new DepRow(d.Kind.ToString(), d.Package, d.IsInstalled ? "✓ installed" : ""));

        RDependents.Clear();
        foreach (var r in dependentsT.Result)
            RDependents.Add(r);

        Raise(nameof(SelectedSummary));
        Raise(nameof(SelectedInstalled));
        Raise(nameof(SelectedCandidate));
        Raise(nameof(SelectedSection));
        Raise(nameof(SelectedMaintainer));

        Status = $"Details loaded for {item.Name}.";
    }

    public async Task InstallAsync(PackageListItem? item) => await PrivilegedPackageActionAsync(
        item, "install", () => _apt.InstallAsync(item!.Name));

    public async Task ReinstallAsync(PackageListItem? item) => await PrivilegedPackageActionAsync(
        item, "reinstall", () => _apt.ReinstallAsync(item!.Name));

    public async Task RemoveAsync(PackageListItem? item) => await PrivilegedPackageActionAsync(
        item, "remove", () => _apt.RemoveAsync(item!.Name));

    /// <summary>Installs one or more locally-downloaded .deb files (upstream
    /// LibreOffice bundles are tarballs holding many .debs).</summary>
    public async Task InstallDebsAsync(IReadOnlyList<string> debPaths, string? cleanupDir = null)
    {
        if (debPaths.Count == 0)
        {
            Status = cleanupDir is not null ? "That archive contains no .deb packages." : "Nothing to install.";
            return;
        }
        try
        {
            await RunBusyAsync(async () =>
            {
                var display = debPaths.Count == 1
                    ? Path.GetFileName(debPaths[0])
                    : $"{debPaths.Count} .deb packages from an archive";

                // Dry-run first: check dependencies without changing anything
                var dryRun = await _apt.InstallDebsDryRunAsync(debPaths);
                if (!dryRun.Success)
                {
                    var errorLines = ExtractAptErrors(dryRun.Output);
                    Status = $"Can't install — unmet dependencies. Check the log.";
                    AppendLog($"apt-get install -s {display}", dryRun);
                    return;
                }

                var result = await _apt.InstallDebsAsync(debPaths);
                AppendLog($"apt-get install {display}", result);
                if (!result.Success)
                {
                    Status = $"install failed — see log (exit {result.ExitCode}).";
                    return;
                }
                Status = $"Installed: {display}.";
                await InitializePackagesAsync(refresh: true); // full rebuild so the new packages appear in views
            });
        }
        finally
        {
            if (cleanupDir is not null)
            {
                try { Directory.Delete(cleanupDir, recursive: true); } catch { /* best effort */ }
            }
        }
    }

    /// <summary>Handles LibreOffice-style upstream archives (.tar.gz containing
    /// many .debs): extracts locally, installs every .deb inside, cleans up.</summary>
    public async Task InstallDebArchiveAsync(string archivePath)
    {
        var destDir = Path.Combine(Path.GetTempPath(), "aptbuddy-deb-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(destDir);
            
            // Use system tar for extraction to support .gz, .bz2, and .xz transparently
            var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "tar",
                Arguments = $"-xf \"{archivePath}\" -C \"{destDir}\"",
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new Exception($"Tar extraction failed with exit code {process.ExitCode}");

            var debs = Directory.EnumerateFiles(destDir, "*.deb", SearchOption.AllDirectories)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();
            if (debs.Count == 0)
            {
                Status = "That archive contains no .deb packages.";
                return;
            }
            Status = $"Found {debs.Count} .deb packages in the archive — installing (this can take a few minutes)…";
            await InstallDebsAsync(debs, destDir);
        }
        catch (Exception ex)
        {
            Status = ex is UnauthorizedAccessException or IOException or InvalidDataException
                ? $"Could not read/extract the archive: {ex.Message}"
                : $"Install failed: {ex.Message}";
        }
        finally
        {
            try { Directory.Delete(destDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Installs a binary archive (e.g. Betterbird, VS Code): extracts to
    /// /opt/&lt;folder&gt;, finds the executable, creates a .desktop launcher in
    /// /usr/share/applications/ via pkexec.</summary>
    public async Task InstallBinaryArchiveAsync(string archivePath)
    {
        var archiveName = Path.GetFileName(archivePath);
        var tempDir = Path.Combine(Path.GetTempPath(), "aptbuddy-bin-" + Guid.NewGuid().ToString("N"));
        try
        {
            Status = $"Extracting {archiveName}…";
            Directory.CreateDirectory(tempDir);

            var tar = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "tar",
                Arguments = $"-xf \"{archivePath}\" -C \"{tempDir}\"",
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            await tar.WaitForExitAsync();
            if (tar.ExitCode != 0)
            {
                var err = await tar.StandardError.ReadToEndAsync();
                Status = $"Extraction failed: {err.Trim()}";
                return;
            }

            // The archive root is the first directory inside the tarball
            var entries = Directory.GetFileSystemEntries(tempDir);
            if (entries.Length == 0)
            {
                Status = "Archive is empty.";
                return;
            }
            var rootEntry = entries.First(e => Directory.Exists(e) || File.Exists(e));
            var rootFolder = Path.GetFileName(rootEntry);

            // Resolve the target path: /opt/<root-folder-name>
            var installRoot = $"/opt/{rootFolder}";

            // Find an executable with the same base name as the root folder
            // (Betterbird → betterbird/betterbird, VS Code → code/code)
            var execName = Path.GetFileName(rootFolder);
            var execPath = FindExecutable(Path.Combine(tempDir, rootFolder), execName);

            if (execPath is null)
            {
                // Last resort: first executable found anywhere
                execPath = Directory.EnumerateFiles(tempDir, "*", SearchOption.AllDirectories)
                    .FirstOrDefault(f => !f.EndsWith(".so", StringComparison.OrdinalIgnoreCase)
                                      && !f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                                      && !f.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)
                                      && !f.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                                      && !f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                                      && File.Exists(f)
                                      && new FileInfo(f).Length > 1024
                                      && IsElf(f));
                if (execPath is null)
                {
                    Status = $"No executable found inside the archive. The app may need manual setup.";
                    return;
                }
            }

            var execRelative = Path.GetFileName(execPath);
            var desktopName = $"{execName}.desktop";
            var iconPath = FindIcon(Path.GetDirectoryName(execPath)!, execName);
            var userDesktopDest = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "applications", desktopName);
            var tmpArchive = Path.Combine(Path.GetTempPath(), "aptbuddy-bin-archive-" + Guid.NewGuid().ToString("N") + Path.GetExtension(archivePath));
            var pkexecScript = Path.Combine(Path.GetTempPath(), "aptbuddy-pkexec-" + Guid.NewGuid().ToString("N") + ".sh");
            try
            {
                File.Copy(archivePath, tmpArchive, overwrite: true);

                Status = $"Installing to {installRoot}…";

                // Desktop content — written by unprivileged C# to user's local dir
                var desktopContent = $"[Desktop Entry]\n" +
                    $"Type=Application\n" +
                    $"Name={execName}\n" +
                    $"Comment={execName} installed by AptBuddy\n" +
                    $"Exec={installRoot}/{execRelative}\n" +
                    (iconPath is not null ? $"Icon={installRoot}/{iconPath}\n" : "") +
                    $"Terminal=false\n" +
                    $"Categories=Network;Utility;\n" +
                    $"StartupNotify=true\n";

                // pkexec: extract to /opt only (no shell script complexity)
                pkexecScript = Path.Combine(Path.GetTempPath(), "aptbuddy-pkexec-" + Guid.NewGuid().ToString("N") + ".sh");
                await File.WriteAllTextAsync(pkexecScript, $"#!/bin/sh\nmkdir -p {installRoot}\ntar -xf \"{tmpArchive}\" -C /opt\nchmod +x {installRoot}/{execRelative}\n");
                var scriptProc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "pkexec",
                    Arguments = $"bash \"{pkexecScript}\"",
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                await scriptProc.WaitForExitAsync();
                if (scriptProc.ExitCode != 0)
                {
                    var err = await scriptProc.StandardError.ReadToEndAsync();
                    Status = $"Install failed (exit {scriptProc.ExitCode}): {err.Trim()}";
                    return;
                }

                // Write .desktop to user-local dir (no elevation needed)
                await File.WriteAllTextAsync(userDesktopDest, desktopContent);
                try { new FileInfo(userDesktopDest).IsReadOnly = false; } catch { /* best effort */ }
                Status = $"Installed: {execName}. Find it in the app menu (log out/in if missing).";
            }
            finally
            {
                try { File.Delete(tmpArchive); } catch { /* best effort */ }
                try { File.Delete(pkexecScript); } catch { /* best effort */ }
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            Status = $"Permission denied: {ex.Message}";
        }
        catch (Exception ex)
        {
            Status = $"Install failed: {ex.Message}";
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static string? FindExecutable(string dir, string execName)
    {
        var candidates = new[]
        {
            Path.Combine(dir, execName),
            Path.Combine(dir, execName.ToLowerInvariant()),
            Path.Combine(dir, "bin", execName),
            Path.Combine(dir, "bin", execName.ToLowerInvariant()),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindIcon(string dir, string execName)
    {
        var iconDir = Path.Combine(dir, "share", "pixmaps");
        if (!Directory.Exists(iconDir)) return null;
        return Directory.EnumerateFiles(iconDir)
            .Where(f => !f.EndsWith(".xpm")) // prefer png/svg
            .OrderByDescending(f => new FileInfo(f).Length)
            .Select(f => f.Substring(dir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .FirstOrDefault();
    }

    private static bool IsElf(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            var header = new byte[4];
            return fs.Read(header, 0, 4) == 4 && header[0] == 0x7f && header[1] == 'E' && header[2] == 'L' && header[3] == 'F';
        }
        catch { return false; }
    }

    public async Task ToggleHoldAsync(PackageListItem? item)
    {
        if (item is null) return;
        await RunBusyAsync(async () =>
        {
            var cmd = IsHeld ? await _apt.UnholdAsync(item.Name) : await _apt.HoldAsync(item.Name);
            AppendLog($"apt-mark {(IsHeld ? "unhold" : "hold")} {item.Name}", cmd);
            if (cmd.Success) Status = $"Changed hold state for {item.Name}.";
            IsHeld = !cmd.Success ? IsHeld : !IsHeld;
            HoldLabel = IsHeld ? "Unhold" : "Hold";
        });
    }

    private async Task PrivilegedPackageActionAsync(
        PackageListItem? item, string action, Func<Task<CommandResult>> run)
    {
        if (item is null) return;
        await RunBusyAsync(async () =>
        {
            var result = await run();
            AppendLog($"apt - {action} {item.Name}", result);
            if (!result.Success)
            {
                Status = $"{action} failed — see log (exit {result.ExitCode}).";
                return;
            }
            Status = $"{action} finished for {item.Name}.";
            await InitializePackagesAsync(refresh: true); // rebuild master lists + markers post-change
            await LoadDetailsCoreAsync(item);
        });
    }

    // ---- updates tab -------------------------------------------------------

    public async Task RefreshAsync()
    {
        await RunBusyAsync(async () =>
        {
            var result = await _apt.RefreshAsync();
            AppendLog("apt-get update", result);
            Status = result.Success ? "Package lists refreshed." : "apt update failed — see log.";
            await LoadUpdatableAsync();
            await LoadRepositoriesAsync();
            await InitializePackagesAsync(refresh: true); // fresh catalog after apt update
        });
    }

    public async Task LoadUpdatableAsync()
    {
        await RunBusyAsync(async () =>
        {
            Upgradable.Clear();
            var rows = await _apt.UpgradableAsync();
            foreach (var r in rows)
                Upgradable.Add(r);
            UpdatesSummary = rows.Count == 0
                ? "Everything is up to date."
                : $"{rows.Count} package(s) can be upgraded.";
            RebuildIdleStats();
        });
    }

    public async Task UpgradeAllAsync()
    {
        await RunBusyAsync(async () =>
        {
            var result = await _apt.UpgradeAllAsync();
            AppendLog("apt-get upgrade -y", result);
            Status = result.Success ? "Upgrade finished. Run upgrades again to confirm." : "Upgrade failed — see log.";
            await LoadUpdatableAsync();
        });
    }

    // ---- repositories tab --------------------------------------------------

    public async Task LoadRepositoriesAsync()
    {
        var sources = await _apt.RepositoriesAsync();
        Repositories.Clear();
        foreach (var s in sources)
            Repositories.Add(new RepoRow(
                s.Enabled ? "enabled" : "disabled",
                s.Uri ?? "(no URI)",
                s.Suite ?? "-",
                s.Component ?? "-",
                s.SignedBy ?? "none",
                s.File,
                s.Format));
        Status = $"Reading repositories: {sources.Count} source(s) configured.";
    }

    // ---- cleanup tab -------------------------------------------------------

    public async Task LoadCleanupAsync()
    {
        await RunBusyAsync(async () =>
        {
            var preview = await _apt.AutoremovePreviewAsync();
            AutoremoveList.Clear();
            foreach (var p in preview.Packages)
                AutoremoveList.Add(p);
            CleanupSummary = preview.Packages.Count == 0
                ? "No removable packages found."
                : $"{preview.Packages.Count} package(s) can be removed safely ({preview.Summary}).";

            Holds.Clear();
            var holds = await _apt.HoldsAsync();
            foreach (var h in holds)
                Holds.Add(h);
            RebuildIdleStats();
        });
    }

    public async Task RemoveAutoremoveAsync()
    {
        await RunBusyAsync(async () =>
        {
            var result = await _apt.AutoremoveAsync();
            AppendLog("apt-get autoremove -y", result);
            Status = result.Success ? "Autoremove finished." : "Autoremove failed — see log.";
            await LoadCleanupAsync();
            await InitializePackagesAsync(refresh: true); // autoremove changed the installed set
        });
    }

    // ---- shared ------------------------------------------------------------

    public void ClearLog() => OutputLog = "";

    public void AppendLog(string what, CommandResult result)
    {
        var stamp = DateTime.Now.ToString("HH:mm:ss");
        var output = SimplifyAptOutput(result.Output);
        OutputLog = string.IsNullOrEmpty(OutputLog)
            ? $"[{stamp}] $ {what}\n{output}"
            : $"{OutputLog}\n\n[{stamp}] $ {what}\n{output}";
    }

    // Trim apt verbosity — keep errors and the info that matters
    private static string SimplifyAptOutput(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "(no output)";
        var lines = raw.Split('\n');
        var important = new List<string>();
        bool skip = false;
        foreach (var line in lines)
        {
            // Start of a meaningful error block
            if (line.StartsWith("E:"))
            {
                important.Add(line);
                skip = false;
                continue;
            }
            if (line.StartsWith("The following packages have unmet dependencies:"))
            {
                important.Add(line);
                skip = false;
                continue;
            }
            if (line.StartsWith("The following packages have NOT been:"))
            {
                important.Add(line);
                skip = false;
                continue;
            }
            if (line.StartsWith("Some packages could not be installed"))
            {
                important.Add(line);
                skip = false;
                continue;
            }
            if (line.StartsWith("Unable to satisfy dependencies"))
            {
                important.Add(line);
                skip = false;
                continue;
            }
            if (line.StartsWith("Reached two conflicting assignments"))
            {
                important.Add(line);
                skip = false;
                continue;
            }
            // Skip routine apt chatter
            if (line.StartsWith("Reading ") ||
                line.StartsWith("Building ") ||
                line.StartsWith("Solving ") ||
                line.StartsWith("NOTE: ") ||
                line.StartsWith("Selecting ") ||
                line.StartsWith("Preparing ") ||
                line.StartsWith("Extracting ") ||
                line.StartsWith("Setting ") ||
                line.StartsWith("Processing ") ||
                line.StartsWith("( ") ||
                line.TrimStart().StartsWith("0 upgraded") ||
                line.TrimStart().StartsWith("0 newly") ||
                line.TrimStart().StartsWith("0 not ") ||
                line.TrimStart().StartsWith("0 packages") ||
                line.StartsWith("Fetched ") ||
                line.StartsWith("Done.") ||
                line.StartsWith("Dependency errors") ||
                line.StartsWith("You might want to run ") ||
                line.Trim() == "")
            {
                if (important.Count > 0 && !skip) skip = true;
                continue;
            }
            skip = false;
            if (important.Count > 0 || line.StartsWith("E:") || line.StartsWith("  ") || line.StartsWith("- "))
                important.Add(line);
        }
        if (important.Count == 0) return raw.TrimEnd();
        // Deduplicate consecutive identical lines
        var seen = new HashSet<string>();
        var deduped = important.Where(l =>
        {
            if (l.StartsWith("  "))
            {
                var norm = l.Trim();
                if (seen.Contains(norm)) return false;
                seen.Add(norm);
            }
            return true;
        }).ToList();
        var result = string.Join("\n", deduped);
        // Truncate if still huge
        if (result.Length > 600) result = result[..600] + "\n…(truncated)";
        return result;
    }

    // Extract the core error message from apt output for inline display
    private static string ExtractAptErrors(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "(unknown error)";
        var lines = raw.Split('\n');
        var errors = new List<string>();
        foreach (var line in lines)
        {
            if (line.StartsWith("E:")) { errors.Add(line); break; }
            if (line.Contains("has unmet dependencies")) { errors.Add(line); }
            if (line.Contains("but it is not installable")) errors.Add(line.Trim());
            if (line.Contains("Depends:") && !line.StartsWith("The following")) errors.Add(line.Trim());
        }
        // Fallback: if nothing matched, return the first E: line or first few lines
        if (errors.Count == 0)
        {
            var firstE = lines.FirstOrDefault(l => l.StartsWith("E:"));
            if (firstE is not null) return firstE;
            return lines.Length > 0 ? lines[0].Trim() : "(unknown error)";
        }
        return string.Join(" ", errors.Take(3));
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        _busyCount++;
        Raise(nameof(IsBusy));
        Raise(nameof(CanAct));
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Status = $"Error: {ex.Message}";
            OutputLog = $"{OutputLog}\n\n[{DateTime.Now:HH:mm:ss}] ERROR: {ex}";
        }
        finally
        {
            _busyCount--;
            Raise(nameof(IsBusy));
            Raise(nameof(CanAct));
        }
    }
}