using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace OakSoul.Review;

public partial class App : Application
{
    /// <summary>The command line, set by <see cref="Program.Main"/> before the app starts (defaults in tests).</summary>
    public static ToolOptions Options { get; set; } = new();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            DockIcon.Set("avares://OakSoul.Review/Assets/icon.png");
            desktop.MainWindow = new MainWindow(Options);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
