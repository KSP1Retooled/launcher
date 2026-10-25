using System.Reflection;
using System.Runtime.InteropServices;

namespace KSP.Retooled.Launcher;

public static class LauncherInfo
{
    /// <summary>Where releases come from until the release repository is settled; overridable in settings.</summary>
    public const string DefaultRepository = "KSP1Retooled/retooled";

    public static string Version { get; } =
        (Assembly.GetEntryAssembly() ?? typeof(LauncherInfo).Assembly)
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    /// <summary><c>windows</c>, <c>linux</c> or <c>macos</c>: the patch this machine needs.</summary>
    public static string Platform { get; } =
        OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsMacOS() ? "macos"
        : OperatingSystem.IsLinux() ? "linux"
        : RuntimeInformation.OSDescription;

    /// <summary>Per-user data: <c>%LOCALAPPDATA%</c>, <c>~/Library/Application Support</c> or <c>~/.local/share</c>.</summary>
    public static string DataDirectory { get; } =
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.Create
            ),
            "KSPRetooled",
            "Launcher"
        );
}

/// <summary>A failure to show the user as is.</summary>
public sealed class LauncherException(string message, Exception? inner = null)
    : Exception(message, inner);
