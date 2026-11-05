using Avalonia;
using Velopack;

namespace KSP.Retooled.Launcher.App;

static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Has to come first: the installer starts the app with hook arguments, and the process exits inside Run().
        // Elsewhere, replacing the app can need an admin prompt, which should not appear unasked at every start.
        VelopackApp.Build().SetAutoApplyOnStartup(OperatingSystem.IsWindows()).Run();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // also used by the visual designer
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
