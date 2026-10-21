using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Interactivity;
using AptBuddy.Core;

namespace AptBuddy;

public partial class AddRepoWindow : Window
{
    private readonly AptService _apt = new();
    private bool _keyVerified;

    public AddRepoWindow()
    {
        InitializeComponent();
        ScriptPath.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents", "Default Project", "add-repo.sh");
    }

    private async void FetchKey_Click(object? sender, RoutedEventArgs e)
    {
        var url = KeyUrl.Text?.Trim() ?? "";
        if (!url.StartsWith("https://", StringComparison.Ordinal))
        {
            ResultText.Text = "Key URL must be https://";
            return;
        }

        _keyVerified = false;
        AddButton.IsEnabled = false;
        FingerprintBox.Text = "Fetching key…";

        var (ok, output) = await RepoService.FetchKeyFingerprintAsync(url);
        _keyVerified = ok;
        FingerprintBox.Text = ok
            ? $"Fingerprint of {url}:\n\n{output}\n\nIf this matches the vendor's published fingerprint, click “Add repository”."
            : $"Failed to fetch or verify the key:\n{output}";
        AddButton.IsEnabled = ok;
    }

    private async void Add_Click(object? sender, RoutedEventArgs e)
    {
        var name = RepoName.Text?.Trim() ?? "";
        var keyUrl = KeyUrl.Text?.Trim() ?? "";
        var suite = Suite.Text?.Trim() ?? "";
        var component = Component.Text?.Trim() ?? "";
        var repoUrl = RepoUrl.Text?.Trim() ?? "";
        var arch = Arch.Text?.Trim() ?? "";
        var script = ScriptPath.Text?.Trim() ?? "";

        if (name.Length == 0 || keyUrl.Length == 0 || suite.Length == 0 || component.Length == 0 || repoUrl.Length == 0)
        {
            ResultText.Text = "Fill in all required fields first.";
            return;
        }
        if (!Regex.IsMatch(name, "^[a-z0-9][a-z0-9-]*$"))
        {
            ResultText.Text = "Repo name must match [a-z0-9][a-z0-9-]* (lowercase letters, digits, dashes).";
            return;
        }
        if (!_keyVerified)
        {
            ResultText.Text = "Fetch and verify the key fingerprint first.";
            return;
        }
        if (!File.Exists(script))
        {
            ResultText.Text = $"add-repo.sh not found at: {script}";
            return;
        }

        var args = new List<string> { "--yes" };
        if (FmtDeb822.IsChecked == true) args.Add("--deb822");
        if (arch.Length > 0) { args.Add("--arch"); args.Add(arch); }
        args.AddRange(new[] { name, keyUrl, suite, component, repoUrl });

        AddButton.IsEnabled = false;
        ResultText.Text = "Running add-repo.sh via pkexec… a password prompt may appear.";

        var result = await _apt.AddRepoAsync(script, args);
        ResultText.Text = result.Success
            ? $"Repository added.\n\n{result.Output}"
            : $"Failed (exit {result.ExitCode}).\n\n{result.Output}";
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}