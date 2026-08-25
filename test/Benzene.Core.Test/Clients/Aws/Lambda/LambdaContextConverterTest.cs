using System.Collections.Generic;
using System.Threading.Tasks;
using Amazon.Lambda;
using Amazon.Lambda.Model;
using Benzene.Abstractions.Messages.BenzeneClient;
using Benzene.Clients;
using Benzene.Clients.Aws.Lambda;
using Benzene.Results;
using Benzene.Test.Clients.Aws.Samples;
using Xunit;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Test.Clients.Aws.Lambda;

public class LambdaContextConverterTest
{
    private static IBenzeneClientContext<ExamplePayload, Void> BuildContextIn()
    {
        var request = new BenzeneClientRequest<ExamplePayload>("some-topic", new ExamplePayload(), new Dictionary<string, string>());
        return new BenzeneClientContext<ExamplePayload, Void>(request);
    }

    [Fact]
    public async Task CreateRequestAsync_SetsInvocationTypeEvent()
    {
        // #12: the <T, Void> fire-and-forget shape must invoke async (Event), not the SDK default
        // (RequestResponse, i.e. synchronous) - contradicting the shape's own fire-and-forget contract.
        var converter = new LambdaContextConverter<ExamplePayload>();

        var context = await converter.CreateRequestAsync(BuildContextIn());

        Assert.Equal(InvocationType.Event, context.Request.InvocationType);
    }

    [Fact]
    public async Task MapResponseAsync_EventInvoke_2xxStatusCode_Accepted()
    {
        var converter = new LambdaContextConverter<ExamplePayload>();
        var contextIn = BuildContextIn();
        var contextOut = new LambdaSendMessageContext(new InvokeRequest { InvocationType = InvocationType.Event })
        {
            Response = new InvokeResponse { StatusCode = 202 }
        };

        await converter.MapResponseAsync(contextIn, contextOut);

        Assert.Equal(BenzeneResultStatus.Accepted, contextIn.Response.Status);
        Assert.True(contextIn.Response.IsSuccessful);
    }

    [Fact]
    public async Task MapResponseAsync_EventInvoke_NonSuccessStatusCode_Failure()
    {
        // A non-2xx StatusCode on an Event invoke's InvokeResponse must not be reported as Accepted.
        var converter = new LambdaContextConverter<ExamplePayload>();
        var contextIn = BuildContextIn();
        var contextOut = new LambdaSendMessageContext(new InvokeRequest { InvocationType = InvocationType.Event, FunctionName = "some-function" })
        {
            Response = new InvokeResponse { StatusCode = 429 }
        };

        await converter.MapResponseAsync(contextIn, contextOut);

        Assert.NotEqual(BenzeneResultStatus.Accepted, contextIn.Response.Status);
        Assert.False(contextIn.Response.IsSuccessful);
    }

    [Fact]
    public async Task MapResponseAsync_RequestResponseInvoke_FunctionErrorSet_Failure()
    {
        // #12: MapResponseAsync must stop unconditionally returning Accepted - a non-null FunctionError
        // on a request/response invoke must produce a failure result carrying the error details, never
        // Accepted (AWS returns HTTP 200 even when the target function threw).
        var converter = new LambdaContextConverter<ExamplePayload>();
        var contextIn = BuildContextIn();
        var contextOut = new LambdaSendMessageContext(new InvokeRequest { InvocationType = InvocationType.RequestResponse, FunctionName = "some-function" })
        {
            Response = new InvokeResponse { StatusCode = 200, FunctionError = "Unhandled" }
        };

        await converter.MapResponseAsync(contextIn, contextOut);

        Assert.NotEqual(BenzeneResultStatus.Accepted, contextIn.Response.Status);
        Assert.False(contextIn.Response.IsSuccessful);
        Assert.Contains(contextIn.Response.Errors, e => e.Message.Contains("Unhandled"));
    }

    [Fact]
    public async Task MapResponseAsync_RequestResponseInvoke_NoFunctionError_Accepted()
    {
        var converter = new LambdaContextConverter<ExamplePayload>();
        var contextIn = BuildContextIn();
        var contextOut = new LambdaSendMessageContext(new InvokeRequest { InvocationType = InvocationType.RequestResponse, FunctionName = "some-function" })
        {
            Response = new InvokeResponse { StatusCode = 200 }
        };

        await converter.MapResponseAsync(contextIn, contextOut);

        Assert.Equal(BenzeneResultStatus.Accepted, contextIn.Response.Status);
        Assert.True(contextIn.Response.IsSuccessful);
    }
}
