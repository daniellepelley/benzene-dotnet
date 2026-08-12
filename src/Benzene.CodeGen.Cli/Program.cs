using Benzene.CodeGen.Cli.Core;

namespace Benzene.CodeGen.Cli;

class Program
{
    // Real exit codes so `benzene` is usable as a CI gate: 0 on success, 1 on any propagated
    // failure. Commands signal failure by throwing (see ConsoleApplication.ExecuteAsync, which lets
    // exceptions propagate); this is the one place that turns that into a process exit code. The
    // interactive REPL (args.Length == 0) is unaffected - it runs forever and keeps its own local
    // try/catch so one bad command doesn't kill the session.
    static async Task<int> Main(string[] args)
    {
        var consoleApplication = new ConsoleApplication();
        if (args.Length == 0)
        {
            do
            {
                var stringArgs = Console.ReadLine();

                try
                {
                    await consoleApplication.ExecuteAsync(stringArgs);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex);
                }
            } while (true);
        }

        try
        {
            await consoleApplication.ExecuteAsync(args);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}
