using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.Response;
using Benzene.Core.Messages;
using Benzene.Results;
using Benzene.Test.Examples;
using Xunit;
using StjJsonSerializer = Benzene.Core.MessageHandlers.Serialization.JsonSerializer;

namespace Benzene.Test.Core.Core.Response;

/// <summary>
/// Coverage for <see cref="DefaultResponsePayloadMapper{TContext}"/>'s failure-branch emission
/// (Phase 3 of work/problem-details-plan.md): a failed result serializes to the RFC 9457 problem
/// document <see cref="ProblemTypes.From"/> builds, not the retired <c>ErrorPayload</c> shape.
/// </summary>
public class DefaultResponsePayloadMapperTest
{
    private static readonly StjJsonSerializer Serializer = new();

    private static MessageHandlerResult FailedResult(IBenzeneResult benzeneResult)
    {
        var handlerDefinition = Mother.CreateMessageHandlerDefinitionV2();
        return new MessageHandlerResult(new Topic(Defaults.Topic), handlerDefinition, benzeneResult);
    }

    [Fact]
    public void Map_FailedResult_SerializesTheRegistryTypeAndTitle()
    {
        var mapper = new DefaultResponsePayloadMapper<object>();

        var body = mapper.Map(new object(), FailedResult(BenzeneResult.NotFound("Order 123 not found")), Serializer);
        var problem = Serializer.Deserialize<ProblemDetails>(body);

        Assert.Equal("https://benzene.app/problems/not-found", problem.Type);
        Assert.Equal("Not found", problem.Title);
        Assert.Equal("Order 123 not found", problem.Detail);
        Assert.Equal(BenzeneResultStatus.NotFound, problem.BenzeneStatus);
    }

    [Fact]
    public void Map_FailedResult_NeverIncludesANumericStatusMember()
    {
        var mapper = new DefaultResponsePayloadMapper<object>();

        var body = mapper.Map(new object(), FailedResult(BenzeneResult.NotFound("Order 123 not found")), Serializer);

        // Not merely null - genuinely absent, matching the future conformance fixture's
        // "bodyExclude" assertion (this mapper is transport-neutral; the numeric HTTP status is an
        // HTTP-binding-only concern added by a later phase).
        Assert.DoesNotContain("\"status\"", body);
    }

    [Fact]
    public void Map_FailedResultWithStructuredErrors_IncludesTheErrorsArray()
    {
        var mapper = new DefaultResponsePayloadMapper<object>();
        var errors = new[]
        {
            new BenzeneError("Name must not be empty", "Name", "NotEmptyValidator"),
        };

        var body = mapper.Map(new object(), FailedResult(BenzeneResult.ValidationError(errors)), Serializer);
        var problem = Serializer.Deserialize<ProblemDetails>(body);

        var error = Assert.Single(problem.Errors!);
        Assert.Equal("Name must not be empty", error.Message);
        Assert.Equal("Name", error.Field);
        Assert.Equal("NotEmptyValidator", error.Code);
    }

    [Fact]
    public void Map_FailedResultWithNoErrorsAtAll_OmitsTheErrorsMember()
    {
        // BenzeneResult.NotFound() with no message - the result's Errors collection is genuinely
        // empty (unlike Map_FailedResult_SerializesTheRegistryTypeAndTitle's message-only failure,
        // which still yields a single message-only errors entry).
        var mapper = new DefaultResponsePayloadMapper<object>();

        var body = mapper.Map(new object(), FailedResult(BenzeneResult.NotFound()), Serializer);

        Assert.DoesNotContain("\"errors\"", body);
    }

    [Fact]
    public void Map_SuccessfulResult_SerializesThePayload_NotAProblemDocument()
    {
        var mapper = new DefaultResponsePayloadMapper<object>();
        var handlerDefinition = Mother.CreateMessageHandlerDefinitionV2();
        var result = new MessageHandlerResult(new Topic(Defaults.Topic), handlerDefinition,
            BenzeneResult.Ok(Mother.CreateResponse("resp-name")));

        var body = mapper.Map(new object(), result, Serializer);

        Assert.Contains("resp-name", body);
        Assert.DoesNotContain("\"benzeneStatus\"", body);
    }

    [Fact]
    public void Map_HealthCheckStyleResult_SuccessfulDespiteAFailureStatus_RendersThePayload_NotAProblemDocument()
    {
        // The Set<T>(status, payload, isSuccessful) escape hatch (e.g. a health check reporting
        // ServiceUnavailable for a 503 probe while staying successful so the body is the health
        // report, not an error): the mapper branches on IsSuccessful, never on the status class.
        var mapper = new DefaultResponsePayloadMapper<object>();
        var handlerDefinition = Mother.CreateMessageHandlerDefinitionV2();
        var result = new MessageHandlerResult(new Topic(Defaults.Topic), handlerDefinition,
            BenzeneResult.Set(BenzeneResultStatus.ServiceUnavailable, Mother.CreateResponse("degraded"), true));

        var body = mapper.Map(new object(), result, Serializer);

        Assert.Contains("degraded", body);
        Assert.DoesNotContain("\"benzeneStatus\"", body);
        Assert.DoesNotContain("\"type\"", body);
    }

    [Fact]
    public void Map_NoHandlerDefinition_ReturnsNull()
    {
        var mapper = new DefaultResponsePayloadMapper<object>();
        var result = new MessageHandlerResult(BenzeneResult.NotFound());

        Assert.Null(mapper.Map(new object(), result, Serializer));
    }
}
