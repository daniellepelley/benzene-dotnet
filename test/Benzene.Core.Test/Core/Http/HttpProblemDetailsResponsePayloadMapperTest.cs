using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.Response;
using Benzene.Core.Messages;
using Benzene.Http;
using Benzene.Results;
using Benzene.Test.Examples;
using Xunit;
using StjJsonSerializer = Benzene.Core.MessageHandlers.Serialization.JsonSerializer;

namespace Benzene.Test.Core.Http;

/// <summary>
/// Coverage for <see cref="HttpProblemDetailsResponsePayloadMapper{TContext}"/> (Phase 4 of
/// work/archive/problem-details-plan-2026-08.md): the HTTP-facing decorator that fills in
/// <see cref="ProblemDetails.Status"/> from the same <see cref="IHttpStatusCodeMapper"/> the
/// response status line uses, and otherwise delegates straight through.
/// </summary>
public class HttpProblemDetailsResponsePayloadMapperTest
{
    private static readonly StjJsonSerializer Serializer = new();

    private static HttpProblemDetailsResponsePayloadMapper<object> CreateMapper()
    {
        return new HttpProblemDetailsResponsePayloadMapper<object>(
            new DefaultResponsePayloadMapper<object>(), new DefaultHttpStatusCodeMapper());
    }

    private static MessageHandlerResult ResultFor(IBenzeneResult benzeneResult)
    {
        var handlerDefinition = Mother.CreateMessageHandlerDefinitionV2();
        return new MessageHandlerResult(new Topic(Defaults.Topic), handlerDefinition, benzeneResult);
    }

    [Fact]
    public void Map_FailedResult_SetsStatusFromTheSameHttpStatusCodeMapperTheResponseLineUses()
    {
        var mapper = CreateMapper();
        var httpStatusCodeMapper = new DefaultHttpStatusCodeMapper();

        var body = mapper.Map(new object(), ResultFor(BenzeneResult.ValidationError("Name must not be empty")), Serializer);
        var problem = Serializer.Deserialize<ProblemDetails>(body);

        var expectedStatus = int.Parse(httpStatusCodeMapper.Map(BenzeneResultStatus.ValidationError, false));
        Assert.Equal(422, expectedStatus);
        Assert.Equal(expectedStatus, problem.Status);
    }

    [Fact]
    public void Map_FailedResult_StatusMatchesTheRegistryHttpMapping()
    {
        var mapper = CreateMapper();

        var notFoundProblem = Serializer.Deserialize<ProblemDetails>(
            mapper.Map(new object(), ResultFor(BenzeneResult.NotFound("missing")), Serializer));
        var conflictProblem = Serializer.Deserialize<ProblemDetails>(
            mapper.Map(new object(), ResultFor(BenzeneResult.Conflict("conflict")), Serializer));

        Assert.Equal(404, notFoundProblem.Status);
        Assert.Equal(409, conflictProblem.Status);
    }

    [Fact]
    public void Map_FailedResult_StillCarriesTheRegistryTypeTitleDetailAndBenzeneStatus()
    {
        var mapper = CreateMapper();

        var body = mapper.Map(new object(), ResultFor(BenzeneResult.NotFound("Order 123 not found")), Serializer);
        var problem = Serializer.Deserialize<ProblemDetails>(body);

        Assert.Equal("https://benzene.app/problems/not-found", problem.Type);
        Assert.Equal("Not found", problem.Title);
        Assert.Equal("Order 123 not found", problem.Detail);
        Assert.Equal(BenzeneResultStatus.NotFound, problem.BenzeneStatus);
        Assert.Equal(404, problem.Status);
    }

    [Fact]
    public void Map_SuccessfulResult_DelegatesToInner_NoStatusOrBenzeneStatusMember()
    {
        var mapper = CreateMapper();
        var handlerDefinition = Mother.CreateMessageHandlerDefinitionV2();
        var result = new MessageHandlerResult(new Topic(Defaults.Topic), handlerDefinition,
            BenzeneResult.Ok(Mother.CreateResponse("resp-name")));

        var body = mapper.Map(new object(), result, Serializer);

        Assert.Contains("resp-name", body);
        Assert.DoesNotContain("\"benzeneStatus\"", body);
        Assert.DoesNotContain("\"status\"", body);
    }

    [Fact]
    public void Map_HealthCheckStyleResult_SuccessfulDespiteAFailureStatus_DelegatesToInner()
    {
        // The Set<T>(status, payload, isSuccessful) escape hatch: the mapper branches on
        // IsSuccessful, never on the status class, so this never reaches the problem-document path
        // even though the status is a failure status.
        var mapper = CreateMapper();
        var handlerDefinition = Mother.CreateMessageHandlerDefinitionV2();
        var result = new MessageHandlerResult(new Topic(Defaults.Topic), handlerDefinition,
            BenzeneResult.Set(BenzeneResultStatus.ServiceUnavailable, Mother.CreateResponse("degraded"), true));

        var body = mapper.Map(new object(), result, Serializer);

        Assert.Contains("degraded", body);
        Assert.DoesNotContain("\"status\"", body);
    }

    [Fact]
    public void Map_NoHandlerDefinition_ReturnsNull()
    {
        var mapper = CreateMapper();
        var result = new MessageHandlerResult(BenzeneResult.NotFound());

        Assert.Null(mapper.Map(new object(), result, Serializer));
    }

    [Fact]
    public void Inner_ExposesTheWrappedMapper()
    {
        var inner = new DefaultResponsePayloadMapper<object>();
        var mapper = new HttpProblemDetailsResponsePayloadMapper<object>(inner, new DefaultHttpStatusCodeMapper());

        Assert.Same(inner, mapper.Inner);
    }
}
