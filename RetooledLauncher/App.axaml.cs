using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace KSP.Retooled.Launcher.App;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            var model = new MainViewModel(LauncherSettings.Load(), window);
            window.DataContext = model;
            desktop.MainWindow = window;
            window.Opened += async (_, _) => await model.StartAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
