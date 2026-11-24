using System.Reflection;
using System.Runtime.InteropServices;

namespace KSP.Retooled.Launcher.Utils;

public static class LauncherInfo
{
    /// <summary>
    /// Where releases come from until the release repository is settled; overridable in settings.
    /// </summary>
    public const string DEFAULT_REPOSITORY = "KSP1Retooled/retooled";

    /// <summary>
    /// Marks a channel that offers its repository's debug patches instead of the regular ones.
    /// </summary>
    public const string DEBUG_SUFFIX = "#debug";

    /// <summary>
    /// The default repository's releases with their debug patches; always offered.
    /// </summary>
    public const string DEBUG_CHANNEL = DEFAULT_REPOSITORY + DEBUG_SUFFIX;

    /// <summary>
    /// Builds of pull requests for players to try, one release per pull request (<c>pr-&lt;number&gt;</c>); always
    /// offered.
    /// A release's patches are replaced in place when its pull request is built again.
    /// </summary>
    public const string EXPERIMENTAL_CHANNEL = "KSP1Retooled/experimental";

    /// <summary>
    /// The GitCode (gitcode.com) copy of <see cref="DEFAULT_REPOSITORY"/>, for when GitHub cannot be reached.
    /// </summary>
    public const string DEFAULT_MIRROR = "ksp1retooled/retooled";

    /// <summary>
    /// Where the launcher itself is released.
    /// </summary>
    public const string REPOSITORY = "KSP1Retooled/launcher";

    /// <summary>
    /// The channel of a patch installed from a local file; never a repository, which holds a '/'.
    /// </summary>
    public const string LOCAL_CHANNEL = "local";

    public static string Version { get; } = (Assembly.GetEntryAssembly() ?? typeof(LauncherInfo).Assembly)
                                           .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                                          ?.InformationalVersion.Split('+')[0]
                                         ?? "0.0.0";

    /// <summary>
    /// <c>windows</c>, <c>linux</c> or <c>macos</c>: the patch this machine needs.
    /// </summary>
    public static string Platform { get; } =
        OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsMacOS() ? "macos"
        : OperatingSystem.IsLinux() ? "linux"
                                      : RuntimeInformation.OSDescription;

    /// <summary>
    /// Per-user data: <c>%LOCALAPPDATA%</c>, <c>~/Library/Application Support</c> or <c>~/.local/share</c>.
    /// </summary>
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
                                               Environment.SpecialFolderOption.Create),
                     "KSPRetooled",
                     "Launcher");

    public static bool IsBuiltInChannel(string channel) => channel.Equals(DEFAULT_REPOSITORY, StringComparison.OrdinalIgnoreCase)
                                                        || channel.Equals(DEBUG_CHANNEL, StringComparison.OrdinalIgnoreCase)
                                                        || channel.Equals(EXPERIMENTAL_CHANNEL, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the channel's releases are versions, each replacing the ones before it, rather than separate builds.
    /// </summary>
    public static bool IsVersionedChannel(string channel) => !RepositoryOf(channel).Equals(EXPERIMENTAL_CHANNEL, StringComparison.OrdinalIgnoreCase);

    public static bool IsDebugChannel(string channel) => channel.EndsWith(DEBUG_SUFFIX, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The GitHub repository behind a channel.
    /// </summary>
    public static string RepositoryOf(string channel) => IsDebugChannel(channel) ? channel[..^DEBUG_SUFFIX.Length] : channel;

    /// <summary>
    /// The GitCode repository that mirrors a GitHub one, if any.
    /// </summary>
    public static string? MirrorOf(string repository) => repository.Equals(DEFAULT_REPOSITORY, StringComparison.OrdinalIgnoreCase)
                                                             ? DEFAULT_MIRROR
                                                             : null;
}
