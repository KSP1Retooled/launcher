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
        var velopack = VelopackApp.Build().SetAutoApplyOnStartup(OperatingSystem.IsWindows());
        // only Windows has an uninstaller to run hooks
        if (OperatingSystem.IsWindows())
            velopack.OnBeforeUninstallFastCallback(_ => RemoveData());
        velopack.Run();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // also used by the visual designer
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();

    // Velopack removes only its own folder, so the settings and patch cache would outlive the launcher. Patched games
    // keep their stock files in <ksp>/.retooled, so they don't need the cache to be restored. A file that can't be
    // deleted must not stop the uninstall.
    static void RemoveData()
    {
        try
        {
            if (Directory.Exists(LauncherInfo.DataDirectory))
                Directory.Delete(LauncherInfo.DataDirectory, recursive: true);

            var parent = Path.GetDirectoryName(LauncherInfo.DataDirectory);
            if (parent is not null && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                Directory.Delete(parent);
        }
        catch (Exception) { }
    }
}
