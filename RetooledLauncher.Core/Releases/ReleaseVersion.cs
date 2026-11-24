namespace KSP.Retooled.Launcher.Releases;

/// <summary>
/// A semantic version read from a release tag (<c>v1.2.3</c>, <c>1.2</c>, <c>v0.4.0-beta.2+build</c>). Ordering
/// follows
/// semver 2.0: a pre-release sorts before its release, identifiers compare numerically when both are numbers, and
/// build metadata is ignored.
/// </summary>
public sealed class ReleaseVersion : IComparable<ReleaseVersion>
{
    private ReleaseVersion(int major, int minor, int patch, string[] prerelease)
    {
        this.Major         = major;
        this.Minor         = minor;
        this.Patch         = patch;
        this.PrereleaseIds = prerelease;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    public IReadOnlyList<string> PrereleaseIds { get; }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        int c = this.Major.CompareTo(other.Major);
        if (c is 0)
        {
            c = this.Minor.CompareTo(other.Minor);
        }

        if (c is 0)
        {
            c = this.Patch.CompareTo(other.Patch);
        }

        if (c is not 0)
        {
            return c;
        }

        // no pre-release identifiers outranks any
        if (this.PrereleaseIds.Count is 0 || other.PrereleaseIds.Count is 0)
        {
            return this.PrereleaseIds.Count == other.PrereleaseIds.Count ? 0
                   : this.PrereleaseIds.Count is 0                       ? 1
                                                                           : -1;
        }

        for (int i = 0; i < Math.Min(this.PrereleaseIds.Count, other.PrereleaseIds.Count); i++)
        {
            string a = this.PrereleaseIds[i],
                   b = other.PrereleaseIds[i];

            bool aNumber = a.All(char.IsAsciiDigit),
                 bNumber = b.All(char.IsAsciiDigit);

            c =
                aNumber && bNumber ? CompareNumeric(a, b)
                : aNumber          ? -1
                : bNumber          ? 1
                                     : string.CompareOrdinal(a, b);

            if (c is not 0)
            {
                return Math.Sign(c);
            }
        }

        return this.PrereleaseIds.Count.CompareTo(other.PrereleaseIds.Count);
    }

    public static bool TryParse(string? tag, out ReleaseVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        string s = tag.Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
        {
            s = s[1..];
        }

        int plus = s.IndexOf('+');
        if (plus >= 0)
        {
            s = s[..plus];
        }

        int dash = s.IndexOf('-');
        string core = dash >= 0 ? s[..dash] : s;
        string[] prerelease = dash >= 0 ? s[(dash + 1)..].Split('.') : [];
        if (prerelease.Any(p => p.Length is 0))
        {
            return false;
        }

        string[] parts = core.Split('.');
        if (parts.Length is < 2 or > 3)
        {
            return false;
        }

        int[] numbers = new int[3];
        if (parts.Index().Any(p => p.Item.Length is 0
                                || !p.Item.All(char.IsAsciiDigit)
                                || !int.TryParse(p.Item, out numbers[p.Index])))
        {
            return false;
        }

        version = new ReleaseVersion(numbers[0], numbers[1], numbers[2], prerelease);
        return true;
    }

    private static int CompareNumeric(string a, string b)
    {
        a = a.TrimStart('0');
        b = b.TrimStart('0');
        return a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
    }

    public override string ToString() => $"{this.Major}.{this.Minor}.{this.Patch}{(this.PrereleaseIds.Count > 0 ? "-" + string.Join('.', this.PrereleaseIds) : string.Empty)}";
}
