namespace Benzene.SchemaRegistry.Core;

/// <summary>
/// A schema to register or check: the subject it belongs to (the registry's namespace key, by
/// convention <c>&lt;topic&gt;-value</c> or <c>&lt;topic&gt;-key</c> for Kafka), the schema text, and
/// its format.
/// </summary>
public class SchemaDefinition
{
    /// <summary>Initializes a schema definition.</summary>
    /// <param name="subject">The registry subject. Must be non-null and non-empty/whitespace - it is
    /// used as a dictionary key throughout the registry, where a null value fails with an opaque
    /// <see cref="ArgumentNullException"/> deep inside the caller instead of at construction.</param>
    /// <param name="schema">The schema text (e.g. an Avro <c>.avsc</c> document). Must be non-null and
    /// non-empty/whitespace.</param>
    /// <param name="format">The schema format. Defaults to <see cref="SchemaFormat.Avro"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="subject"/> or <paramref name="schema"/> is
    /// null, empty, or whitespace.</exception>
    public SchemaDefinition(string subject, string schema, SchemaFormat format = SchemaFormat.Avro)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            throw new ArgumentException("Subject must not be null, empty, or whitespace.", nameof(subject));
        }

        if (string.IsNullOrWhiteSpace(schema))
        {
            throw new ArgumentException("Schema must not be null, empty, or whitespace.", nameof(schema));
        }

        Subject = subject;
        Schema = schema;
        Format = format;
    }

    /// <summary>Gets the registry subject this schema belongs to.</summary>
    public string Subject { get; }

    /// <summary>Gets the schema text.</summary>
    public string Schema { get; }

    /// <summary>Gets the schema format.</summary>
    public SchemaFormat Format { get; }
}
