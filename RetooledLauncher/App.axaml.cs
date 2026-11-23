using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace KSP.Retooled.Launcher.App;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (this.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            MainWindow window = new();
            MainViewModel model = new(LauncherSettings.Load(), window);
            window.DataContext =  model;
            desktop.MainWindow =  window;
            window.Opened      += async (_, _) => await model.StartAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
