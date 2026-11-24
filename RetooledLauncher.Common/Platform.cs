using System.Collections.Immutable;
using System.Runtime.InteropServices;
using FastEnumUtility;
using JetBrains.Annotations;

namespace RetooledLauncher.Common;

/// <summary>
/// Launcher target platform
/// </summary>
[PublicAPI]
public enum Platform
{
    UNSUPPORTED,
    [Label("windows")]
    [Label("Windows", 1)]
    WINDOWS,
    [Label("linux")]
    [Label("Linux", 1)]
    LINUX,
    [Label("macos")]
    [Label("macOS", 1)]
    MACOS
}

[PublicAPI, FastEnum<Platform>]
public partial class PlatformBooster;

[PublicAPI]
public static class PlatformUtils
{
    private static readonly Platform CurrentPlatform;
    private static readonly ImmutableArray<Platform> AllPlatforms = [Platform.WINDOWS, Platform.LINUX, Platform.MACOS];

    static PlatformUtils()
    {
        if (OperatingSystem.IsWindows())
        {
            CurrentPlatform = Platform.WINDOWS;
        }
        else if (OperatingSystem.IsLinux())
        {
            CurrentPlatform =  Platform.LINUX;
        }
        else if (OperatingSystem.IsMacOS())
        {
            CurrentPlatform =  Platform.MACOS;
        }
        else
        {
            CurrentPlatform = Platform.UNSUPPORTED;
        }
    }

    extension(Platform platform)
    {
        /// <summary>
        /// Array of all valid platforms
        /// </summary>
        public static ImmutableArray<Platform> All => AllPlatforms;

        /// <summary>
        /// Get the current platform being used
        /// </summary>
        public static Platform Current => CurrentPlatform;

        /// <summary>
        /// Get the current platform being used
        /// </summary>
        public string Name => platform switch
        {
            Platform.WINDOWS => platform.GetLabel()!,
            Platform.LINUX   => platform.GetLabel()!,
            Platform.MACOS   => platform.GetLabel()!,
            _                => RuntimeInformation.OSDescription
        };

        /// <summary>
        /// Get the current platform being used
        /// </summary>
        public string DisplayName => platform switch
        {
            Platform.WINDOWS => platform.GetLabel(1)!,
            Platform.LINUX   => platform.GetLabel(1)!,
            Platform.MACOS   => platform.GetLabel(1)!,
            _                => "Unknown"
        };
    }
}
