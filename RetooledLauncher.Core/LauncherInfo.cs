using System.Reflection;
using System.Runtime.InteropServices;

namespace KSP.Retooled.Launcher;

public static class LauncherInfo
{
    /// <summary>Where releases come from until the release repository is settled; overridable in settings.</summary>
    public const string DefaultRepository = "KSP1Retooled/retooled";

    /// <summary>Marks a channel that offers its repository's debug patches instead of the regular ones.</summary>
    public const string DebugSuffix = "#debug";

    /// <summary>The default repository's releases with their debug patches; always offered.</summary>
    public const string DebugChannel = DefaultRepository + DebugSuffix;

    public static bool IsBuiltInChannel(string channel) =>
        channel.Equals(DefaultRepository, StringComparison.OrdinalIgnoreCase)
        || channel.Equals(DebugChannel, StringComparison.OrdinalIgnoreCase);

    public static bool IsDebugChannel(string channel) =>
        channel.EndsWith(DebugSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The GitHub repository behind a channel.</summary>
    public static string RepositoryOf(string channel) =>
        IsDebugChannel(channel) ? channel[..^DebugSuffix.Length] : channel;

    /// <summary>Where the launcher itself is released.</summary>
    public const string Repository = "KSP1Retooled/launcher";

    /// <summary>Whether the launcher gets new versions without the user's help; elsewhere it tells them about one.</summary>
    public static bool UpdatesItself => OperatingSystem.IsWindows();

    /// <summary>The channel of a patch installed from a local file; never a repository, which holds a '/'.</summary>
    public const string LocalChannel = "local";

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
