using Benzene.Abstractions.MessageHandlers.Response;
using Benzene.Abstractions.Results;
using Benzene.Results;

namespace Benzene.Core.MessageHandlers.Response;

/// <summary>
/// An <see cref="IProblemFormatter{TContext}"/> built from delegates, for
/// <c>AddProblemFormatter&lt;TContext&gt;(format, ...)</c>.
/// </summary>
/// <typeparam name="TContext">The transport context type this formatter applies to.</typeparam>
public class DelegateProblemFormatter<TContext> : IProblemFormatter<TContext>
{
    private readonly Func<ProblemDetails, IBenzeneResult, object> _format;
    private readonly Func<IBenzeneResult, bool>? _applies;

    /// <summary>Initializes a new instance of the <see cref="DelegateProblemFormatter{TContext}"/> class.</summary>
    /// <param name="format">Builds the body from the problem document and the failed result.</param>
    /// <param name="contentType">The body's content type.</param>
    /// <param name="applies">Which failures to format; <c>null</c> formats every failure.</param>
    public DelegateProblemFormatter(Func<ProblemDetails, IBenzeneResult, object> format, string contentType,
        Func<IBenzeneResult, bool>? applies = null)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentException.ThrowIfNullOrEmpty(contentType);
        _format = format;
        _applies = applies;
        ContentType = contentType;
    }

    /// <inheritdoc />
    public string ContentType { get; }

    /// <inheritdoc />
    public bool Applies(TContext context, IBenzeneResult result) => _applies?.Invoke(result) ?? true;

    /// <inheritdoc />
    public object Format(TContext context, ProblemDetails problem, IBenzeneResult result) => _format(problem, result);
}
