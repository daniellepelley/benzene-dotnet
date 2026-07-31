using Benzene.Abstractions.DI;
using Benzene.Abstractions.Middleware;
using Benzene.Abstractions.Pipelines;
using Benzene.Abstractions.StartUpChecks;
using Benzene.Core.Exceptions;

namespace Benzene.Core.MessageHandlers.StartUpChecks;

/// <summary>
/// Reports a pipeline that has nothing in it that can end — every message walks it, reaches the end,
/// and is discarded.
/// </summary>
/// <remarks>
/// <para>
/// <c>UseSqs(sqs =&gt; { })</c> is the whole reason this exists. It compiles, it composes, it deploys,
/// and it dead-letters the entire queue: each message runs a pipeline of decorators, is never
/// dispatched to a handler, produces no result, and SQS redrives it until it gives up. Nothing throws,
/// the function returns 200, and the service reports itself healthy while losing every message. The
/// same holds for a pipeline that has <c>UseLogger()</c> and <c>UseValidation()</c> but no
/// <c>UseMessageHandlers()</c> — the mistake is easier to make the more middleware is in the way.
/// </para>
/// <para>
/// The rule is <see cref="ITerminalMiddleware"/>: a pipeline needs at least one middleware that claims
/// to be able to end it. That is a positive marker rather than an inference, because "does this
/// middleware call <c>next</c>?" is not answerable without running it, and most middleware answers
/// "sometimes".
/// </para>
/// <para>
/// It follows that a custom terminal middleware that has not been marked will be reported. That is the
/// one false positive this check can produce, so the message names the fix — implement the marker —
/// alongside the more likely one. An empty pipeline (no middleware at all) is left alone: it is
/// unmistakably deliberate scaffolding, and reporting it would fire on test doubles that never handle
/// a message.
/// </para>
/// </remarks>
public class TerminalMiddlewareStartUpCheck : IStartUpCheck
{
    /// <inheritdoc />
    public string Name => "terminal-middleware";

    /// <inheritdoc />
    public void Check(IServiceResolver resolver)
    {
        var failures = new List<string>();

        foreach (var pipeline in resolver.GetServices<PipelineDescriptor>())
        {
            if (pipeline.Constructors.Count == 0)
            {
                continue;
            }

            var terminals = 0;
            var unconstructible = false;

            foreach (var constructor in pipeline.Constructors)
            {
                try
                {
                    if (constructor(resolver) is ITerminalMiddleware)
                    {
                        terminals++;
                    }
                }
                catch (Exception)
                {
                    // pipeline-resolution owns this failure and reports it with the dependency that was
                    // missing. Here it only means the middleware's intent is unknown, and guessing would
                    // turn one clear error into two, the second of them wrong.
                    unconstructible = true;
                }
            }

            if (terminals == 0 && !unconstructible)
            {
                failures.Add($"the {pipeline.Name} pipeline has no terminal middleware, so every message " +
                             "reaching it would run to the end of the pipeline unhandled");
            }
        }

        if (failures.Count > 0)
        {
            throw new BenzeneException(
                $"{failures.Count} pipeline(s) cannot handle a message:{Environment.NewLine}  " +
                string.Join($"{Environment.NewLine}  ", failures) + Environment.NewLine +
                "Add the middleware that handles the message — UseMessageHandlers() for an inbound " +
                "pipeline, the transport's send middleware for an outbound one. If the pipeline does end " +
                "in your own middleware, mark that middleware with ITerminalMiddleware to say so.");
        }
    }
}
