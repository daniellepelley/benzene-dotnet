using Benzene.Abstractions;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.MessageHandlers.Info;
using Benzene.Abstractions.Messages;
using Benzene.Http.Routing;
using Benzene.Schema.OpenApi.Abstractions;
using Benzene.Schema.OpenApi.Derivation;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Extensions;
using Microsoft.OpenApi.Models;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Schema.OpenApi.EventService
{
    /// <summary>
    /// Derives the Contract Document (contract-document.md) from a service's registrations: handlers
    /// become <c>requests</c>, event declarations (<c>ResponseEventCatalog</c>) become <c>events</c>,
    /// send declarations (<c>MeshOutboundRegistry</c>) become <c>sends</c>, and every payload type's
    /// schema is derived once, by the OpenAPI 3.0 dialect of §4.2, into <c>components</c>.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Reserved <c>benzene:</c> topics are never listed (§1) - their shapes are wire-contracts §7's.</item>
    /// <item>A handler or send whose response type is <see cref="Void"/> has no <c>response</c>: absent
    /// means no payload (§2, §3.2).</item>
    /// <item>No example is synthesized; only an author-supplied one is written.</item>
    /// <item>A (topic, version) declared both as an event and as a send fails <see cref="Build"/> (§3.3),
    /// and two different types claiming one component name fail derivation (§4.1 rule 4) - both before
    /// any message is handled when the document is derived at start-up or build time.</item>
    /// </list>
    /// </remarks>
    public class EventServiceDocumentBuilder :
        IConsumesMessageHandlerDefinitions<EventServiceDocumentBuilder>,
        IConsumesBroadcastEventsDefinitions<EventServiceDocumentBuilder>,
        IConsumesMessageSenderDefinitions<EventServiceDocumentBuilder>,
        IConsumesApplicationInfo<EventServiceDocumentBuilder>,
        IConsumesHttpEndpointDefinitions<EventServiceDocumentBuilder>,
        IConsumesMessageEndpoint<EventServiceDocumentBuilder>,
        IConsumesTransportsInfo<EventServiceDocumentBuilder>,
        IConsumesDeployment<EventServiceDocumentBuilder>,
        IProducesJson,
        IProducesYaml
    {
        private readonly List<Event> _events = new();
        private readonly List<Send> _sends = new();
        private readonly List<RequestResponse> _requests = new();
        private readonly ISchemaBuilder _schemaBuilder = new SchemaBuilder();
        private string _service = string.Empty;
        private string? _serviceVersion;
        private string? _versionScheme;
        private string? _description;
        private string? _messageEndpoint;
        private string[] _transports = Array.Empty<string>();
        private ContractDocumentDeployment? _deployment;
        private readonly List<string> _degraded = new();

        public EventServiceDocumentBuilder(ISchemaBuilder? schemaBuilder = null)
        {
            if (schemaBuilder != null)
            {
                _schemaBuilder = schemaBuilder;
            }
        }

        /// <summary>Builds the document.</summary>
        /// <exception cref="InvalidOperationException">A (topic, version) is declared both as an event and as a send.</exception>
        public EventServiceDocument Build()
        {
            var both = _events
                .Select(x => (x.Topic, x.Version))
                .Intersect(_sends.Select(x => (x.Topic, x.Version)))
                .ToArray();
            if (both.Length > 0)
            {
                throw new InvalidOperationException(
                    "A topic is declared both as an event this service publishes and as a send to a topic another party defines: " +
                    string.Join(", ", both.Select(x => string.IsNullOrEmpty(x.Version) ? $"'{x.Topic}'" : $"'{x.Topic}' version '{x.Version}'")) +
                    ". Declare each (topic, version) once - as an event when this service owns its contract, as a send when another party does (contract-document.md §3.3).");
            }

            var components = new OpenApiComponents
            {
                Schemas = _schemaBuilder.Build()
            };

            return new EventServiceDocument(
                _requests.ToArray(),
                _events.ToArray(),
                _sends.ToArray(),
                components
            )
            {
                Service = _service,
                ServiceVersion = _serviceVersion,
                VersionScheme = _versionScheme,
                Description = _description,
                MessageEndpoint = _messageEndpoint,
                Transports = _transports,
                Runtime = _deployment?.Runtime,
                Placement = _deployment?.Placement,
                Profile = _deployment?.Profile,
                Degraded = _degraded.ToArray()
            };
        }

        /// <summary>
        /// Stamps the deployment-side fields (contract-document.md §1: <c>runtime</c>, <c>placement</c>,
        /// <c>profile</c>) - outside both hashes, so they never change them.
        /// </summary>
        public EventServiceDocumentBuilder AddDeployment(ContractDocumentDeployment deployment)
        {
            _deployment = deployment;
            return this;
        }

        /// <summary>
        /// Names a member (<see cref="ContractDocumentMembers"/>) this producer could not determine
        /// (contract-document.md §1 <c>degraded</c>). The member's list is emitted empty: a degraded
        /// member asserts nothing, where an empty list without <c>degraded</c> asserts "none".
        /// </summary>
        public EventServiceDocumentBuilder AddDegraded(string member)
        {
            if (!_degraded.Contains(member))
            {
                _degraded.Add(member);
            }

            return this;
        }

        /// <summary>
        /// Advertises the service's BenzeneMessage-over-HTTP endpoint as the document's top-level
        /// <c>messageEndpoint</c> field, so spec consumers (e.g. the Spec UI's try-it panel) can
        /// discover where to POST message envelopes.
        /// </summary>
        public EventServiceDocumentBuilder AddMessageEndpoint(string path)
        {
            _messageEndpoint = path;
            return this;
        }

        /// <summary>
        /// Advertises every transport this host is wired to receive messages over as the
        /// document's top-level <c>transports</c> field (see <see cref="EventServiceDocument.Transports"/>).
        /// </summary>
        public EventServiceDocumentBuilder AddTransportsInfo(ITransportsInfo transportsInfo)
        {
            _transports = transportsInfo.Transports;
            return this;
        }

        public Dictionary<string, OpenApiSchema> GetSchemas()
        {
            return _schemaBuilder.Build();
        }

        /// <summary>
        /// The service's identity, from the single source <see cref="IApplicationInfo"/>:
        /// <c>service</c> = <see cref="IApplicationInfo.Name"/>, <c>serviceVersion</c> =
        /// <see cref="IApplicationInfo.Version"/>, <c>versionScheme</c> =
        /// <see cref="IApplicationInfo.VersionScheme"/>, <c>description</c> =
        /// <see cref="IApplicationInfo.Description"/> (empty values are omitted).
        /// </summary>
        public EventServiceDocumentBuilder AddApplicationInfo(IApplicationInfo applicationInfo)
        {
            return AddService(applicationInfo.Name, applicationInfo.Version, applicationInfo.Description,
                applicationInfo.VersionScheme);
        }

        /// <summary>Sets the service's identity fields.</summary>
        public EventServiceDocumentBuilder AddService(string service, string? serviceVersion = null,
            string? description = null, string? versionScheme = null)
        {
            _service = service ?? string.Empty;
            _serviceVersion = NullIfEmpty(serviceVersion);
            _description = NullIfEmpty(description);
            _versionScheme = NullIfEmpty(versionScheme);
            return this;
        }

        public EventServiceDocumentBuilder AddMessageHandlerDefinitions(IMessageHandlerDefinition[] messageHandlerDefinitions)
        {
            foreach (var messageHandlerDefinition in messageHandlerDefinitions)
            {
                AddMessageHandlerDefinition(messageHandlerDefinition);
            }
            return this;
        }

        public void AddMessageHandlerDefinition(IMessageHandlerDefinition messageHandlerDefinition)
        {
            var topic = messageHandlerDefinition.Topic.Id;
            if (BenzeneTopic.IsReserved(topic))
            {
                return;
            }

            if (!_requests.Any(x => x.Topic == topic && x.Version == messageHandlerDefinition.Topic.Version))
            {
                _requests.Add(new RequestResponse
                {
                    Topic = topic,
                    Version = messageHandlerDefinition.Topic.Version ?? string.Empty,
                    // A request is REQUIRED; a handler that takes no payload (Void) accepts any: {}.
                    Request = AddSchema(topic, messageHandlerDefinition.RequestType) ?? new OpenApiSchema(),
                    Response = AddSchema(topic, messageHandlerDefinition.ResponseType)
                });
            }
        }

        /// <summary>
        /// Adds event declarations - topics this service publishes and defines (contract-document.md
        /// §3.1), e.g. <c>ResponseEventCatalog</c>'s.
        /// </summary>
        public EventServiceDocumentBuilder AddBroadcastEventDefinitions(IMessageDefinition[] messageDefinitions)
        {
            foreach (var messageDefinition in messageDefinitions)
            {
                AddBroadcastEventDefinition(messageDefinition);
            }
            return this;
        }

        /// <summary>Adds one event declaration (§3.1).</summary>
        public EventServiceDocumentBuilder AddBroadcastEventDefinition(IMessageDefinition messageDefinition)
        {
            var topic = messageDefinition.Topic.Id;
            if (BenzeneTopic.IsReserved(topic))
            {
                return this;
            }

            var version = messageDefinition.Topic.Version ?? string.Empty;
            if (_events.Any(x => x.Topic == topic && x.Version == version))
            {
                return this;
            }

            _events.Add(new Event(topic, AddSchema(topic, messageDefinition.RequestType) ?? new OpenApiSchema())
            {
                Version = version
            });
            return this;
        }

        /// <summary>
        /// Adds send declarations - messages this service sends to topics another party defines
        /// (contract-document.md §3.2), e.g. <c>MeshOutboundRegistry</c>'s. A definition with a
        /// <see cref="Void"/> (or no) response type is one-way: its <c>response</c> is absent.
        /// </summary>
        public EventServiceDocumentBuilder AddMessageSenderDefinitions(IMessageDefinition[] messageDefinitions)
        {
            foreach (var messageDefinition in messageDefinitions)
            {
                AddMessageSenderDefinition(messageDefinition);
            }

            return this;
        }

        /// <summary>Adds one send declaration (§3.2).</summary>
        public EventServiceDocumentBuilder AddMessageSenderDefinition(IMessageDefinition messageDefinition)
        {
            var topic = messageDefinition.Topic.Id;
            if (BenzeneTopic.IsReserved(topic))
            {
                return this;
            }

            var version = messageDefinition.Topic.Version ?? string.Empty;
            if (_sends.Any(x => x.Topic == topic && x.Version == version))
            {
                return this;
            }

            var responseType = (messageDefinition as IRequestResponseMessageDefinition)?.ResponseType;
            _sends.Add(new Send(topic, AddSchema(topic, messageDefinition.RequestType) ?? new OpenApiSchema(),
                AddSchema(topic, responseType))
            {
                Version = version
            });
            return this;
        }

        public EventServiceDocumentBuilder AddEvent(string topic, Type type)
        {
            _events.Add(new Event
            (
                topic,
                AddSchema(topic, type) ?? new OpenApiSchema()
            ));
            return this;
        }

        public EventServiceDocumentBuilder AddEventDefinition(string topic, string payloadName, OpenApiSchema schema)
        {
            _events.Add(new Event
            (
                topic,
                _schemaBuilder.AddSchema(payloadName, schema)
            ));
            return this;
        }

        public OpenApiSchema AddSchema(string key, OpenApiSchema schema)
        {
            return _schemaBuilder.AddSchema(key, schema);
        }

        public string GenerateJson()
        {
            return Build().SerializeAsJson(OpenApiSpecVersion.OpenApi3_0);
        }

        public string GenerateYaml()
        {
            return Build().SerializeAsYaml(OpenApiSpecVersion.OpenApi3_0);
        }

        public EventServiceDocumentBuilder AddHttpEndpointDefinitions(IHttpEndpointDefinition[] httpEndpointDefinitions,
            IMessageHandlerDefinition[] messageHandlerDefinitions)
        {
            AddMessageHandlerDefinitions(messageHandlerDefinitions);

            foreach (var group in httpEndpointDefinitions.GroupBy(x => x.Topic))
            {
                foreach (var request in _requests.Where(x => x.Topic == group.Key))
                {
                    request.HttpMappings = group.Select(x =>
                        new HttpMapping
                        {
                            Method = x.Method,
                            Path = x.Path
                        }).ToArray();
                }
            }

            return this;
        }

        /// <summary>
        /// The schema of <paramref name="type"/>, or null for no payload (<see cref="Void"/> or none).
        /// A component-name collision is re-thrown naming the topics that reach either type.
        /// </summary>
        private OpenApiSchema? AddSchema(string topic, Type? type)
        {
            if (type == null || type == typeof(Void))
            {
                return null;
            }

            try
            {
                return _schemaBuilder.AddSchema(type);
            }
            catch (ComponentNameCollisionException collision)
            {
                throw collision.WithTopics(TopicsReaching(collision.Name).Append(topic).Distinct().ToArray());
            }
        }

        private IEnumerable<string> TopicsReaching(string componentName)
        {
            var schemas = _schemaBuilder.Build();
            bool Reaches(params OpenApiSchema?[] roots) =>
                SchemaReach.Reachable(schemas, roots).Contains(componentName);

            return _requests.Where(x => Reaches(x.Request, x.Response)).Select(x => x.Topic)
                .Concat(_events.Where(x => Reaches(x.Message)).Select(x => x.Topic))
                .Concat(_sends.Where(x => Reaches(x.Request, x.Response)).Select(x => x.Topic));
        }

        private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
