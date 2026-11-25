using System.CommandLine;
using System.CommandLine.Parsing;
using DotMake.CommandLine;
using KSP.Retooled.Patch.Cli.Commands;
using KSP.Retooled.Patch.Exceptions;
using DotMakeCli = DotMake.CommandLine.Cli;

namespace KSP.Retooled.Patch.Cli;

internal static class Program
{
    /// <summary>
    /// The exit code for a bad command line, a bad install or a bad patch file.
    /// </summary>
    private const int USAGE_ERROR = 2;

    internal static int Main(string[] args)
    {
        Console.Title = "KSP Retooled CLI";

        CliRunnableResult result = DotMakeCli.Parse<RootCliCommand>(args);
        ParseResult parse = result.ParseResult;

        // Help and --version clear parse errors, so a command can show its help even when its required options are missing.
        if (parse.Errors.Count > 0 && parse.Action is not { ClearsParseErrors: true })
        {
            foreach (ParseError error in parse.Errors)
            {
                Console.Error.WriteLine(error.Message);
            }
            Console.Error.Flush();

            Command command = parse.CommandResult.Command;
            string name = command.Parents.Any() ? $"retooled-patch {command.Name}" : "retooled-patch";
            return Fail($"To see the options, run '{name} --help'.");
        }

        try
        {
            return result.Run();
        }
        catch (Exception e) when (e is PatchException or InvalidDataException or IOException)
        {
            // A bad install or patch file is the user's problem to fix, so it doesn't need a stack trace.
            return Fail($"[{e.GetType().Name}] {e.Message}");
        }
        catch (Exception e)
        {
            // Unknown error
            Console.Error.WriteLine($"Unexpected error: [{e.GetType().Name}] {e.Message}\n{e.StackTrace}");
            Console.Error.Flush();
            return 1;
        }
    }

    /// <summary>
    /// Reports a problem with the command line that the parser can't check by itself.
    /// </summary>
    internal static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.Flush();
        return USAGE_ERROR;
    }
}
