using System.Text.Json;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.MessageHandlers.Mappers;
using Benzene.Abstractions.Messages.Mappers;
using Benzene.Abstractions.Middleware;
using Benzene.Abstractions.Results;
using Benzene.Abstractions.Validation;
using Benzene.Core.MessageHandlers;
using Benzene.Results;
using Json.Schema;

namespace Benzene.JsonSchema;

/// <summary>
/// Middleware that validates the request body against the schema resolved by the registered
/// <see cref="IJsonSchemaProvider{TContext}"/>, short-circuiting the pipeline with a validation-error
/// result when the body is missing, malformed, or fails the schema.
/// </summary>
/// <typeparam name="TContext">The transport-specific context type this middleware applies to.</typeparam>
public class JsonSchemaMiddleware<TContext> : IMiddleware<TContext> where TContext : class
{
    private readonly IMessageBodyGetter<TContext> _messageBodyGetter;
    private readonly IJsonSchemaProvider<TContext> _jsonSchemaProvider;
    private readonly IDefaultStatuses _defaultStatuses;
    private readonly IMessageHandlerResultSetter<TContext> _messageHandlerResultSetter;
    private readonly IMessageTopicGetter<TContext> _messageTopicGetter;
    private readonly IMessageHandlerDefinitionLookUp _messageHandlerDefinitionLookUp;
    private readonly IValidationStatusMapper? _validationStatusMapper;

    /// <summary>
    /// Initializes a new instance of the <see cref="JsonSchemaMiddleware{TContext}"/> class.
    /// </summary>
    /// <param name="messageBodyGetter">Reads the raw request body to validate.</param>
    /// <param name="jsonSchemaProvider">Resolves the schema to validate against.</param>
    /// <param name="defaultStatuses">Supplies the status used for a validation-error result when no
    /// <paramref name="validationStatusMapper"/> is registered, or the resolved handler carries no
    /// <c>[ValidationStatus]</c> attribute.</param>
    /// <param name="messageHandlerResultSetter">Sets the short-circuit result on validation failure.</param>
    /// <param name="messageTopicGetter">Resolves the current message's topic from the context.</param>
    /// <param name="messageHandlerDefinitionLookUp">Looks up the handler registered for a topic, to attach to the failure result.</param>
    /// <param name="validationStatusMapper">
    /// The shared <c>Benzene.Abstractions.Validation</c> status mapper - optional, resolved via DI
    /// (e.g. registered by <c>Benzene.FluentValidation</c>'s <c>AddFluentValidation</c> elsewhere in
    /// the app). When present, honours <c>[ValidationStatus]</c> on the resolved handler type the
    /// same way <c>Benzene.FluentValidation</c> and <c>Benzene.DataAnnotations</c> do.
    /// </param>
    public JsonSchemaMiddleware(IMessageBodyGetter<TContext> messageBodyGetter,
        IJsonSchemaProvider<TContext> jsonSchemaProvider,
        IDefaultStatuses defaultStatuses,
        IMessageHandlerResultSetter<TContext> messageHandlerResultSetter,
        IMessageTopicGetter<TContext> messageTopicGetter,
        IMessageHandlerDefinitionLookUp messageHandlerDefinitionLookUp,
        IValidationStatusMapper? validationStatusMapper = null)
    {
        _messageHandlerResultSetter = messageHandlerResultSetter;
        _messageTopicGetter = messageTopicGetter;
        _messageHandlerDefinitionLookUp = messageHandlerDefinitionLookUp;
        _defaultStatuses = defaultStatuses;
        _jsonSchemaProvider = jsonSchemaProvider;
        _messageBodyGetter = messageBodyGetter;
        _validationStatusMapper = validationStatusMapper;
    }

    /// <inheritdoc />
    public string Name => "JsonSchema";

    /// <inheritdoc />
    public async Task HandleAsync(TContext context, Func<Task> next)
    {
        var jsonSchema = _jsonSchemaProvider.Get(context);

        if (jsonSchema == null)
        {
            await next();
            return;
        }

        var body = _messageBodyGetter.GetBody(context);

        if (body == null)
        {
            await SetValidationErrorAsync(context, JsonSchemaValidationErrors.MissingBody);
            return;
        }

        JsonDocument jsonDocument;
        try
        {
            jsonDocument = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            // Malformed JSON is the most clearly-invalid body of all - treat it as a validation
            // failure (like a null or schema-failing body) rather than letting the exception
            // escape the pipeline as an internal error. Mirrors IsJsonValidator.
            await SetValidationErrorAsync(context, JsonSchemaValidationErrors.MalformedBody);
            return;
        }

        using (jsonDocument)
        {
            var schemaResult = jsonSchema.Evaluate(jsonDocument.RootElement, new EvaluationOptions
            {
                OutputFormat = OutputFormat.List
            });

            if (schemaResult.IsValid)
            {
                await next();
            }
            else
            {
                await SetValidationErrorAsync(context, schemaResult, JsonSchemaValidationErrors.Format(schemaResult));
            }
        }
    }

    private Task SetValidationErrorAsync(TContext context, params string[] errors)
    {
        return SetValidationErrorAsync(context, null, errors.Select(e => new BenzeneError(e)).ToArray());
    }

    private Task SetValidationErrorAsync(TContext context, object? validationResult, IReadOnlyList<BenzeneError> errors)
    {
        // Same failure contract as Benzene.FluentValidation/Benzene.DataAnnotations: the errors
        // travel as the result's errors, which the response pipeline serializes as an RFC 9457
        // problem document ({ benzeneStatus, detail, errors, ... } - ProblemTypes.From). The topic's
        // handler definition is attached so the response payload mapper actually writes that body
        // (it skips definition-less results).
        var topic = _messageTopicGetter.GetTopic(context);
        var messageHandlerDefinition = topic != null ? _messageHandlerDefinitionLookUp.FindHandler(topic) : null;

        // Single call site for the failure status: a registered IValidationStatusMapper wins and
        // honours [ValidationStatus] on the resolved handler type - same contract
        // Benzene.FluentValidation/Benzene.DataAnnotations already honour. Absent a registered
        // mapper, falls back to today's behavior: IDefaultStatuses.ValidationError.
        var status = _validationStatusMapper != null
            ? _validationStatusMapper.GetStatus(messageHandlerDefinition?.HandlerType, messageHandlerDefinition?.RequestType ?? typeof(object), validationResult)
            : _defaultStatuses.ValidationError;

        return _messageHandlerResultSetter.SetResultAsync(context,
            new MessageHandlerResult(topic, messageHandlerDefinition,
                BenzeneResult.Set(status, errors)));
    }
}
