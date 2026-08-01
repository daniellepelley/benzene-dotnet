using System;
using System.Collections.Generic;
using System.Linq;
using Benzene.Abstractions.DI;
using Benzene.Core.Versioning.CasterBuilder;
using Benzene.Core.Versioning.Schemas;

namespace Benzene.Core.Versioning;

/// <summary>
/// One-call setup for transparent payload-version casting (docs/specification/versioning.md §4,
/// mechanism B). This is the recommended entry point; it wraps the lower-level
/// <see cref="SchemaCasterExtensions.RegisterSchemaCastDefinitions"/> /
/// <see cref="SchemaCasterExtensions.RegisterPayloadSchemaVersions"/> /
/// <see cref="PayloadVersionCastingExtensions.UsePayloadVersionCasting{TContext}"/> trio and closes the
/// footguns of wiring them by hand:
/// <list type="bullet">
/// <item>The caster graph is <b>validated eagerly</b>, at registration time - a missing conversion path
/// throws here, at startup, not on the first message in production.</item>
/// <item>The version list and both cast directions are declared <b>once</b>: <c>FromSchemas</c>/
/// <c>ToSchemas</c> are derived from the declared versions, and a downcast for each upcast is synthesized
/// (drop the added field) unless you supply one, so there is nothing to keep in sync.</item>
/// <item>The casting decorators for the transports you name (<see cref="PayloadVersioningBuilder.ForContext{TContext}"/>)
/// are registered here too, so there is no separate, order-sensitive <c>UsePayloadVersionCasting</c> call
/// to forget or mis-place. (This works in <c>ConfigureServices</c> because the transports register their
/// default request mapper with <c>TryAdd</c>, so this registration wins.)</item>
/// </list>
/// </summary>
public static class PayloadVersioningExtensions
{
    /// <summary>
    /// Registers transparent payload-version casting from a single fluent definition. See
    /// <see cref="PayloadVersioningExtensions"/> for what this does that hand-wiring does not.
    /// </summary>
    /// <param name="services">The service container to register into.</param>
    /// <param name="configure">Declares the transports to cast for and, per topic, the versions and casters.</param>
    /// <returns>The same container, for chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown at registration time if no transport context is declared, if a caster references an
    /// undeclared version, or if the declared versions cannot all be reached by the registered casters
    /// (a missing conversion path) - i.e. misconfiguration fails the build, not the first message.
    /// </exception>
    public static IBenzeneServiceContainer AddPayloadVersioning(this IBenzeneServiceContainer services,
        Action<PayloadVersioningBuilder> configure)
    {
        var builder = new PayloadVersioningBuilder();
        configure(builder);
        builder.Apply(services);
        return services;
    }
}

/// <summary>
/// Fluent surface for <see cref="PayloadVersioningExtensions.AddPayloadVersioning"/>: name the transport
/// contexts to enable casting for, and declare each versioned topic.
/// </summary>
public class PayloadVersioningBuilder
{
    private readonly List<Type> _contexts = new();
    private readonly List<TopicVersioningBuilder> _topics = new();

    /// <summary>
    /// Enable casting for a transport's context type (e.g. <c>BenzeneMessageContext</c>,
    /// <c>ApiGatewayContext</c>, <c>SqsMessageContext</c>). Call once per transport the service hosts.
    /// </summary>
    public PayloadVersioningBuilder ForContext<TContext>() where TContext : class
    {
        _contexts.Add(typeof(TContext));
        return this;
    }

    /// <summary>Declare a versioned topic and its casters.</summary>
    public PayloadVersioningBuilder Topic(string topic, Action<TopicVersioningBuilder> configure)
    {
        var topicBuilder = new TopicVersioningBuilder(topic);
        configure(topicBuilder);
        _topics.Add(topicBuilder);
        return this;
    }

    internal void Apply(IBenzeneServiceContainer services)
    {
        if (_topics.Count > 0 && _contexts.Count == 0)
        {
            throw new InvalidOperationException(
                "AddPayloadVersioning declares topics but no transport: call ForContext<TContext>() for each " +
                "transport that should cast (e.g. .ForContext<BenzeneMessageContext>()), or casting never runs.");
        }

        var castersBuilder = new SchemaCastersBuilder();
        var payloadSchemaVersions = new List<PayloadSchemaVersions>();

        foreach (var topic in _topics)
        {
            topic.Build(castersBuilder);
            payloadSchemaVersions.Add(topic.PayloadSchemaVersions());
        }

        var casters = castersBuilder.Build();

        // Eager validation: expand (compose the multi-step chains) NOW. A missing path throws here, at
        // registration/startup - the whole point of this entry point over the lazy hand-wired singleton.
        var expanded = new SchemaCastDefinitionsExpander().Expand(casters, payloadSchemaVersions.ToArray());

        foreach (var caster in casters)
        {
            _ = services.AddSingleton(caster);
        }

        var schemaCasters = new SchemaCasters(expanded);
        _ = services.AddSingleton<ISchemaCasters>(schemaCasters);

        foreach (var context in _contexts)
        {
            EnableCastingFor(services, context);
        }
    }

    // UsePayloadVersionCasting<TContext>() is generic on the context; the contexts are captured as Type,
    // so close the generic method over each and invoke it. Kept to this one reflection call so the rest
    // of the wiring stays strongly typed.
    private static void EnableCastingFor(IBenzeneServiceContainer services, Type contextType)
    {
        var method = typeof(PayloadVersionCastingExtensions)
            .GetMethod(nameof(PayloadVersionCastingExtensions.UsePayloadVersionCasting))!
            .MakeGenericMethod(contextType);
        _ = method.Invoke(null, new object[] { services });
    }
}

/// <summary>
/// Declares one topic's versions and the casters between them. Versions are declared once (name ↔ CLR
/// type); casters reference the CLR types and the schema names are looked up from the declarations.
/// </summary>
public class TopicVersioningBuilder
{
    private readonly string _topic;
    private readonly Dictionary<Type, string> _versionNamesByType = new();
    private readonly List<Action<SchemaCastersBuilder>> _forwardCasters = new();
    private readonly List<(Type From, Type To, Action<SchemaCastersBuilder> Apply)> _reverseCasters = new();
    private readonly HashSet<(Type From, Type To)> _explicitDowncastPairs = new();

    internal TopicVersioningBuilder(string topic)
    {
        _topic = topic;
    }

    /// <summary>Declare a payload version: its wire name (e.g. "v1") and the CLR type that models it.</summary>
    public TopicVersioningBuilder Version<T>(string name)
    {
        _versionNamesByType[typeof(T)] = name;
        return this;
    }

    /// <summary>
    /// Register an upcaster from an older version's type to a newer one's. The optional
    /// <paramref name="configure"/> seeds fields the newer version introduced (e.g.
    /// <c>f =&gt; f.RegisterInitValue(o =&gt; o.WarehouseId, "wh-main")</c>). A matching downcaster is
    /// synthesized automatically (dropping the added field) unless you declare one with
    /// <see cref="Downcast{TFrom,TTo}"/>.
    /// </summary>
    public TopicVersioningBuilder Upcast<TFrom, TTo>(Action<CasterFactory<TFrom, TTo>>? configure = null)
    {
        _forwardCasters.Add(b => Add<TFrom, TTo>(b, configure));
        // Candidate reverse (auto property-map, which drops the added field); applied at Build only if no
        // explicit downcast covers the (TTo -> TFrom) pair.
        _reverseCasters.Add((typeof(TFrom), typeof(TTo), b => Add<TTo, TFrom>(b, null)));
        return this;
    }

    /// <summary>
    /// Register an explicit downcaster (older &lt;- newer), for the rare case where the older version
    /// needs a field re-derived rather than simply dropped. Not usually needed - a plain field-drop
    /// downcast is synthesized from each <see cref="Upcast{TFrom,TTo}"/>.
    /// </summary>
    public TopicVersioningBuilder Downcast<TFrom, TTo>(Action<CasterFactory<TFrom, TTo>>? configure = null)
    {
        _forwardCasters.Add(b => Add<TFrom, TTo>(b, configure));
        _ = _explicitDowncastPairs.Add((typeof(TFrom), typeof(TTo)));
        return this;
    }

    private void Add<TFrom, TTo>(SchemaCastersBuilder builder, Action<CasterFactory<TFrom, TTo>>? configure)
    {
        var fromSchema = SchemaNameFor(typeof(TFrom));
        var toSchema = SchemaNameFor(typeof(TTo));
        if (configure == null)
        {
            _ = builder.Add<TFrom, TTo>(_topic, fromSchema, toSchema);
        }
        else
        {
            _ = builder.Add<TFrom, TTo>(_topic, fromSchema, toSchema, configure);
        }
    }

    private string SchemaNameFor(Type type)
    {
        if (!_versionNamesByType.TryGetValue(type, out var name))
        {
            throw new InvalidOperationException(
                $"Payload versioning for topic '{_topic}' uses type '{type.Name}' in a caster, but no version " +
                $"name is declared for it: add .Version<{type.Name}>(\"...\") to this topic.");
        }

        return name;
    }

    internal void Build(SchemaCastersBuilder builder)
    {
        foreach (var caster in _forwardCasters)
        {
            caster(builder);
        }

        // Synthesize a field-drop downcaster for each upcast that has no explicit downcast.
        foreach (var (from, to, apply) in _reverseCasters)
        {
            if (!_explicitDowncastPairs.Contains((to, from)))
            {
                apply(builder);
            }
        }
    }

    internal PayloadSchemaVersions PayloadSchemaVersions()
    {
        var versions = _versionNamesByType.Values.Distinct().ToArray();
        return new PayloadSchemaVersions
        {
            Topic = _topic,
            // Every declared version may arrive, and a response may be requested in any of them; the
            // expander composes every needed (from, to) pair from the adjacent casters.
            FromSchemas = versions,
            ToSchemas = versions
        };
    }
}
