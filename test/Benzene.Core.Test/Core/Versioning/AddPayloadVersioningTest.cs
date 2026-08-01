using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Abstractions.Serialization;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.BenzeneMessage;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.Messages.BenzeneMessage;
using Benzene.Core.Middleware;
using Benzene.Core.Versioning;
using Benzene.Microsoft.Dependencies;
using Benzene.Results;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Xunit;
using V1 = Benzene.Test.Core.Versioning.Schemas.V1;
using V2 = Benzene.Test.Core.Versioning.Schemas.V2;
using V3 = Benzene.Test.Core.Versioning.Schemas.V3;
using JsonSerializer = Benzene.Core.MessageHandlers.Serialization.JsonSerializer;

namespace Benzene.Test.Core.Versioning;

/// <summary>
/// Coverage for the consolidated <see cref="PayloadVersioningExtensions.AddPayloadVersioning"/> entry
/// point: eager (registration-time) validation of the caster graph, auto-synthesised downcasts, and an
/// end-to-end V1 -> V2 -> V3 upcast chain over the BenzeneMessage transport with the response downcast
/// back - all from a single call, and with only the upcasts declared.
/// </summary>
public class AddPayloadVersioningTest
{
    public class VersioningOrderHandlerV3 : IMessageHandler<V3.OrderPayload, V3.OrderPayload>
    {
        public Task<IBenzeneResult<V3.OrderPayload>> HandleAsync(V3.OrderPayload request)
        {
            // Currency was seeded by the V1->V2 hop, Reference by the V2->V3 hop. Echo both into Id, which
            // exists in V1 and therefore survives the downcast back - so a V1 response proves both hops ran.
            return Task.FromResult(BenzeneResult.Ok(new V3.OrderPayload
            {
                Id = $"{request.Currency}|{request.Reference}",
                Quantity = request.Quantity
            }));
        }
    }

    private static BenzeneMessageApplication BuildApp(out MicrosoftServiceResolverFactory factory)
    {
        var services = new ServiceCollection();
        services
            .AddTransient<ISerializer, JsonSerializer>()
            .AddTransient<JsonSerializer>()
            .UsingBenzene(x => x
                .AddBenzene()
                .AddBenzeneMessage()
                .AddContextItems()
                // One call. Only the upcasts are declared; the downcasts (V3->V2->V1) are synthesised.
                .AddPayloadVersioning(v => v
                    .ForContext<BenzeneMessageContext>()
                    .Topic("order", topic => topic
                        .Version<V1.OrderPayload>("V1")
                        .Version<V2.OrderPayload>("V2")
                        .Version<V3.OrderPayload>("V3")
                        .Upcast<V1.OrderPayload, V2.OrderPayload>(f => f.RegisterInitValue(o => o.Currency, "FROM-V1V2"))
                        .Upcast<V2.OrderPayload, V3.OrderPayload>(f => f.RegisterInitValue(o => o.Reference, "FROM-V2V3")))));

        var pipeline = new MiddlewarePipelineBuilder<BenzeneMessageContext>(new MicrosoftBenzeneServiceContainer(services));
        pipeline.UseMessageHandlers(new Type[0],
            router => router.AddMessageHandler<VersioningOrderHandlerV3, V3.OrderPayload, V3.OrderPayload>("order"));

        factory = new MicrosoftServiceResolverFactory(services);
        return new BenzeneMessageApplication(pipeline.Build());
    }

    [Fact]
    public async Task V1Request_IsUpcastByChainingV1V2V3_AndResponseDowncastToV1_FromASingleCall()
    {
        var app = BuildApp(out var factory);

        var request = new BenzeneMessageRequest
        {
            Topic = "order",
            Body = JsonConvert.SerializeObject(new V1.OrderPayload { Id = "order-1", Quantity = 5 }),
            Headers = new Dictionary<string, string> { { "benzene-version", "V1" } }
        };

        var response = await app.HandleAsync(request, factory);

        Assert.Equal(BenzeneResultStatus.Ok, response.StatusCode);

        var v1 = JsonConvert.DeserializeObject<V1.OrderPayload>(response.Body);
        // Both hops ran (chaining), and the auto-synthesised downcasters returned a V1-shaped response.
        Assert.Equal("FROM-V1V2|FROM-V2V3", v1.Id);
        Assert.Equal(5, v1.Quantity);
        // V1-shaped: none of V2's Currency / V3's Reference leaked out.
        Assert.DoesNotContain("currency", response.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reference", response.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingConversionPath_ThrowsAtRegistration_NotOnFirstMessage()
    {
        var container = new MicrosoftBenzeneServiceContainer(new ServiceCollection());

        // V3 is declared and therefore required, but only the V1->V2 hop is registered - there is no way
        // to reach V3. The expander must fail here, when AddPayloadVersioning is called, not lazily.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            container.AddPayloadVersioning(v => v
                .ForContext<BenzeneMessageContext>()
                .Topic("order", topic => topic
                    .Version<V1.OrderPayload>("V1")
                    .Version<V2.OrderPayload>("V2")
                    .Version<V3.OrderPayload>("V3")
                    .Upcast<V1.OrderPayload, V2.OrderPayload>())));

        Assert.Contains("No conversion path", ex.Message);
    }

    [Fact]
    public void CasterReferencingAnUndeclaredVersion_ThrowsAtRegistration()
    {
        var container = new MicrosoftBenzeneServiceContainer(new ServiceCollection());

        var ex = Assert.Throws<InvalidOperationException>(() =>
            container.AddPayloadVersioning(v => v
                .ForContext<BenzeneMessageContext>()
                .Topic("order", topic => topic
                    .Version<V1.OrderPayload>("V1")
                    // V2 not declared, but used in the upcast below.
                    .Upcast<V1.OrderPayload, V2.OrderPayload>())));

        Assert.Contains("no version name is declared", ex.Message);
    }

    [Fact]
    public void TopicsWithoutAContext_ThrowAtRegistration()
    {
        var container = new MicrosoftBenzeneServiceContainer(new ServiceCollection());

        var ex = Assert.Throws<InvalidOperationException>(() =>
            container.AddPayloadVersioning(v => v
                .Topic("order", topic => topic
                    .Version<V1.OrderPayload>("V1")
                    .Version<V2.OrderPayload>("V2")
                    .Upcast<V1.OrderPayload, V2.OrderPayload>())));

        Assert.Contains("no transport", ex.Message);
    }
}
