using Benzene.CodeGen.Cli.Core.Parsing;

namespace Benzene.CodeGen.Cli.Core;

public class CommandRouter
{
    private readonly ICommand[] _commands;

    public CommandRouter(params ICommand[] commands)
    {
        _commands = commands.Concat(new []
        {
            new HelpCommand(commands)
        }).ToArray();
    }

    public async Task RouteAsync(CommandArguments args)
    {
        var command = _commands.FirstOrDefault(x => x.Name == args.Name);
        if (command == null)
        {
            Console.Error.WriteLine($"Command {args.Name} not found");
            Console.Error.WriteLine();

            // Print the available commands before failing, so a mistyped command name is
            // recoverable from the CLI's own output rather than just a non-zero exit code.
            var help = _commands.First(x => x.Name == "help");
            await help.ExecuteAsync(new CommandArguments { Name = "help", Attributes = new Dictionary<string, string?>() });

            throw new InvalidOperationException($"Command '{args.Name}' not found");
        }

        await command.ExecuteAsync(args);
    }
}
