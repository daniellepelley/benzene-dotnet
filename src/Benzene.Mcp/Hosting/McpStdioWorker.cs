using Benzene.Abstractions.DI;
using Benzene.Abstractions.Hosting;

namespace Benzene.Mcp.Hosting;

/// <summary>
/// MCP over stdio, as an <see cref="IBenzeneWorker"/>: one JSON-RPC message per line in, one per
/// line out, for a client running on the same machine (a desktop assistant, an IDE).
/// </summary>
/// <remarks>
/// One rule governs everything here: the output stream carries the protocol and nothing else. A stray
/// <c>Console.WriteLine</c> anywhere in the process (a log line, a warning) lands in the middle of a
/// frame and the client sees a parse error it cannot explain. Everything this worker has to say goes
/// to the log action (standard error by default), which the client shows the user and never parses.
/// Each message runs in a DI scope of its own, per the binding contract's scope rule.
/// </remarks>
public sealed class McpStdioWorker : IBenzeneWorker
{
    private readonly McpServer _server;
    private readonly IServiceResolverFactory _serviceResolverFactory;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly Action<string> _log;

    /// <summary>Initializes a new instance of the <see cref="McpStdioWorker"/> class.</summary>
    /// <param name="server">The protocol server.</param>
    /// <param name="serviceResolverFactory">Creates the scope each message runs in.</param>
    /// <param name="input">Where messages arrive (standard input, typically).</param>
    /// <param name="output">Where answers go (standard output, typically). Nothing else may write to it.</param>
    /// <param name="log">Where the worker's own remarks go; never <paramref name="output"/>.</param>
    public McpStdioWorker(McpServer server, IServiceResolverFactory serviceResolverFactory, TextReader input, TextWriter output, Action<string> log)
    {
        _server = server;
        _serviceResolverFactory = serviceResolverFactory;
        _input = input;
        _output = output;
        _log = log;
    }

    /// <summary>Reads and answers messages until the input ends or <paramref name="cancellationToken"/> is signalled.</summary>
    /// <param name="cancellationToken">The token used to stop the loop.</param>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await _input.ReadLineAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (line is null) return; // the client closed its end; the conversation is over
            if (string.IsNullOrWhiteSpace(line)) continue;

            string? answer;
            try
            {
                using var scope = _serviceResolverFactory.CreateScope();
                var response = await _server.HandleAsync(line, scope, sessionId: null, cancellationToken);
                answer = response?.ToJsonString();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // Something threw where nothing expected it to. Say so on the log and keep the loop alive:
                // a tool server that exits on one bad call takes the whole conversation with it.
                _log($"unhandled while answering: {e}");
                continue;
            }

            if (answer is null) continue; // a notification; the protocol says not to answer one
            await _output.WriteLineAsync(answer);
            await _output.FlushAsync(cancellationToken);
        }
    }

    /// <summary>Stops the worker. Nothing to release beyond leaving the read loop.</summary>
    /// <param name="cancellationToken">The cancellation token for the stop operation.</param>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
