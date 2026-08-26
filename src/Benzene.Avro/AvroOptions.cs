namespace Benzene.Avro;

/// <summary>
/// Configures how <see cref="AvroSerializer"/> obtains an Avro schema for a CLR type. Avro is
/// schema-based (unlike JSON/XML), so every type serialized needs a schema. Two modes are supported
/// and can be mixed:
/// <list type="bullet">
/// <item><b>Reflection (schemaless from the caller's perspective)</b> — the schema is inferred from the
/// CLR type's public read/write properties. On by default (<see cref="UseReflectionSchemas"/>).</item>
/// <item><b>Explicit schema</b> — an Avro schema (<c>.avsc</c> JSON) registered per type via
/// <see cref="RegisterSchema{T}"/>, matching the schema-registry model common in finance/Kafka
/// deployments. An explicit registration always wins over reflection for that type.</item>
/// </list>
/// </summary>
public class AvroOptions
{
    private readonly Dictionary<Type, string> _explicitSchemas = new();

    /// <summary>
    /// Whether to infer an Avro schema by reflection for any type that has no explicit schema
    /// registered. Defaults to <c>true</c>. Set to <c>false</c> to require an explicit schema for
    /// every serialized type (a type with no registration then throws).
    /// </summary>
    public bool UseReflectionSchemas { get; set; } = true;

    /// <summary>
    /// An optional hard cap, in bytes, on any single length-prefixed <c>bytes</c>/<c>string</c> field
    /// the deserializer will accept from an <c>application/avro</c> body, on top of the always-applied
    /// bound that no field may exceed the decoded input size. Avro binary length-prefixes each such
    /// field, so a hostile payload can declare a huge length and drive a large allocation before any
    /// data is read; this rejects it up front. <c>null</c> (the default) applies only the
    /// input-size bound - already enough to stop the classic "tiny input, huge length prefix" OOM.
    /// Set a smaller value to bound it tighter for untrusted producers.
    /// </summary>
    public long? MaxDeserializeBytes { get; set; }

    /// <summary>The default for <see cref="MaxDepth"/> when not overridden.</summary>
    public const int DefaultMaxDepth = 500;

    /// <summary>
    /// The maximum nesting depth <see cref="AvroSerializer"/> will follow on either serialize or
    /// deserialize before throwing <see cref="AvroPayloadTooDeepException"/>. A self-referencing or
    /// very deeply-nested schema/object graph recurses on every extra level of nesting; left unbounded,
    /// a hostile (or just very deep) payload/object graph drives that recursion past the CLR's call
    /// stack and crashes the whole process with an uncatchable <see cref="StackOverflowException"/> -
    /// this bounds it well before that point, mirroring <c>MessagePackSecurity.UntrustedData</c>'s
    /// depth cap. On deserialize this is enforced by <see cref="BoundedBinaryDecoder"/> counting every
    /// union-branch selection and array/map block start it observes for the whole payload; because it
    /// has no visibility into the schema shape, it cannot distinguish "N levels deep" from "N sibling
    /// nullable/collection fields at shallow depth", so a very wide (not deep) message with many such
    /// fields can also trip it - raise this if that is a legitimate shape for your data. Defaults to
    /// <see cref="DefaultMaxDepth"/> (500), far above any reasonable real schema but far below the
    /// depth (tens of thousands of levels) that actually crashes the process.
    /// </summary>
    public int MaxDepth { get; set; } = DefaultMaxDepth;

    /// <summary>Registers an explicit Avro schema (<c>.avsc</c> JSON) for <paramref name="type"/>.</summary>
    /// <param name="type">The CLR type the schema applies to.</param>
    /// <param name="avroSchemaJson">The Avro schema as JSON.</param>
    /// <returns>These options, for chaining.</returns>
    public AvroOptions RegisterSchema(Type type, string avroSchemaJson)
    {
        _explicitSchemas[type] = avroSchemaJson;
        return this;
    }

    /// <summary>Registers an explicit Avro schema (<c>.avsc</c> JSON) for <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The CLR type the schema applies to.</typeparam>
    /// <param name="avroSchemaJson">The Avro schema as JSON.</param>
    /// <returns>These options, for chaining.</returns>
    public AvroOptions RegisterSchema<T>(string avroSchemaJson) => RegisterSchema(typeof(T), avroSchemaJson);

    /// <summary>Gets the explicitly-registered schemas, keyed by CLR type.</summary>
    internal IReadOnlyDictionary<Type, string> ExplicitSchemas => _explicitSchemas;
}
