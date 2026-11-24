using KSP.Retooled.Patch.Exceptions;

namespace KSP.Retooled.Patch.Utils;

/// <summary>
/// Protects other KSP installs when patching an install in place. Replacing a file at its path preserves the
/// contents shared through a hard link or file symlink. A change inside a symlinked directory or junction
/// reaches the shared directory directly, so the patcher first replaces the directory link with a local directory.
/// </summary>
internal static class SharedLinks
{
    /// <summary>
    /// Replaces directory links at or above each path in <paramref name="paths"/>, relative to
    /// <paramref name="install"/>. It hard-links regular files when possible and copies them otherwise.
    /// It recreates nested symlinks when possible and falls back to copying their contents.
    /// </summary>
    public static void Unshare(string install, IEnumerable<string> paths, TextWriter log)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string path in paths)
        {
            string current = install;
            foreach (string part in path.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                if (!seen.Add(current)) continue;

                DirectoryInfo info = new(current);
                if (!info.Exists) break;

                if (info.LinkTarget is null) continue;

                string target = Materialize(info);
                log.WriteLine($"unshared {Path.GetRelativePath(install, current).Replace('\\', '/')}: copied it from {target}, which it was linked to");
            }
        }
    }

    /// <summary>
    /// Replaces the directory link with a local copy and returns the original target path.
    /// </summary>
    private static string Materialize(DirectoryInfo link)
    {
        string target = link.ResolveLinkTarget(true)?.FullName
                     ?? throw new PatchException($"{link.FullName} is a link to {link.LinkTarget}, which does not exist");

        string copy = link.FullName + ".retooled-unshare";
        if (Directory.Exists(copy))
        {
            Directory.Delete(copy, true);
        }

        try
        {
            CopyTree(target, copy);
        }
        catch
        {
            if (Directory.Exists(copy)) Directory.Delete(copy, true);
            throw;
        }

        // Remove the link so the local directory can take its place.
        link.Delete();
        Directory.Move(copy, link.FullName);
        return target;
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (FileSystemInfo entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            string to = Path.Combine(destination, entry.Name);
            if (entry.LinkTarget is not null && TryRecreateLink(entry, to)) continue;

            if (entry is DirectoryInfo)
            {
                CopyTree(entry.FullName, to);
            }
            else
            {
                FileClone.LinkOrCopy(entry.FullName, to);
            }
        }
    }

    /// <summary>
    /// Creates a symlink at <paramref name="to"/> with the same target as <paramref name="entry"/>.
    /// Returns false if an I/O or permissions error prevents creating the link.
    /// </summary>
    private static bool TryRecreateLink(FileSystemInfo entry, string to)
    {
        // Resolve relative targets from the original link's parent so the new link points to the same location.
        string target = Path.GetFullPath(entry.LinkTarget!, Path.GetDirectoryName(entry.FullName)!);
        try
        {
            if (entry is DirectoryInfo)
            {
                Directory.CreateSymbolicLink(to, target);
            }
            else
            {
                File.CreateSymbolicLink(to, target);
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
