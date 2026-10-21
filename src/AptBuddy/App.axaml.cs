using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace AptBuddy;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Positional CLI args become the initial search term: `aptbuddy nmap`
            desktop.MainWindow = desktop.Args is { Length: > 0 } args
                ? new MainWindow(string.Join(" ", args))
                : new MainWindow();
        }
        base.OnFrameworkInitializationCompleted();
    }
}