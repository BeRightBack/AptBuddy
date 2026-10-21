using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AptBuddy.ViewModels;

namespace AptBuddy;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow() : this(null) { }

    public MainWindow(string? initialSearch)
    {
        _vm = new MainViewModel();
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(initialSearch))
            _vm.SetInitialSearch(initialSearch);
        DataContext = _vm;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        await Task.WhenAll(
            _vm.LoadRepositoriesAsync(),
            _vm.LoadUpdatableAsync(),
            _vm.LoadCleanupAsync(),
            _vm.InitializePackagesAsync());
    }

    private async void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            await _vm.DoSearchAsync();
    }

    private async void ShowAll_Click(object? sender, RoutedEventArgs e) => await _vm.ShowAllAsync();
    private async void Refresh_Click(object? sender, RoutedEventArgs e) => await _vm.RefreshAsync();

    private static readonly string[] ArchiveExtensions = { ".deb", ".tar.gz", ".tar.xz", ".tgz", ".tar.bz2", ".tar" };

    private static bool IsArchive(string path) =>
        ArchiveExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    private async void InstallDeb_Click(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var options = new FilePickerOpenOptions
        {
            Title = "Choose packages to install (.deb, or a LibreOffice-style archive of .debs)",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Debian packages / archives")
                {
                    Patterns = new[] { "*.*" }, // broad enough to show everything; filter by extension in code
                },
            },
        };
        var files = await top.StorageProvider.OpenFilePickerAsync(options);
        if (files is not { Count: 1 }) return;
        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;
        if (IsArchive(path) && !path.EndsWith(".deb", StringComparison.OrdinalIgnoreCase))
            await _vm.InstallDebArchiveAsync(path);
        else
            await _vm.InstallDebsAsync(new[] { path });
    }

    private async void Install_Click(object? sender, RoutedEventArgs e) => await _vm.InstallAsync(_vm.Selected);
    private async void Reinstall_Click(object? sender, RoutedEventArgs e) => await _vm.ReinstallAsync(_vm.Selected);
    private async void Remove_Click(object? sender, RoutedEventArgs e) => await _vm.RemoveAsync(_vm.Selected);
    private async void ToggleHold_Click(object? sender, RoutedEventArgs e) => await _vm.ToggleHoldAsync(_vm.Selected);
    private async void UpgradeAll_Click(object? sender, RoutedEventArgs e) => await _vm.UpgradeAllAsync();
    private async void RefreshRepos_Click(object? sender, RoutedEventArgs e) => await _vm.LoadRepositoriesAsync();
    private async void CleanupCheck_Click(object? sender, RoutedEventArgs e) => await _vm.LoadCleanupAsync();
    private async void CleanupRemove_Click(object? sender, RoutedEventArgs e) => await _vm.RemoveAutoremoveAsync();
    private void ClearLog_Click(object? sender, RoutedEventArgs e) => _vm.ClearLog();

    private async void AddRepo_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new AddRepoWindow();
        await dialog.ShowDialog(this);
        await _vm.LoadRepositoriesAsync();
    }

    private async void InstallBinary_Click(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var options = new FilePickerOpenOptions
        {
            Title = "Choose a binary archive to install (e.g. Betterbird, VS Code)",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Binary archives")
                {
                    Patterns = new[] { "*.*" },
                },
            },
        };
        var files = await top.StorageProvider.OpenFilePickerAsync(options);
        if (files is not { Count: 1 }) return;
        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;
        if (IsArchive(path))
            await _vm.InstallBinaryArchiveAsync(path);
    }
}
