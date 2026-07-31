namespace Benzene.Abstractions.Middleware;

/// <summary>
/// Marks middleware that can end a pipeline — that produces the response, dispatches to a handler, or
/// sends the message — rather than decorating whatever comes after it.
/// </summary>
/// <remarks>
/// <para>
/// A pipeline made entirely of decorators composes perfectly and runs perfectly, and does nothing.
/// <c>UseSqs(sqs =&gt; { })</c> is the shortest example: it builds, it deploys, and every message walks
/// the pipeline, reaches the end, is never handled, and is dead-lettered. Nothing fails, so nothing
/// says anything. The same is true of a pipeline that has logging and validation but no
/// <c>UseMessageHandlers()</c>.
/// </para>
/// <para>
/// Deliberately non-generic. The start-up check that uses it walks every pipeline in the process
/// through <c>PipelineDescriptor</c>, which erases each middleware to <see cref="object"/> because it
/// cannot be generic over every pipeline's context type. A marker with no context parameter is
/// testable from there; <c>IMiddleware&lt;TContext&gt;</c> is not.
/// </para>
/// <para>
/// Custom middleware that ends a pipeline should implement this. Nothing about execution changes —
/// it is a statement of intent, read only at start-up, and the check's message says so when it
/// reports a pipeline it believes goes nowhere.
/// </para>
/// </remarks>
public interface ITerminalMiddleware
{
}
