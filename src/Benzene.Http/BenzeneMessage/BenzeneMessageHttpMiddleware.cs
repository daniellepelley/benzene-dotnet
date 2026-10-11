using System.Threading;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers.Response;
using Benzene.Abstractions.Messages.Mappers;
using Benzene.Abstractions.Middleware;
using Benzene.Core.MessageHandlers.BenzeneMessage;
using Benzene.Core.Messages.BenzeneMessage;
using Benzene.Results;
using JsonSerializer = Benzene.Core.MessageHandlers.Serialization.JsonSerializer;

namespace Benzene.Http.BenzeneMessage;

/// <summary>
/// Transport-agnostic HTTP middleware that dispatches a POSTed BenzeneMessage envelope
/// (<c>{ "topic": ..., "headers": ..., "body": ... }</c>) into a BenzeneMessage pipeline — the
/// HTTP equivalent of the direct AWS Lambda invoke path. Works on any Benzene HTTP transport
/// (API Gateway, Azure Functions, ASP.NET Core, self-host) because it drives the transport-neutral
/// request/response adapters directly, the same short-circuit shape as <c>SpecUiMiddleware</c> and
/// <c>CorsMiddleware</c>.
/// </summary>
/// <typeparam name="TContext">The HTTP context type.</typeparam>
/// <remarks>
/// <para>
/// On a POST to its path it runs the envelope through the pipeline (topic routing, validation,
/// middleware, handler — the <c>"benzene"</c> transport) and writes the response envelope
/// (<c>{ "statusCode": ..., "isSuccessful": ..., "headers": ..., "body": ... }</c>) as
/// <c>application/json</c>. Any other request falls through to <c>next</c>. The envelope is always
/// read and written with Benzene's default JSON serialization, regardless of the payload formats the
/// app's own serializer negotiates.
/// </para>
/// <para>
/// <b>HTTP 200 for every envelope it processed, whatever its status</b> (transport-bindings.md §1): the
/// envelope carries the outcome (<c>statusCode</c>, <c>isSuccessful</c>) and a client reads it, never
/// the status line. A <c>not-found</c>, a <c>validation-error</c> (an envelope with no topic is processed
/// - the router answers it), a <c>deleted</c> with its body, an <c>unauthorized</c> from the pipeline's own
/// auth middleware - all 200. The problem document inside the envelope omits <c>status</c>
/// (wire-contracts.md §1.3; this endpoint never decorates the envelope pipeline with the HTTP
/// <c>status</c> member). A non-2xx status means no envelope was processed: a body that is not an
/// envelope (empty, not JSON, not a JSON object) is <b>400</b>, with an <c>application/problem+json</c>
/// body; 401/403 come only from authentication the host enforces before this middleware runs.
/// </para>
/// <para>
/// Security: this endpoint runs the same pipeline as the service's other transports, so the same
/// authentication and authorization middleware - compose it in front. Because it reaches every handler
/// the service registers, including topics with no HTTP mapping, restrict the topics it serves with
/// <see cref="BenzeneMessageHttpOptions.TopicFilter"/> (spec D21's required restriction): a topic the
/// filter rejects is answered <c>not-found</c>, exactly as if no handler matched. Do not expose it
/// unauthenticated in production.
/// </para>
/// </remarks>
public class BenzeneMessageHttpMiddleware<TContext> : IMiddleware<TContext>, ITerminalMiddleware where TContext : IHttpContext
{
    private static readonly JsonSerializer Serializer = new();

    private readonly string _path;
    private readonly Func<string, bool>? _topicFilter;
    private readonly BenzeneMessageApplication _application;
    private readonly IServiceResolver _serviceResolver;
    private readonly IHttpRequestAdapter<TContext> _httpRequestAdapter;
    private readonly IMessageBodyGetter<TContext> _messageBodyGetter;
    private readonly IBenzeneResponseAdapter<TContext> _responseAdapter;
    private readonly IHttpStatusCodeMapper _httpStatusCodeMapper;

    /// <summary>
    /// Initializes a new instance of the <see cref="BenzeneMessageHttpMiddleware{TContext}"/> class.
    /// </summary>
    /// <param name="options">The endpoint options (path and optional topic filter).</param>
    /// <param name="pipeline">The built BenzeneMessage pipeline to dispatch envelopes to.</param>
    /// <param name="serviceResolver">The service resolver for the current invocation scope.</param>
    /// <param name="httpRequestAdapter">Adapter used to read the request method and path.</param>
    /// <param name="messageBodyGetter">Getter used to read the request body.</param>
    /// <param name="responseAdapter">Adapter used to write the response.</param>
    /// <param name="httpStatusCodeMapper">
    /// Maps a carrier failure's status to the HTTP status line. A processed envelope is always 200, so
    /// this is consulted only when the request body is not an envelope.
    /// </param>
    public BenzeneMessageHttpMiddleware(
        BenzeneMessageHttpOptions options,
        IMiddlewarePipeline<BenzeneMessageContext> pipeline,
        IServiceResolver serviceResolver,
        IHttpRequestAdapter<TContext> httpRequestAdapter,
        IMessageBodyGetter<TContext> messageBodyGetter,
        IBenzeneResponseAdapter<TContext> responseAdapter,
        IHttpStatusCodeMapper httpStatusCodeMapper)
    {
        _path = NormalizePath(options.Path);
        _topicFilter = options.TopicFilter;
        _application = new BenzeneMessageApplication(pipeline);
        _serviceResolver = serviceResolver;
        _httpRequestAdapter = httpRequestAdapter;
        _messageBodyGetter = messageBodyGetter;
        _responseAdapter = responseAdapter;
        _httpStatusCodeMapper = httpStatusCodeMapper;
    }

    /// <summary>
    /// Gets the name of the middleware.
    /// </summary>
    public string Name => "BenzeneMessageHttp";

    /// <summary>
    /// Dispatches a matching POSTed envelope into the BenzeneMessage pipeline and short-circuits;
    /// any other request is passed to the next middleware.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <param name="next">The next middleware in the pipeline.</param>
    public async Task HandleAsync(TContext context, Func<Task> next)
    {
        var request = _httpRequestAdapter.Map(context);

        if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase) ||
            NormalizePath(request.Path) != _path)
        {
            await next();
            return;
        }

        var envelope = ReadEnvelope(context);
        if (envelope == null)
        {
            // A carrier failure: no envelope was processed, so the HTTP response itself carries the
            // outcome - a problem document whose status equals the status line (wire-contracts §1.3, §4.1).
            var problem = ProblemTypes.From(BenzeneResult.BadRequest(
                "The request body is not a BenzeneMessage envelope: expected a JSON object { \"topic\", \"headers\", \"body\" }"));
            var statusCode = _httpStatusCodeMapper.Map(BenzeneResultStatus.BadRequest, false);
            problem.Status = int.Parse(statusCode);
            _responseAdapter.SetStatusCode(context, statusCode);
            _responseAdapter.SetContentType(context, "application/problem+json; charset=utf-8");
            _responseAdapter.SetBody(context, Serializer.Serialize(problem));
            await _responseAdapter.FinalizeAsync(context);
            return;
        }

        var response = await DispatchAsync(envelope);

        // Every processed envelope is 200, whatever its status: the envelope is the outcome
        // (transport-bindings §1). Mapping the inner status onto the status line made a client that
        // reads the line - or a proxy that rewrites error bodies - see a different outcome than the one
        // the envelope reports.
        _responseAdapter.SetStatusCode(context, ProcessedStatusCode);
        _responseAdapter.SetContentType(context, "application/json; charset=utf-8");
        _responseAdapter.SetBody(context, Serializer.Serialize(response));
        await _responseAdapter.FinalizeAsync(context);
    }

    /// <summary>The HTTP status every envelope the endpoint processed is answered with.</summary>
    public const string ProcessedStatusCode = "200";

    /// <summary>
    /// The request body as an envelope, or <c>null</c> when it is not one - empty, not JSON, or not a
    /// JSON object. A JSON object without a topic IS an envelope (the router answers it with
    /// <c>validation-error</c>, envelope-cases.json's empty-topic case), so it is not refused here.
    /// </summary>
    private BenzeneMessageRequest? ReadEnvelope(TContext context)
    {
        try
        {
            var body = _messageBodyGetter.GetBody(context);
            if (string.IsNullOrWhiteSpace(body) || !body.TrimStart().StartsWith('{'))
            {
                return null;
            }

            var request = Serializer.Deserialize<BenzeneMessageRequest>(body);
            if (request == null)
            {
                return null;
            }

            request.Topic ??= string.Empty;
            return request;
        }
        catch
        {
            return null;
        }
    }

    private async Task<IBenzeneMessageResponse> DispatchAsync(BenzeneMessageRequest benzeneMessageRequest)
    {
        if (_topicFilter != null && !string.IsNullOrEmpty(benzeneMessageRequest.Topic) && !_topicFilter(benzeneMessageRequest.Topic))
        {
            // Restricted (spec D21): answered exactly as if no handler matched - not-found, the same
            // problem document the router writes, and no hint that the topic exists elsewhere.
            return NotFoundResponse($"No handler found for topic '{benzeneMessageRequest.Topic}'");
        }

        // Resolve the OUTER (per-HTTP-request) scope's ambient cancellation accessor — already seeded
        // from the real transport's cancellation signal (e.g. HttpContext.RequestAborted) by the
        // "SeedCancellationToken" middleware in BuildHttpPipeline — and thread its token through the
        // 3-argument HandleAsync overload so the INNER scope this envelope dispatches into is seeded
        // with the real request's token, not CancellationToken.None (the 2-argument overload's hardcoded
        // default). TryGetService keeps this null-tolerant: a pipeline with no accessor registered (or
        // no transport-level seeding) behaves exactly as before. See #285.
        var cancellationToken = _serviceResolver.TryGetService<ICancellationTokenAccessor>()?.CancellationToken
            ?? CancellationToken.None;

        return await _application.HandleAsync(benzeneMessageRequest,
            _serviceResolver.GetService<IServiceResolverFactory>(),
            cancellationToken);
    }

    private static IBenzeneMessageResponse NotFoundResponse(string detail)
    {
        // The inner problem document omits status: it is not itself an HTTP response (wire §1.3).
        return new BenzeneMessageResponse
        {
            StatusCode = BenzeneResultStatus.NotFound,
            IsSuccessful = false,
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["content-type"] = "application/problem+json"
            },
            Body = Serializer.Serialize(ProblemTypes.From(BenzeneResult.NotFound(detail)))
        };
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        var trimmed = path.Trim();
        if (!trimmed.StartsWith('/'))
        {
            trimmed = "/" + trimmed;
        }

        if (trimmed.Length > 1 && trimmed.EndsWith('/'))
        {
            trimmed = trimmed.TrimEnd('/');
        }

        return trimmed.ToLowerInvariant();
    }
}
