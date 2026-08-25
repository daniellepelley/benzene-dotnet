using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Amazon.StepFunctions;
using Amazon.StepFunctions.Model;
using Benzene.Clients.Aws.StepFunctions;
using Benzene.Results;
using Benzene.Test.Examples;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json;
using Xunit;

namespace Benzene.Test.Clients.Aws.StepFunctions;

public class StepFunctionsClientTest
{
    [Fact]
    public async Task Start()
    {
        var mockAmazonStepFunctions = new Mock<IAmazonStepFunctions>();
        mockAmazonStepFunctions.Setup(x => x.StartExecutionAsync(It.IsAny<StartExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StartExecutionResponse
            {
                HttpStatusCode = HttpStatusCode.OK
            });

        var client = new StepFunctionsClientFactory(Defaults.StateMachineArn, mockAmazonStepFunctions.Object, NullLogger<StepFunctionsClient>.Instance).Create();
        var result = await client.StartExecutionAsync<ExampleRequestPayload, ExampleResponsePayload>(new ExampleRequestPayload { Id = 42, Name = "hi" });

        mockAmazonStepFunctions.Verify(x => x.StartExecutionAsync(
                       It.Is<StartExecutionRequest>(message =>
                           message.StateMachineArn == Defaults.StateMachineArn &&
                           JsonConvert.DeserializeObject<ExampleRequestPayload>(message.Input).Name == "hi"
                           ), It.IsAny<CancellationToken>()));

        Assert.Equal(BenzeneResultStatus.Accepted, result.Status);
    }

    [Fact]
    public async Task Start_Exception()
    {
        var mockAmazonStepFunctions = new Mock<IAmazonStepFunctions>();
        mockAmazonStepFunctions.Setup(x => x.StartExecutionAsync(It.IsAny<StartExecutionRequest>(), It.IsAny<CancellationToken>()))
            .Throws(new Exception());

        var client = new StepFunctionsClientFactory(Defaults.StateMachineArn, mockAmazonStepFunctions.Object, NullLogger<StepFunctionsClient>.Instance).Create();
        var result = await client.StartExecutionAsync<ExampleRequestPayload, ExampleResponsePayload>(new ExampleRequestPayload { Id = 42, Name = "hi" });

        mockAmazonStepFunctions.Verify(x => x.StartExecutionAsync(
                       It.Is<StartExecutionRequest>(message =>
                           message.StateMachineArn == Defaults.StateMachineArn &&
                           JsonConvert.DeserializeObject<ExampleRequestPayload>(message.Input).Name == "hi"
                           ), It.IsAny<CancellationToken>()));

        Assert.Equal(BenzeneResultStatus.ServiceUnavailable, result.Status);
    }

    [Fact]
    public async Task Start_WithExecutionName_SetsSanitizedNameForIdempotency()
    {
        var mockAmazonStepFunctions = new Mock<IAmazonStepFunctions>();
        mockAmazonStepFunctions.Setup(x => x.StartExecutionAsync(It.IsAny<StartExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StartExecutionResponse { HttpStatusCode = HttpStatusCode.OK });

        var client = new StepFunctionsClientFactory(Defaults.StateMachineArn, mockAmazonStepFunctions.Object, NullLogger<StepFunctionsClient>.Instance).Create();

        // A correlation id containing characters Step Functions disallows in a name.
        var result = await client.StartExecutionAsync<ExampleRequestPayload, ExampleResponsePayload>(
            new ExampleRequestPayload { Id = 42, Name = "hi" }, "corr id/with:bad*chars");

        mockAmazonStepFunctions.Verify(x => x.StartExecutionAsync(
            It.Is<StartExecutionRequest>(message => message.Name == "corr-id-with-bad-chars"),
            It.IsAny<CancellationToken>()));
        Assert.Equal(BenzeneResultStatus.Accepted, result.Status);
    }

    [Fact]
    public async Task Start_ExecutionAlreadyExists_MatchingInput_IsTreatedAsIdempotentSuccess()
    {
        // #13: on ExecutionAlreadyExistsException, the client must call DescribeExecution and compare
        // the existing execution's input to this call's input before deciding - a bare name collision
        // is not, on its own, proof of a true idempotent duplicate.
        //
        // The existing execution's Input is echoed back from whatever this call's own StartExecution
        // attempt sent - i.e. the exact serialized string the client under test produced - rather than
        // reproduced with a second (Newtonsoft) serializer, since the two need not agree byte-for-byte
        // on casing/formatting even for the "same" logical payload.
        string capturedInput = null;
        var mockAmazonStepFunctions = new Mock<IAmazonStepFunctions>();
        mockAmazonStepFunctions.Setup(x => x.StartExecutionAsync(It.IsAny<StartExecutionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<StartExecutionRequest, CancellationToken>((request, _) => capturedInput = request.Input)
            .ThrowsAsync(new ExecutionAlreadyExistsException("already started"));
        mockAmazonStepFunctions.Setup(x => x.DescribeExecutionAsync(It.IsAny<DescribeExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new DescribeExecutionResponse { Input = capturedInput });

        var client = new StepFunctionsClientFactory(Defaults.StateMachineArn, mockAmazonStepFunctions.Object, NullLogger<StepFunctionsClient>.Instance).Create();

        var result = await client.StartExecutionAsync<ExampleRequestPayload, ExampleResponsePayload>(
            new ExampleRequestPayload { Id = 42, Name = "hi" }, "stable-token");

        // A retry after a lost response must not surface as a failure - the execution already exists
        // with the exact same input, verified via DescribeExecution.
        mockAmazonStepFunctions.Verify(x => x.DescribeExecutionAsync(It.IsAny<DescribeExecutionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(BenzeneResultStatus.Accepted, result.Status);
    }

    [Fact]
    public async Task Start_ExecutionAlreadyExists_MismatchedInput_ReturnsConflict()
    {
        // #13: a name collision with a DIFFERENT input must not be silently reported as Accepted - the
        // caller's payload was NOT started, and it needs to know that (never the rejected "already
        // started, unverified" success alternative - see StepFunctionsClient.HandleExecutionAlreadyExistsAsync).
        var mockAmazonStepFunctions = new Mock<IAmazonStepFunctions>();
        mockAmazonStepFunctions.Setup(x => x.StartExecutionAsync(It.IsAny<StartExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ExecutionAlreadyExistsException("already started"));
        mockAmazonStepFunctions.Setup(x => x.DescribeExecutionAsync(It.IsAny<DescribeExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DescribeExecutionResponse
            {
                // The already-existing execution was started with a DIFFERENT input.
                Input = "{\"id\":999,\"name\":\"someone-else\"}"
            });

        var client = new StepFunctionsClientFactory(Defaults.StateMachineArn, mockAmazonStepFunctions.Object, NullLogger<StepFunctionsClient>.Instance).Create();

        var result = await client.StartExecutionAsync<ExampleRequestPayload, ExampleResponsePayload>(
            new ExampleRequestPayload { Id = 42, Name = "hi" }, "stable-token");

        Assert.Equal(BenzeneResultStatus.Conflict, result.Status);
        Assert.False(result.IsSuccessful);

        // The original (mismatched) execution must be left untouched - no further StartExecution/other
        // mutating call is made once the mismatch is detected.
        mockAmazonStepFunctions.Verify(x => x.StartExecutionAsync(It.IsAny<StartExecutionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
