using Benzene.Abstractions.Results;
using Benzene.Results;
using Xunit;
using StjJsonSerializer = Benzene.Core.MessageHandlers.Serialization.JsonSerializer;
using NewtonsoftJsonSerializer = Benzene.NewtonsoftJson.JsonSerializer;

namespace Benzene.Test.Core.Core.Results;

/// <summary>
/// Round-trip coverage for <see cref="ProblemDetails"/> across the two serializers exercised at
/// merge time by work/archive/problem-details-plan-2026-08.md Phase 3 (System.Text.Json - the process default -
/// and Newtonsoft.Json), plus the <c>[JsonIgnore(Condition = WhenWritingNull)]</c> omission
/// contract on every optional member - load-bearing for <see cref="ProblemDetails.Status"/>
/// specifically: a future conformance fixture pins that it is *absent* from the wire, not <c>null</c>,
/// when no HTTP response exists (verified against the default STJ serializer specifically, matching
/// the equivalent <c>BenzeneErrorSerializationTest</c> caveat for Newtonsoft's own
/// <c>NullValueHandling</c>).
/// </summary>
public class ProblemDetailsSerializationTest
{
    private static readonly ProblemDetails Full = new()
    {
        Type = "https://benzene.app/problems/validation-error",
        Title = "Validation failed",
        Status = 422,
        Detail = "Name must not be empty, Age must be greater than 0",
        Instance = "https://example.com/errors/123",
        BenzeneStatus = "validation-error",
        Errors = new[]
        {
            new BenzeneError("Name must not be empty", "Name", "NotEmptyValidator"),
            new BenzeneError("Age must be greater than 0", "Age", "GreaterThanValidator"),
        },
    };

    // The shape emitted off an HTTP transport: no numeric Status, no Errors (message-only failure).
    private static readonly ProblemDetails EnvelopeShaped = new()
    {
        Type = "https://benzene.app/problems/not-found",
        Title = "Not found",
        Detail = "Order 123 not found",
        BenzeneStatus = "not-found",
    };

    [Fact]
    public void Stj_RoundTrip_PreservesEveryMember()
    {
        var serializer = new StjJsonSerializer();

        var json = serializer.Serialize(Full);
        var roundTripped = serializer.Deserialize<ProblemDetails>(json);

        Assert.Equal(Full.Type, roundTripped.Type);
        Assert.Equal(Full.Title, roundTripped.Title);
        Assert.Equal(Full.Status, roundTripped.Status);
        Assert.Equal(Full.Detail, roundTripped.Detail);
        Assert.Equal(Full.Instance, roundTripped.Instance);
        Assert.Equal(Full.BenzeneStatus, roundTripped.BenzeneStatus);
        Assert.Equal(Full.Errors, roundTripped.Errors);
    }

    [Fact]
    public void Stj_Serialize_UsesCamelCaseWireNames()
    {
        var json = new StjJsonSerializer().Serialize(Full);

        Assert.Contains("\"type\"", json);
        Assert.Contains("\"title\"", json);
        Assert.Contains("\"status\"", json);
        Assert.Contains("\"detail\"", json);
        Assert.Contains("\"instance\"", json);
        Assert.Contains("\"benzeneStatus\"", json);
        Assert.Contains("\"errors\"", json);
    }

    [Fact]
    public void Stj_Serialize_OmitsStatus_NotNull_WhenAbsent()
    {
        // The load-bearing assertion: Status must be genuinely ABSENT from the wire, not present as
        // JSON null - a future conformance fixture's "bodyExclude" pins exactly this.
        var json = new StjJsonSerializer().Serialize(EnvelopeShaped);

        Assert.DoesNotContain("\"status\"", json);
        Assert.DoesNotContain("null", json);
    }

    [Fact]
    public void Stj_Serialize_OmitsEveryUnsetOptionalMember()
    {
        var json = new StjJsonSerializer().Serialize(EnvelopeShaped);

        Assert.DoesNotContain("\"instance\"", json);
        Assert.DoesNotContain("\"errors\"", json);
    }

    [Fact]
    public void Stj_RoundTrip_EnvelopeShaped_StatusAndErrorsStayNull()
    {
        var serializer = new StjJsonSerializer();

        var json = serializer.Serialize(EnvelopeShaped);
        var roundTripped = serializer.Deserialize<ProblemDetails>(json);

        Assert.Null(roundTripped.Status);
        Assert.Null(roundTripped.Errors);
        Assert.Equal(EnvelopeShaped.Detail, roundTripped.Detail);
        Assert.Equal(EnvelopeShaped.BenzeneStatus, roundTripped.BenzeneStatus);
    }

    [Fact]
    public void Newtonsoft_RoundTrip_PreservesEveryMember()
    {
        var serializer = new NewtonsoftJsonSerializer();

        var json = serializer.Serialize(Full);
        var roundTripped = serializer.Deserialize<ProblemDetails>(json);

        Assert.Equal(Full.Type, roundTripped.Type);
        Assert.Equal(Full.Title, roundTripped.Title);
        Assert.Equal(Full.Status, roundTripped.Status);
        Assert.Equal(Full.Detail, roundTripped.Detail);
        Assert.Equal(Full.Instance, roundTripped.Instance);
        Assert.Equal(Full.BenzeneStatus, roundTripped.BenzeneStatus);
        Assert.Equal(Full.Errors, roundTripped.Errors);
    }

    [Fact]
    public void Newtonsoft_RoundTrip_EnvelopeShaped_StatusAndErrorsStayNull()
    {
        var serializer = new NewtonsoftJsonSerializer();

        var json = serializer.Serialize(EnvelopeShaped);
        var roundTripped = serializer.Deserialize<ProblemDetails>(json);

        Assert.Null(roundTripped.Status);
        Assert.Null(roundTripped.Errors);
        Assert.Equal(EnvelopeShaped.Detail, roundTripped.Detail);
    }
}
