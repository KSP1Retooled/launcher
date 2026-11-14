using DotMake.CommandLine;
using DotMakeCli = DotMake.CommandLine.Cli;

namespace KSP.Retooled.Patch.Cli;

public static class Program
{
    /// <summary>The exit code for a bad command line, a bad install or a bad patch file.</summary>
    internal const int UsageError = 2;

    public static int Main(string[] args)
    {
        var result = DotMakeCli.Parse<RootCliCommand>(args);
        var parse = result.ParseResult;
        // Help and --version clear parse errors, so a command can show its help even when its required options are missing.
        if (parse.Errors.Count > 0 && parse.Action is not { ClearsParseErrors: true })
        {
            foreach (var error in parse.Errors) Console.Error.WriteLine(error.Message);
            var command = parse.CommandResult.Command;
            string name = command.Parents.Any() ? $"retooled-patch {command.Name}" : "retooled-patch";
            Console.Error.WriteLine($"To see the options, run '{name} --help'.");
            return UsageError;
        }

        try
        {
            return result.Run();
        }
        catch (Exception e) when (e is PatchException or InvalidDataException or IOException)
        {
            // A bad install or patch file is the user's problem to fix, so it doesn't need a stack trace.
            Console.Error.WriteLine(e.Message);
            return UsageError;
        }
    }

    /// <summary>Reports a problem with the command line that the parser can't check by itself.</summary>
    internal static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return UsageError;
    }
}

[CliCommand(
    Description = "Builds, checks, applies and reverts KSP Retooled patch files (.rtpatch).",
    ShortFormAutoGenerate = CliNameAutoGenerate.None)]
public class RootCliCommand;
