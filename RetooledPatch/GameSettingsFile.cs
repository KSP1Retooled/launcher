using System.Text.RegularExpressions;

namespace KSP.Retooled.Patch;

/// <summary>The settings.cfg the game writes in an install's root.</summary>
static partial class GameSettingsFile
{
    public const string FileName = "settings.cfg";

    [GeneratedRegex(@"^([ \t]*LANGUAGE[ \t]*=)[^\r\n]*", RegexOptions.Multiline)]
    private static partial Regex LanguageLine();

    /// <summary>
    /// Sets LANGUAGE in <paramref name="install"/>'s settings.cfg; false, changing nothing, if the game hasn't written
    /// one yet.
    /// </summary>
    public static bool SetLanguage(string install, string language)
    {
        string path = Path.Combine(install, FileName);
        if (!File.Exists(path)) return false;
        string text = File.ReadAllText(path);
        // stock writes LANGUAGE = en-us whatever the install's language, and never reads it
        text = LanguageLine().IsMatch(text)
            ? LanguageLine().Replace(text, m => m.Groups[1].Value + " " + language, 1)
            : "LANGUAGE = " + language + Environment.NewLine + text;
        File.WriteAllText(path, text);
        return true;
    }
}
