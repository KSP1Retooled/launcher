namespace KSP.Retooled.Launcher.Common.Exceptions;

/// <summary>
/// A failure to show the user as is.
/// </summary>
public sealed class LauncherException(string message, Exception? inner = null) : Exception(message, inner);
