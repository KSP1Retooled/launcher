using System.Runtime.InteropServices;

namespace KSP.Retooled.Patch;

internal static class FileClone
{
    public static void LinkOrCopy(string source, string destination)
    {
        if (TryHardLink(source, destination))
        {
            return;
        }

        File.Copy(source, destination);
    }

    // TODO: Replace this with File.CreateHardLink when we update to .NET 11
    public static bool TryHardLink(string source, string destination)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return CreateHardLink(destination, source, IntPtr.Zero);
            }

            return link(source, destination) is 0;
        }
        catch (EntryPointNotFoundException) { }
        catch (DllNotFoundException) { }
        return false;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("libc", SetLastError = true)]
    private static extern int link(string existing, string link);
}
