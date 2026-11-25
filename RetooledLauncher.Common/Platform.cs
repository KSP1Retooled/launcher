using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using FastEnumUtility;
using JetBrains.Annotations;
using KSP.Retooled.Launcher.Common.Converters;
using KSP.Retooled.Launcher.Common.Exceptions;

namespace KSP.Retooled.Launcher.Common;

/// <summary>
/// Launcher target platform
/// </summary>
[PublicAPI, JsonConverter(typeof(PlatformJsonConverter))]
public enum Platform
{
    /// <summary>
    /// Unknown/undefined platform
    /// </summary>
    [Label("Unknown")]
    UNKNOWN,
    /// <summary>
    /// Windows platform
    /// </summary>
    [EnumMember(Value = "windows")]
    [Label("Windows"), Label("KSP_x64.exe", 1)]
    WINDOWS,
    /// <summary>
    /// Linux platform
    /// </summary>
    [EnumMember(Value = "linux")]
    [Label("Linux"), Label("KSP.x86_64", 1)]
    LINUX,
    /// <summary>
    /// MacOS platform
    /// </summary>
    [EnumMember(Value = "macos")]
    [Label("macOS"), Label("KSP.app", 1)]
    MACOS
}

/// <summary>
/// Platform enum utils
/// </summary>
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
            CurrentPlatform = Platform.UNKNOWN;
        }
    }

    /// <param name="platform">Platform value</param>
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
        public string Name => platform.GetEnumMemberValue(throwIfNotFound: false) ?? RuntimeInformation.OSDescription;

        /// <summary>
        /// Get the current platform being used
        /// </summary>
        public string DisplayName => platform.GetLabel(throwIfNotFound: false) ?? platform.FastToString();

        /// <summary>
        /// Gets the executable name for the given platform
        /// </summary>
        /// <exception cref="LauncherException">If the platform isn't supported</exception>
        public string PlayerName => platform.GetLabel(1, throwIfNotFound: false)
                                 ?? throw new LauncherException($"Unsupported platform: {platform.FastToString()}");
    }
}
