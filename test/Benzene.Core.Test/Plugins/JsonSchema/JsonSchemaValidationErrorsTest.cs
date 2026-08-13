using System.Collections.Generic;
using System.Text.Json;
using Benzene.Abstractions.Results;
using Benzene.JsonSchema;
using Json.Schema;
using Xunit;

namespace Benzene.Test.Plugins.JsonSchema;

public class JsonSchemaValidationErrorsTest
{
    private static readonly Json.Schema.JsonSchema Schema = Json.Schema.JsonSchema.FromText(/*lang=json*/ """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "name": { "type": "string", "maxLength": 5 },
            "lines": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": { "sku": { "type": "string" } },
                "required": [ "sku" ]
              }
            }
          },
          "required": [ "name" ]
        }
        """);

    private static IReadOnlyList<BenzeneError> Evaluate(string json)
    {
        using var document = JsonDocument.Parse(json);
        var results = Schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.False(results.IsValid);
        return JsonSchemaValidationErrors.Format(results);
    }

    [Fact]
    public void Format_PropertyFailure_IsScopedToTheInstancePointer()
    {
        var errors = Evaluate("""{ "name": "far-too-long-a-name" }""");

        var error = Assert.Single(errors);
        Assert.Equal("/name", error.Field);
        Assert.False(error.Message.StartsWith("/"), error.Message);
    }

    [Fact]
    public void Format_RootFailure_HasNoFieldSet()
    {
        var errors = Evaluate("""{ }""");

        var error = Assert.Single(errors);
        Assert.Null(error.Field);
        Assert.Contains("name", error.Message);
    }

    [Fact]
    public void Format_NestedArrayFailure_PointsAtTheFailingElement()
    {
        var errors = Evaluate("""{ "name": "ok", "lines": [ { "sku": "ABC" }, { } ] }""");

        var error = Assert.Single(errors);
        Assert.Equal("/lines/1", error.Field);
        Assert.Contains("sku", error.Message);
    }

    [Fact]
    public void Format_MultipleFailures_YieldOneErrorEach()
    {
        var errors = Evaluate("""{ "name": "far-too-long-a-name", "lines": [ { } ] }""");

        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, x => x.Field == "/name");
        Assert.Contains(errors, x => x.Field == "/lines/0");
    }

    [Fact]
    public void Format_KeywordFailure_CarriesTheFailedKeywordAsCode()
    {
        var errors = Evaluate("""{ "name": "far-too-long-a-name" }""");

        var error = Assert.Single(errors);
        Assert.False(string.IsNullOrEmpty(error.Code));
    }
}
