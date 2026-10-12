namespace Benzene.Schema.OpenApi.EventService;

/// <summary>
/// A contract document declares a <c>schemaDialect</c> this port does not know, or declares none
/// (contract-document.md §1: a consumer MUST reject it rather than guess at its schemas; a document
/// without <c>schemaDialect</c> has an unknown dialect).
/// </summary>
public class UnknownSchemaDialectException : FormatException
{
    /// <summary>The kind the conformance fixtures name this failure by.</summary>
    public const string Kind = "unknown-schema-dialect";

    /// <summary>Initializes the exception.</summary>
    /// <param name="schemaDialect">The declared dialect (null when absent).</param>
    public UnknownSchemaDialectException(string? schemaDialect)
        : base(schemaDialect == null
            ? $"{Kind}: the contract document has no schemaDialect, so its dialect is unknown; this reader knows only " +
              $"'{EventServiceDocument.SchemaDialectOpenApi30}'. A legacy (spec-0.2.0) document must be regenerated with the " +
              "current producer (or read for migration with EventServiceDocumentDeserializer.DeserializeAllowingLegacyShape)."
            : $"{Kind}: the contract document's schemaDialect is '{schemaDialect}'; this reader knows only " +
              $"'{EventServiceDocument.SchemaDialectOpenApi30}'.")
    {
        SchemaDialect = schemaDialect;
    }

    /// <summary>The declared dialect.</summary>
    public string? SchemaDialect { get; }
}
