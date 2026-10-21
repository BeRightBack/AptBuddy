using System.Text.RegularExpressions;

namespace AptBuddy.Core;

/// <summary>
/// Reads configured apt sources from /etc/apt/sources.list(.d).
/// Understands both one-line `deb [opts] uri suite comp` (.list) and
/// deb822 stanza (.sources) formats.
/// </summary>
public static partial class RepoService
{
    public const string SourcesDir = "/etc/apt/sources.list.d";
    public const string MainSourcesFile = "/etc/apt/sources.list";

    public static List<RepoSource> ReadSources() => ReadSourcesFrom(MainSourcesFile, SourcesDir);

    public static List<RepoSource> ReadSourcesFrom(string mainFile, string sourcesDir)
    {
        var sources = new List<RepoSource>();
        var files = new List<string>();
        if (File.Exists(mainFile))
            files.Add(mainFile);
        if (Directory.Exists(sourcesDir))
            files.AddRange(Directory.GetFiles(sourcesDir).OrderBy(f => f, StringComparer.Ordinal));

        foreach (var file in files)
        {
            try
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext == ".list") ParseOneLine(file, sources);
                else if (ext == ".sources") ParseDeb822(file, sources);
            }
            catch
            {
                // skip unreadable/malformed files; keep the app resilient
            }
        }
        return sources;
    }

    private static void ParseOneLine(string file, List<RepoSource> sources)
    {
        foreach (var raw in File.ReadAllLines(file))
        {
            var line = StripComment(raw).Trim();
            if (line.Length == 0) continue;
            var enabled = !raw.TrimStart().StartsWith('#');

            var m = OneLinePattern().Match(line);
            if (!m.Success || m.Groups["type"].Value == "deb-src") continue;

            var opts = m.Groups["opts"].Value;
            var signedBy = SignedByPattern().Match(opts).Groups["key"].Value;

            sources.Add(new RepoSource(file, "one-line", enabled,
                m.Groups["uri"].Value, m.Groups["suite"].Value,
                m.Groups["comp"].Value.Length > 0 ? m.Groups["comp"].Value : "main",
                signedBy.Length > 0 ? signedBy : null));
        }
    }

    private static void ParseDeb822(string file, List<RepoSource> sources)
    {
        var key = "";
        var values = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var anyEnabledLine = false;

        foreach (var rawLine in File.ReadAllLines(file))
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                // flush one stanza, but deb822 sources usually have one block per file
                continue;
            }

            var trimmed = rawLine.Trim();
            if (trimmed.StartsWith('#'))
                continue;

            anyEnabledLine = true;
            if (trimmed.Contains(':'))
            {
                var idx = trimmed.IndexOf(':');
                key = trimmed[..idx].Trim();
                var value = trimmed[(idx + 1)..].Trim();
                if (value.Length > 0)
                    Add(values, key, value);
            }
            else if (key.Length > 0 && rawLine.StartsWith(' ') && trimmed.Length > 0)
            {
                // continuation line of a multi-value key
                Add(values, key, trimmed);
            }
        }

        if (!anyEnabledLine) return;

        var types = values.TryGetValue("Types", out var t) ? t : new List<string>();
        var uris = values.TryGetValue("URIs", out var u) ? u : new List<string>();
        var suites = values.TryGetValue("Suites", out var s) ? s : new List<string>();
        var components = values.TryGetValue("Components", out var c) ? c : new List<string>();
        var signedBy = values.TryGetValue("Signed-By", out var sb) && sb.Count > 0 ? sb[0] : null;

        foreach (var type in types)
        {
            if (type != "deb") continue;
            foreach (var uri in uris)
            foreach (var suite in suites)
            {
                var comp = components.Count > 0 ? string.Join(' ', components) : "main";
                sources.Add(new RepoSource(file, "deb822", true, uri, suite, comp, signedBy));
            }
        }
    }

    private static void Add(Dictionary<string, List<string>> values, string key, string value)
    {
        // a key can repeat; split whitespace-separated items
        if (!values.TryGetValue(key, out var list))
            values[key] = list = new List<string>();
        list.AddRange(value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string StripComment(string line)
    {
        var idx = line.IndexOf('#');
        return idx >= 0 ? line[..idx] : line;
    }

    [GeneratedRegex(@"^(?<type>deb(?:-src)?)(?:\s+\[(?<opts>[^\]]+)\])?\s+(?<uri>\S+)\s+(?<suite>\S+)(?:\s+(?<comp>.+))?")]
    private static partial Regex OneLinePattern();

    [GeneratedRegex(@"signed-by=(?<key>/\S+)")]
    private static partial Regex SignedByPattern();

    /// <summary>
    /// Fetches a repo key over HTTPS and returns its fingerprint text so the UI
    /// can show it for confirmation before running add-repo.sh.
    /// </summary>
    public static async Task<(bool Ok, string Output)> FetchKeyFingerprintAsync(
        string keyUrl,
        CancellationToken ct = default)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"aptbuddy-key-{Guid.NewGuid():N}");
        try
        {
            var asc = tmp + ".asc";
            var curl = await AptProcess.RunAsync("curl", new[] { "-fsSL", keyUrl, "-o", asc }, ct).ConfigureAwait(false);
            if (!curl.Success)
                return (false, curl.Output);

            var gpg = await AptProcess.RunAsync("gpg", new[] { "--dearmor", "--output", tmp, asc }, ct).ConfigureAwait(false);
            if (!gpg.Success)
                return (false, gpg.Output);

            var info = await AptProcess.RunAsync("gpg", new[] { "--show-keys", "--fingerprint", tmp }, ct).ConfigureAwait(false);
            return (info.Success, info.Output);
        }
        finally
        {
            File.Delete(tmp);
            File.Delete(tmp + ".asc");
        }
    }
}