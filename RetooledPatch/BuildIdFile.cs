namespace KSP.Retooled.Patch;

/// <summary>A KSP install's buildID64.txt or buildID.txt: <c>key = value</c> lines such as <c>build id = 03190</c>.</summary>
static class BuildIdFile
{
    public static readonly string[] Names = ["buildID64.txt", "buildID.txt"];

    /// <summary>The value of the first <paramref name="key"/> line in the file at <paramref name="path"/>, or null.</summary>
    public static string? Value(string path, string key) => File.ReadLines(path)
        .Select(l => l.Split('=', 2)).Where(p => p.Length == 2 && p[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
        .Select(p => p[1].Trim()).FirstOrDefault();

    /// <summary>The <paramref name="key"/> value of the first buildID file in <paramref name="root"/> that has one, or null.</summary>
    public static string? Find(string root, string key) => Names
        .Select(name => Path.Combine(root, name)).Where(File.Exists)
        .Select(path => Value(path, key)).FirstOrDefault(value => value != null);
}
