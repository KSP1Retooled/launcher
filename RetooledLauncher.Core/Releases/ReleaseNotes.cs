using System.Text.RegularExpressions;

namespace KSP.Retooled.Launcher.Releases;

/// <summary>Prepares <see cref="Release.Notes"/> for the launcher's markdown renderer.</summary>
public static partial class ReleaseNotes
{
    /// <summary>
    /// Writes plain URLs and <c>&lt;url&gt;</c> autolinks as <c>[url](url)</c>. GitHub links both, and its generated
    /// notes use nothing else, but the renderer only links the explicit form. Code and existing links are left alone.
    /// </summary>
    public static string LinkUrls(string notes) =>
        Markup().Replace(
            notes,
            match =>
            {
                if (match.Groups["autolink"] is { Success: true } autolink)
                    return Link(autolink.Value) ?? match.Value;
                if (match.Groups["bare"] is not { Success: true } bare)
                    return match.Value;

                var url = WithoutTrailingPunctuation(bare.Value);
                return Link(url) is { } link ? link + bare.Value[url.Length..] : match.Value;
            }
        );

    /// <summary>The link for <paramref name="url"/>, whose text must not read as emphasis.</summary>
    static string? Link(string url) =>
        Release.IsWebLink(url) ? $"[{Emphasis().Replace(url, @"\$0")}]({url})" : null;

    /// <summary>Drops what ends the sentence around a URL, keeping a <c>)</c> that closes one inside it.</summary>
    static string WithoutTrailingPunctuation(string url)
    {
        while (true)
        {
            var trimmed = url.TrimEnd('.', ',', ':', ';', '!', '?', '\'', '"', '*', '_', '~');
            if (trimmed.EndsWith(')') && trimmed.Count(c => c == ')') > trimmed.Count(c => c == '('))
                trimmed = trimmed[..^1];
            if (trimmed.Length == url.Length)
                return url;
            url = trimmed;
        }
    }

    [GeneratedRegex(@"[*_~]")]
    private static partial Regex Emphasis();

    /// <summary>Everything a URL can appear in, earliest first, so that code and links match before the URLs in them.</summary>
    [GeneratedRegex(
        """
        ^[ ]{0,3}(?<fence>`{3,}|~{3,})[\s\S]*?(?:^[ ]{0,3}\k<fence>|\z)  # fenced code
        | ^(?:[ ]{4}|\t).*$                                              # indented code
        | ^[ ]{0,3}\[[^\]\n]+\]:.*$                                      # link reference definition
        | (?<!`)(?<ticks>`+)(?!`).+?(?<!`)\k<ticks>(?!`)                 # code span
        | !?\[(?:[^\[\]\n]|\[[^\]\n]*\](?:\([^)\n]*\))?)*\]\([^\n]*?\)   # link or image, perhaps of an image
        | <(?<autolink>https?://[^\s<>]+)>
        | (?<![\w/:.@+-])(?<bare>https?://[^\s<>\[\]`]+)
        """,
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace
    )]
    private static partial Regex Markup();
}
