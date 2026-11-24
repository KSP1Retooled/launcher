using KSP.Retooled.Launcher.Exceptions;
using KSP.Retooled.Launcher.Patches;
using KSP.Retooled.Launcher.Releases;

namespace KSP.Retooled.Launcher.Tests.Mocking;

/// <summary>
/// Serves patch files built by the test, by tag.
/// </summary>
internal sealed class FakePatches : IPatchProvider
{
    public readonly Dictionary<string, string> files = [];
    public readonly List<string> kept = [];

    public Task<string> GetPatchAsync(Release release,
                                      string platform,
                                      IProgress<LauncherProgress>? progress,
                                      CancellationToken cancellation) => this.files.TryGetValue(release.Tag, out string? path)
                                                                             ? Task.FromResult(path)
                                                                             : throw new LauncherException($"no patch for {release.Tag}");

    public Task<string> ImportAsync(string file,
                                    IProgress<LauncherProgress>? progress,
                                    CancellationToken cancellation) => Task.FromResult(file);

    public void Prune(IEnumerable<string> keep)
    {
        this.kept.Clear();
        this.kept.AddRange(keep);
    }
}
