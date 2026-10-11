using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.MessageHandlers.Info;
using Benzene.Abstractions.Messages;
using Benzene.Abstractions.Validation;
using Benzene.Http.BenzeneMessage;
using Benzene.Http.Routing;
using Benzene.Schema.OpenApi.Abstractions;
using Benzene.Schema.OpenApi.AsyncApi;
using Benzene.Schema.OpenApi.EventService;
using Benzene.Schema.OpenApi.OpenApi;

namespace Benzene.Schema.OpenApi;

/// <summary>
/// Builds the document a <c>benzene:spec</c> request names (wire-contracts §7): <c>type</c> is
/// <see cref="BenzeneType"/> (the contract document, the default), <see cref="OpenApiType"/> or
/// <see cref="AsyncApiType"/>; <c>format</c> is <see cref="JsonFormat"/> (the default) or
/// <see cref="YamlFormat"/> where the type's builder produces it. Values are compared case-insensitively;
/// an absent or empty value takes the default. A type or format this service does not support is never
/// answered with a different document - <see cref="IsSupported"/> is false and the handler answers
/// <c>not-implemented</c>.
/// </summary>
public class SpecBuilder
{
    /// <summary>The contract document's type, and the default.</summary>
    public const string BenzeneType = "benzene";

    /// <summary>OpenAPI 3.0, a port extension.</summary>
    public const string OpenApiType = "openapi";

    /// <summary>AsyncAPI 3.0, a port extension.</summary>
    public const string AsyncApiType = "asyncapi";

    /// <summary>JSON, the default format.</summary>
    public const string JsonFormat = "json";

    /// <summary>YAML, a port extension, for the builders that produce it.</summary>
    public const string YamlFormat = "yaml";

    private static readonly IReadOnlyDictionary<string, Type> BuilderTypes = new Dictionary<string, Type>
    {
        [BenzeneType] = typeof(EventServiceDocumentBuilder),
        [OpenApiType] = typeof(OpenApiDocumentBuilder),
        [AsyncApiType] = typeof(AsyncApiDocumentBuilder),
    };

    /// <summary>
    /// The request with its defaults applied and its values lower-cased: an absent request, an empty
    /// one, and <c>{}</c> all mean <c>benzene</c>/<c>json</c>.
    /// </summary>
    /// <param name="request">The request as received; null for an absent or <c>null</c> body.</param>
    /// <returns>A new, normalized request.</returns>
    public static SpecRequest Normalize(SpecRequest? request)
        => new(Normalize(request?.Type, BenzeneType), Normalize(request?.Format, JsonFormat));

    /// <summary>Whether this service can answer <paramref name="request"/> (after <see cref="Normalize(SpecRequest?)"/>).</summary>
    /// <param name="request">The request.</param>
    /// <returns>True when the type is known and its builder produces the format.</returns>
    public static bool IsSupported(SpecRequest? request)
    {
        var normalized = Normalize(request);
        if (!BuilderTypes.TryGetValue(normalized.Type, out var builderType))
        {
            return false;
        }

        return normalized.Format switch
        {
            JsonFormat => typeof(IProducesJson).IsAssignableFrom(builderType),
            YamlFormat => typeof(IProducesYaml).IsAssignableFrom(builderType),
            _ => false,
        };
    }

    /// <summary>Builds the document <paramref name="specRequest"/> names.</summary>
    /// <param name="resolver">Resolves the registrations the document is derived from.</param>
    /// <param name="specRequest">The request; normalized first.</param>
    /// <returns>The document.</returns>
    /// <exception cref="NotSupportedException">The type or format is not supported (see <see cref="IsSupported"/>).</exception>
    public string CreateSpec(IServiceResolver resolver, SpecRequest specRequest)
    {
        if (!TryCreateSpec(resolver, specRequest, out var document))
        {
            var normalized = Normalize(specRequest);
            throw new NotSupportedException(
                $"Spec type '{normalized.Type}' in format '{normalized.Format}' is not supported.");
        }

        return document;
    }

    /// <summary>Builds the document <paramref name="specRequest"/> names, when it is supported.</summary>
    /// <param name="resolver">Resolves the registrations the document is derived from.</param>
    /// <param name="specRequest">The request; normalized first.</param>
    /// <param name="document">The document, or empty when unsupported.</param>
    /// <returns>False when the type or format is not supported.</returns>
    public bool TryCreateSpec(IServiceResolver resolver, SpecRequest? specRequest, out string document)
    {
        document = string.Empty;
        var normalized = Normalize(specRequest);
        if (!IsSupported(normalized))
        {
            return false;
        }

        var builder = CreateBuilder(resolver, normalized.Type);
        document = normalized.Format == YamlFormat
            ? ((IProducesYaml)builder).GenerateYaml()
            : ((IProducesJson)builder).GenerateJson();
        return true;
    }

    /// <summary>
    /// Creates the document builder for <paramref name="type"/> (case-insensitive; null or empty is
    /// <see cref="BenzeneType"/>), fed with this service's registrations.
    /// </summary>
    /// <param name="resolver">Resolves the registrations the document is derived from.</param>
    /// <param name="type">The spec type.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="NotSupportedException">The type is not one this service builds.</exception>
    public object CreateBuilder(IServiceResolver resolver, string? type)
    {
        return Normalize(type, BenzeneType) switch
        {
            OpenApiType => CreateSpec(resolver, new OpenApiDocumentBuilder(CreateSchemaBuilder(resolver))),
            AsyncApiType => CreateSpec(resolver, new AsyncApiDocumentBuilder(CreateSchemaBuilder(resolver),
                resolver.TryGetService<AsyncApiSpecOptions>()?.ResponseTopicSuffix)),
            BenzeneType => CreateSpec(resolver, new EventServiceDocumentBuilder(CreateSchemaBuilder(resolver))),
            var other => throw new NotSupportedException($"Spec type '{other}' is not supported.")
        };
    }

    private static string Normalize(string? value, string defaultValue)
        => string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim().ToLowerInvariant();

    
    public TBuilder CreateSpec<TBuilder>(IServiceResolver resolver, TBuilder builder)
    {
        if (builder is IConsumesApplicationInfo<TBuilder> consumesApplicationInfo)
        {
            var applicationInfo = resolver.TryGetService<IApplicationInfo>();
            if (applicationInfo != null)
            {
                consumesApplicationInfo.AddApplicationInfo(applicationInfo);
            }
        }
        if (builder is IConsumesMessageHandlerDefinitions<TBuilder> consumesMessageHandlerDefinitions)
        {
            var messageHandlersFinder = resolver.TryGetService<IMessageHandlersFinder>();
            if (messageHandlersFinder != null)
            {
                consumesMessageHandlerDefinitions.AddMessageHandlerDefinitions(messageHandlersFinder.FindDefinitions());
            }
        }
        if (builder is IConsumesHttpEndpointDefinitions<TBuilder> consumesHttpEndpointDefinitions)
        {
            var messageHandlersFinder = resolver.TryGetService<IMessageHandlersFinder>();
            var httpEndpointFinder = resolver.TryGetService<IHttpEndpointFinder>();
            if (messageHandlersFinder != null && httpEndpointFinder != null)
            {
                consumesHttpEndpointDefinitions.AddHttpEndpointDefinitions(httpEndpointFinder.FindDefinitions(), messageHandlersFinder.FindDefinitions());
            }
        }
        if (builder is IConsumesBroadcastEventsDefinitions<TBuilder> consumesBroadcastEventsDefinitions)
        {
            var messageFinder = resolver.TryGetService<IMessageDefinitionFinder<IMessageDefinition>>();
            if (messageFinder != null)
            {
                consumesBroadcastEventsDefinitions.AddBroadcastEventDefinitions(messageFinder.FindDefinitions());
            }
        }
        if (builder is IConsumesMessageSenderDefinitions<TBuilder> consumesMessageSenderDefinitions)
        {
            // Send declarations (contract-document.md §3.2) - a MeshOutboundRegistry registered
            // with AddSendDeclarations / the cloud service's WithSends, and each generated client's own
            // (its Add{Service}ServiceClient registers the topics it calls). Every registered finder
            // contributes, not only the last one registered; a (topic, version) declared by more than
            // one finder is listed once - the document builder keeps the first declaration and skips
            // the rest (EventServiceDocumentBuilder.AddMessageSenderDefinition).
            var sends = resolver.GetServices<IMessageDefinitionFinder<IMessageSenderDefinition>>()
                .SelectMany(finder => finder.FindDefinitions())
                .Cast<IMessageDefinition>()
                .ToArray();
            if (sends.Length > 0)
            {
                consumesMessageSenderDefinitions.AddMessageSenderDefinitions(sends);
            }
        }
        if (builder is IConsumesMessageEndpoint<TBuilder> consumesMessageEndpoint)
        {
            var messageEndpointInfo = resolver.TryGetService<IBenzeneMessageHttpEndpointInfo>();
            if (messageEndpointInfo != null)
            {
                consumesMessageEndpoint.AddMessageEndpoint(messageEndpointInfo.Path);
            }
        }
        if (builder is IConsumesTransportsInfo<TBuilder> consumesTransportsInfo)
        {
            var transportsInfo = resolver.TryGetService<ITransportsInfo>();
            if (transportsInfo != null)
            {
                consumesTransportsInfo.AddTransportsInfo(transportsInfo);
            }
        }
        if (builder is IConsumesDeployment<TBuilder> consumesDeployment)
        {
            // The deployment-side fields (contract-document.md §1) - registered by the cloud service, or
            // SetContractDocumentDeployment - so the served document and the mesh register body, which are
            // the same document (mesh.md §2), both carry them.
            var deployment = resolver.TryGetService<ContractDocumentDeployment>();
            if (deployment != null)
            {
                consumesDeployment.AddDeployment(deployment);
            }

            // No handler registry: requests could not be determined, which is not "serves nothing".
            if (resolver.TryGetService<IMessageHandlersFinder>() == null)
            {
                consumesDeployment.AddDegraded(ContractDocumentMembers.Requests);
            }
        }

        return builder;
    }

    private ISchemaBuilder CreateSchemaBuilder(IServiceResolver resolver)
    {
        // BYO seam: a registered ISchemaBuilder (e.g. SuppliedSchemaBuilder over hand-authored
        // schema documents) replaces the default reflection builder. Register custom builders
        // transient/scoped - a schema builder accumulates one document's components catalogue,
        // so a singleton would leak schemas across spec builds. Without one, reflection
        // generation honors any registered SchemaGenerationOptions (inheritance/polymorphism).
        var schemaBuilder = resolver.TryGetService<ISchemaBuilder>()
                            ?? new SchemaBuilder(resolver.TryGetService<SchemaGenerationOptions>());

        var validationSchemaBuilder = resolver.TryGetService<IValidationSchemaBuilder>();
        if (validationSchemaBuilder != null)
        {
            return new OpenApiValidationSchemaBuilder(schemaBuilder, validationSchemaBuilder);
        }

        return schemaBuilder;
    }
}
