using System.Text.RegularExpressions;

namespace KSP.Retooled.Patch;

/// <summary>
/// The settings.cfg the game writes in an install's root.
/// </summary>
internal static partial class GameSettingsFile
{
    public const string FILE_NAME = "settings.cfg";

    [GeneratedRegex(@"^([ \t]*LANGUAGE[ \t]*=)[^\r\n]*", RegexOptions.Multiline)]
    private static partial Regex LanguageLine();

    /// <summary>
    /// Returns the contents of settings.cfg with LANGUAGE updated, or null if the file is missing from
    /// <paramref name="install"/>. The caller must replace settings.cfg with a new file to protect any other
    /// install that shares the original file.
    /// </summary>
    public static string? WithLanguage(string install, string language)
    {
        string path = Path.Combine(install, FILE_NAME);
        if (!File.Exists(path)) return null;

        string text = File.ReadAllText(path);
        // stock writes LANGUAGE = en-us whatever the install's language, and never reads it
        return LanguageLine().IsMatch(text)
                   ? LanguageLine().Replace(text, m => $"{m.Groups[1].Value} {language}", 1)
                   : $"LANGUAGE = {language}{Environment.NewLine}{text}";
    }
}
