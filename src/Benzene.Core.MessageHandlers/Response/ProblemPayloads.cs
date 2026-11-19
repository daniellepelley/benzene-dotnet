using Benzene.Abstractions.MessageHandlers.Response;
using Benzene.Abstractions.Results;
using Benzene.Abstractions.Serialization;
using Benzene.Results;

namespace Benzene.Core.MessageHandlers.Response;

/// <summary>
/// Writes a failed result's body: the problem document, or the registered
/// <see cref="IProblemFormatter{TContext}"/>'s format when one applies. Shared by every response
/// payload mapper, so the rule is the same on every transport.
/// </summary>
public static class ProblemPayloads
{
    /// <summary>
    /// Serializes <paramref name="problem"/>, or what <paramref name="formatter"/> makes of it when it
    /// applies to <paramref name="result"/>.
    /// </summary>
    /// <param name="context">The transport context for the current message.</param>
    /// <param name="result">The failed result.</param>
    /// <param name="problem">The problem document built for the result.</param>
    /// <param name="serializer">The message's negotiated serializer.</param>
    /// <param name="formatter">The context's formatter, or <c>null</c> when none is registered.</param>
    public static string Serialize<TContext>(TContext context, IBenzeneResult result, ProblemDetails problem,
        ISerializer serializer, IProblemFormatter<TContext>? formatter)
    {
        if (!Applies(formatter, context, result))
        {
            // By runtime type, so a handler's ProblemDetails subclass keeps its extension members
            // (serializing as ProblemDetails dropped them).
            return serializer.Serialize(problem.GetType(), problem);
        }

        var body = formatter!.Format(context, problem, result);
        return body switch
        {
            null => null!,
            string text => text,
            _ => serializer.Serialize(body.GetType(), body),
        };
    }

    /// <summary>
    /// Whether <paramref name="formatter"/> writes the body for <paramref name="result"/>: it is
    /// registered, the result failed, and its <see cref="IProblemFormatter{TContext}.Applies"/> agrees.
    /// The renderer asks the same question to choose the content type.
    /// </summary>
    public static bool Applies<TContext>(IProblemFormatter<TContext>? formatter, TContext context, IBenzeneResult result)
    {
        return formatter != null && !result.IsSuccessful && formatter.Applies(context, result);
    }
}
