using KSP.Retooled.Launcher.Installs;
using Xunit;

namespace KSP.Retooled.Launcher.Tests;

public sealed class InstallLocatorTests
{
    [Fact]
    public void ReadsSteamLibraryPaths()
    {
        const string VDF = """
                           "libraryfolders"
                           {
                               "0"
                               {
                                   "path"		"C:\\Program Files (x86)\\Steam"
                                   "apps" { "228980" "1" }
                               }
                               "1"
                               {
                                   "path"		"D:\\SteamLibrary"
                               }
                           }
                           """;

        Assert.Equal(
                     [@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"],
                     InstallLocator.ParseLibraryFolders(VDF)
                    );
    }

    [Fact]
    public void ReadsLegacySteamLibraryPaths()
    {
        const string VDF = """
                           "LibraryFolders"
                           {
                               "TimeNextStatsReport"		"1600000000"
                               "ContentStatsID"		"-1234"
                               "1"		"D:\\SteamLibrary"
                               "2"		"/mnt/games/steam"
                           }
                           """;

        Assert.Equal(
                     [@"D:\SteamLibrary", "/mnt/games/steam"],
                     InstallLocator.ParseLibraryFolders(VDF)
                    );
    }

    [Fact]
    public void ReadsHeroicGogInstalls()
    {
        const string JSON = """
                            {
                              "installed": [
                                {
                                  "platform": "linux",
                                  "executable": "",
                                  "install_path": "/home/kerbal/Games/Heroic/Kerbal Space Program",
                                  "appName": "1429864849"
                                },
                                {
                                  "platform": "windows",
                                  "install_path": "/home/kerbal/Games/Heroic/Other Game",
                                  "appName": "1111111111"
                                }
                              ]
                            }
                            """;

        Assert.Equal(
                     ["/home/kerbal/Games/Heroic/Kerbal Space Program"],
                     InstallLocator.ParseHeroicInstalls(JSON, "1429864849")
                    );

        Assert.Empty(InstallLocator.ParseHeroicInstalls("not json", "1429864849"));
        Assert.Empty(InstallLocator.ParseHeroicInstalls("[]", "1429864849"));
    }

    [Fact]
    public void MapsWindowsPathsIntoWinePrefixes()
    {
        const string PREFIX = "/home/kerbal/.wine";
        Assert.Equal(
                     Path.Combine(PREFIX, "drive_c", "Program Files (x86)", "Steam"),
                     InstallLocator.WindowsPathInPrefix(PREFIX, @"C:\Program Files (x86)\Steam")
                    );

        Assert.Equal(
                     Path.Combine(PREFIX, "dosdevices", "d:", "SteamLibrary"),
                     InstallLocator.WindowsPathInPrefix(PREFIX, @"D:\SteamLibrary")
                    );

        Assert.Equal(
                     Path.Combine("/", "mnt", "games"),
                     InstallLocator.WindowsPathInPrefix(PREFIX, "z:/mnt/games/")
                    );

        Assert.Null(InstallLocator.WindowsPathInPrefix(PREFIX, "/mnt/games"));
    }

    [Fact]
    public void ReadsHeroicAndMinigalaxyInstallFolders()
    {
        Assert.Equal(
                     "/data/games",
                     InstallLocator.ParseHeroicInstallFolder(
                                                             """{ "defaultSettings": { "defaultInstallPath": "/data/games" } }"""
                                                            )
                    );

        Assert.Null(InstallLocator.ParseHeroicInstallFolder("{}"));
        Assert.Equal(
                     "/home/kerbal/GOG Games",
                     InstallLocator.ParseMinigalaxyInstallFolder(
                                                                 """{ "install_dir": "/home/kerbal/GOG Games" }"""
                                                                )
                    );

        Assert.Null(InstallLocator.ParseMinigalaxyInstallFolder("garbage"));
    }
}