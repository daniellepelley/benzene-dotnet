using Benzene.Abstractions.Messages;
using Benzene.Abstractions.Messages.Mappers;
using Benzene.Abstractions.MessageHandlers.Mappers;

namespace Benzene.Core.MessageHandlers;

/// <summary>
/// Default <see cref="IMessageVersionGetter{TContext}"/>: reads the message's version from the context's
/// header dictionary (<see cref="IMessageHeadersGetter{TContext}"/>), under <b>one</b> header name -
/// <c>benzene-version</c> unless the application renamed it (wire-contracts.md §2, versioning.md §2.1).
/// </summary>
/// <typeparam name="TContext">
/// The transport-specific context type. No special shape is required beyond what
/// <see cref="IMessageHeadersGetter{TContext}"/> already needs, so this one implementation serves
/// every transport that has no richer version signal of its own (docs/specification/versioning.md
/// §2.3) - HTTP layers a route-parameter check in front of an instance of this class instead of
/// writing its own header read.
/// </typeparam>
/// <remarks>
/// There is no fallback list. Many producers send a plain <c>version</c>/<c>x-version</c> header for their
/// own purposes (an API or SDK version); reading those as the payload version would silently route such a
/// message to another version's handler. So any header but the configured one is an ordinary header and
/// selects nothing.
/// </remarks>
public class HeaderMessageVersionGetter<TContext> : IMessageVersionGetter<TContext>
{
    /// <summary>The default header name, <see cref="MessageVersionHeaders.Default"/> (<c>benzene-version</c>) -
    /// the same name the outbound helpers write.</summary>
    public const string DefaultHeaderName = MessageVersionHeaders.Default;

    private readonly IMessageHeadersGetter<TContext> _headersGetter;
    private readonly string _headerName;

    /// <summary>
    /// Initializes a new instance of the <see cref="HeaderMessageVersionGetter{TContext}"/> class.
    /// </summary>
    /// <param name="headersGetter">Extracts the header dictionary from the context.</param>
    /// <param name="headerName">
    /// The one header the version is read from. Defaults to <see cref="DefaultHeaderName"/>; an application
    /// renames it in one place, <see cref="Benzene.Abstractions.IBenzeneWireNames.Version"/>, which
    /// <see cref="MessageVersionHeaderNameExtensions.AddHeaderMessageVersionGetter{TContext}"/> passes here.
    /// </param>
    public HeaderMessageVersionGetter(IMessageHeadersGetter<TContext> headersGetter, string? headerName = null)
    {
        _headersGetter = headersGetter;
        _headerName = string.IsNullOrEmpty(headerName) ? DefaultHeaderName : headerName;
    }

    /// <summary>The header name this getter reads.</summary>
    public string HeaderName => _headerName;

    /// <inheritdoc />
    public string? GetVersion(TContext context)
    {
        var headers = _headersGetter.GetHeaders(context);
        if (headers == null)
        {
            return null;
        }

        // Matched case-insensitively regardless of the concrete IMessageHeadersGetter's dictionary
        // comparer (wire-contracts.md §2: "header keys are case-insensitive on read").
        foreach (var header in headers)
        {
            if (string.Equals(header.Key, _headerName, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(header.Value))
            {
                return header.Value;
            }
        }

        return null;
    }
}
