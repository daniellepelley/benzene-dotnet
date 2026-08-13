using Benzene.Abstractions.Results;
using Json.Schema;

namespace Benzene.JsonSchema;

/// <summary>
/// Formats a JSON Schema evaluation failure into the same failure shape the other validation
/// libraries (<c>Benzene.FluentValidation</c>, <c>Benzene.DataAnnotations</c>) produce: one
/// <see cref="BenzeneError"/> per failed keyword, served as the <c>ValidationError</c> result's
/// errors.
/// </summary>
public static class JsonSchemaValidationErrors
{
    /// <summary>The message used when the request body is absent entirely.</summary>
    public const string MissingBody = "Request body is missing";

    /// <summary>The message used when the request body is not parseable JSON.</summary>
    public const string MalformedBody = "Request body is not valid JSON";

    /// <summary>
    /// Flattens an <see cref="EvaluationResults"/> (evaluated with <see cref="OutputFormat.List"/>)
    /// into one <see cref="BenzeneError"/> per failed keyword. Unlike the other validation
    /// integrations, the JSON Pointer of the failing value is already in wire form here, so it
    /// travels as <see cref="BenzeneError.Field"/> rather than being prefixed into the message text
    /// (a root-level failure carries no <c>Field</c>); the failed keyword (e.g. <c>maxLength</c>,
    /// <c>required</c>) travels as <see cref="BenzeneError.Code"/>.
    /// </summary>
    /// <param name="results">The failed evaluation.</param>
    /// <returns>De-duplicated, ordered errors; a generic fallback if the evaluation carries no detail.</returns>
    public static IReadOnlyList<BenzeneError> Format(EvaluationResults results)
    {
        var errors = results.Details
            .Where(x => !x.IsValid && x.Errors is { Count: > 0 })
            .SelectMany(x => x.Errors!.Select(keywordAndMessage =>
                new BenzeneError(keywordAndMessage.Value, NormalizeField(x.InstanceLocation.ToString()), keywordAndMessage.Key)))
            .Distinct()
            .ToArray();

        return errors.Length > 0
            ? errors
            : new[] { new BenzeneError("Request body does not match the schema") };
    }

    private static string? NormalizeField(string instanceLocation)
    {
        return string.IsNullOrEmpty(instanceLocation) ? null : instanceLocation;
    }
}
