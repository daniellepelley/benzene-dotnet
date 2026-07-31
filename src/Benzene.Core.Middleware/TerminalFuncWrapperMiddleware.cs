using Benzene.Abstractions.Middleware;

namespace Benzene.Core.Middleware;

/// <summary>
/// A <see cref="FuncWrapperMiddleware{TContext}"/> that answers the message itself rather than
/// decorating whatever comes after it.
/// </summary>
/// <typeparam name="TContext">The context type that the middleware operates on.</typeparam>
/// <remarks>
/// <para>
/// Inline middleware is the one shape the terminal-middleware start-up check cannot read from a type:
/// <see cref="FuncWrapperMiddleware{TContext}"/> is used for both pass-through decorators and things
/// that end a pipeline, and which one it is lives inside a lambda. The health-check endpoints are the
/// framework's own case — <c>UseLivenessCheck</c> alone is a complete, working pipeline — so the
/// distinction has to be expressible rather than inferred.
/// </para>
/// <para>
/// A separate type rather than a flag because <see cref="ITerminalMiddleware"/> cannot be implemented
/// conditionally. Behaviour is identical to the base class.
/// </para>
/// </remarks>
/// <param name="name">The middleware's name, as it appears in tracing.</param>
/// <param name="func">The function that defines the middleware behaviour.</param>
public class TerminalFuncWrapperMiddleware<TContext>(string name, Func<TContext, Func<Task>, Task> func)
    : FuncWrapperMiddleware<TContext>(name, func), ITerminalMiddleware
{
}
