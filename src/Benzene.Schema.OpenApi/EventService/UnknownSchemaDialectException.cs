namespace Benzene.Schema.OpenApi.EventService;

/// <summary>
/// A contract document declares a <c>schemaDialect</c> this port does not know (contract-document.md
/// §1: a consumer MUST reject it rather than guess at its schemas).
/// </summary>
public class UnknownSchemaDialectException : FormatException
{
    /// <summary>The kind the conformance fixtures name this failure by.</summary>
    public const string Kind = "unknown-schema-dialect";

    /// <summary>Initializes the exception.</summary>
    /// <param name="schemaDialect">The declared dialect (null when absent).</param>
    public UnknownSchemaDialectException(string? schemaDialect)
        : base($"{Kind}: the contract document's schemaDialect is '{schemaDialect ?? "(absent)"}'; this reader knows only " +
               $"'{EventServiceDocument.SchemaDialectOpenApi30}'.")
    {
        SchemaDialect = schemaDialect;
    }

    /// <summary>The declared dialect.</summary>
    public string? SchemaDialect { get; }
}
