using System;
using System.Net;
using System.Security.Cryptography;
using System.Text;
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
        // #14: character replacement makes the sanitized name differ from the original, so - to stay
        // collision-resistant across distinct originals that happen to sanitize alike (see the
        // replacement-collision test below) - the result is the replaced name plus a deterministic
        // hash-of-the-original suffix, not the bare replaced string.
        var mockAmazonStepFunctions = new Mock<IAmazonStepFunctions>();
        mockAmazonStepFunctions.Setup(x => x.StartExecutionAsync(It.IsAny<StartExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StartExecutionResponse { HttpStatusCode = HttpStatusCode.OK });

        var client = new StepFunctionsClientFactory(Defaults.StateMachineArn, mockAmazonStepFunctions.Object, NullLogger<StepFunctionsClient>.Instance).Create();

        // A correlation id containing characters Step Functions disallows in a name.
        const string originalName = "corr id/with:bad*chars";
        var result = await client.StartExecutionAsync<ExampleRequestPayload, ExampleResponsePayload>(
            new ExampleRequestPayload { Id = 42, Name = "hi" }, originalName);

        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(originalName)))[..8];
        var expectedName = $"corr-id-with-bad-chars-{expectedHash}";

        mockAmazonStepFunctions.Verify(x => x.StartExecutionAsync(
            It.Is<StartExecutionRequest>(message => message.Name == expectedName),
            It.IsAny<CancellationToken>()));
        Assert.Equal(BenzeneResultStatus.Accepted, result.Status);
    }

    [Fact]
    public async Task Start_WithExecutionName_AlreadyClean_IsReturnedUnchanged()
    {
        // #14: a name with no disallowed character and within the 80-character cap needs no hash
        // suffix - it is returned exactly as given.
        var mockAmazonStepFunctions = new Mock<IAmazonStepFunctions>();
        mockAmazonStepFunctions.Setup(x => x.StartExecutionAsync(It.IsAny<StartExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StartExecutionResponse { HttpStatusCode = HttpStatusCode.OK });

        var client = new StepFunctionsClientFactory(Defaults.StateMachineArn, mockAmazonStepFunctions.Object, NullLogger<StepFunctionsClient>.Instance).Create();

        var result = await client.StartExecutionAsync<ExampleRequestPayload, ExampleResponsePayload>(
            new ExampleRequestPayload { Id = 42, Name = "hi" }, "already-clean-name");

        mockAmazonStepFunctions.Verify(x => x.StartExecutionAsync(
            It.Is<StartExecutionRequest>(message => message.Name == "already-clean-name"),
            It.IsAny<CancellationToken>()));
        Assert.Equal(BenzeneResultStatus.Accepted, result.Status);
    }

    [Fact]
    public async Task Start_WithExecutionName_ReplacementCollision_ProducesDistinctSanitizedNames()
    {
        // #14: "a/b" and "a:b" both replace a disallowed character ('/' and ':' respectively) to the
        // same "a-b" - without a hash of the ORIGINAL name appended, these two distinct correlation
        // ids would collide onto the same Step Functions execution name, defeating (b)'s idempotency
        // check (two callers with genuinely different inputs would look like the same retried call).
        var nameA = await CaptureSanitizedExecutionNameAsync("a/b");
        var nameB = await CaptureSanitizedExecutionNameAsync("a:b");

        Assert.StartsWith("a-b-", nameA);
        Assert.StartsWith("a-b-", nameB);
        Assert.NotEqual(nameA, nameB);
    }

    [Fact]
    public async Task Start_WithExecutionName_LongNamesDifferingAfterTheCutPoint_ProduceDistinctSanitizedNames()
    {
        // #14: two >80-character names that are identical up to and past Step Functions' 80-character
        // cap (they only diverge after it) must not collide onto the same truncated execution name.
        var commonPrefix = new string('a', 90);
        var nameA = await CaptureSanitizedExecutionNameAsync(commonPrefix + "-suffix-one");
        var nameB = await CaptureSanitizedExecutionNameAsync(commonPrefix + "-suffix-two");

        Assert.NotNull(nameA);
        Assert.NotNull(nameB);
        Assert.True(nameA.Length <= 80);
        Assert.True(nameB.Length <= 80);
        Assert.NotEqual(nameA, nameB);
    }

    private static async Task<string> CaptureSanitizedExecutionNameAsync(string originalExecutionName)
    {
        string capturedName = null;
        var mockAmazonStepFunctions = new Mock<IAmazonStepFunctions>();
        mockAmazonStepFunctions.Setup(x => x.StartExecutionAsync(It.IsAny<StartExecutionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<StartExecutionRequest, CancellationToken>((request, _) => capturedName = request.Name)
            .ReturnsAsync(new StartExecutionResponse { HttpStatusCode = HttpStatusCode.OK });

        var client = new StepFunctionsClientFactory(Defaults.StateMachineArn, mockAmazonStepFunctions.Object, NullLogger<StepFunctionsClient>.Instance).Create();
        await client.StartExecutionAsync<ExampleRequestPayload, ExampleResponsePayload>(
            new ExampleRequestPayload { Id = 42, Name = "hi" }, originalExecutionName);

        return capturedName;
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
