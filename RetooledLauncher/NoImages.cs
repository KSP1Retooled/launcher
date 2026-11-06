using Markdown.Avalonia.Utils;

namespace KSP.Retooled.Launcher.App;

/// <summary>
/// Loads no images for the release notes: the renderer would otherwise fetch any path or URL in them as soon as they
/// are shown, and <see cref="KSP.Retooled.Launcher.Releases.Release.Notes"/> carries text from outside the project.
/// </summary>
sealed class NoImages : IPathResolver
{
    public string? AssetPathRoot
    {
        set { }
    }

    public IEnumerable<string>? CallerAssemblyNames
    {
        set { }
    }

    public Task<Stream?>? ResolveImageResource(string relativeOrAbsolutePath) => null;
}
