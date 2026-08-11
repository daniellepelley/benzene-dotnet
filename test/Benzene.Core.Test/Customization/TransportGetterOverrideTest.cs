using System.Collections.Generic;
using Benzene.Abstractions.MessageHandlers.Request;
using Benzene.Abstractions.Messages.Mappers;
using Benzene.AspNet.Core;
using Benzene.Core.Versioning;
using Benzene.Core.Versioning.Request;
using Benzene.HostedService;
using Benzene.Http;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Benzene.Test.Customization;

// Regression tests for the TryAdd conversion of the per-context transport seams: a user registration
// made in ConfigureServices (which runs BEFORE Configure, where UseAspNet/UseHttp registers the
// transport's defaults) must win over the transport's now-TryAdd'd default registration.
//
// These reuse CustomStatusStartUp (the real UseAspNet wiring) but only BUILD the host - the
// registrations all land in the host's IServiceCollection during ConfigureServices (Configure runs
// inside it, see Benzene.HostedService.HostBuilderExtensions), so resolving from the built container
// proves which registration won without binding a socket.
public class HeaderGetterOverrideStartUp : CustomStatusStartUp
{
    // Wraps the framework default and injects a marker header - the documented "decorate the
    // transport getter" customization.
    public class MarkerHeadersGetter : IMessageHeadersGetter<AspNetContext>
    {
        private readonly AspNetMessageHeadersGetter _inner;

        public MarkerHeadersGetter(IHttpHeaderMappings httpHeaderMappings)
        {
            _inner = new AspNetMessageHeadersGetter(httpHeaderMappings);
        }

        public IDictionary<string, string> GetHeaders(AspNetContext context)
        {
            var headers = _inner.GetHeaders(context);
            headers["x-marker"] = "on";
            return headers;
        }
    }

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.UsingBenzene(x => x.AddScoped<IMessageHeadersGetter<AspNetContext>, MarkerHeadersGetter>());
        base.ConfigureServices(services, configuration);
    }
}

// AddPayloadVersioning(...).ForContext<AspNetContext>() in ConfigureServices: before the TryAdd
// conversion, UseHttp/UseAspNet's later plain AddScoped<IRequestMapper<AspNetContext>> silently
// disabled the casting decorator; now the decorator's earlier registration wins.
public class AspNetVersioningStartUp : CustomStatusStartUp
{
    public class OrderV1
    {
        public string Id { get; set; } = "";
    }

    public class OrderV2
    {
        public string Id { get; set; } = "";
        public string WarehouseId { get; set; } = "";
    }

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.UsingBenzene(x => x.AddPayloadVersioning(v => v
            .ForContext<AspNetContext>()
            .Topic("probe-order", topic => topic
                .Version<OrderV1>("v1")
                .Version<OrderV2>("v2")
                .Upcast<OrderV1, OrderV2>(f => f.RegisterInitValue(o => o.WarehouseId, "wh-main")))));
        base.ConfigureServices(services, configuration);
    }
}

public class TransportGetterOverrideTest
{
    [Fact]
    public void CustomHeadersGetter_InConfigureServices_WinsOverTransportDefault()
    {
        using var host = new HostBuilder().UseBenzene<HeaderGetterOverrideStartUp>().Build();
        using var scope = host.Services.CreateScope();

        var getter = scope.ServiceProvider.GetRequiredService<IMessageHeadersGetter<AspNetContext>>();

        // The transport registers AspNetMessageHeadersGetter with TryAdd, so the user's earlier
        // ConfigureServices registration is the one resolved.
        Assert.IsType<HeaderGetterOverrideStartUp.MarkerHeadersGetter>(getter);
    }

    [Fact]
    public void PayloadVersioning_ForAspNetContext_InConfigureServices_IsNotDisabledByUseAspNet()
    {
        using var host = new HostBuilder().UseBenzene<AspNetVersioningStartUp>().Build();
        using var scope = host.Services.CreateScope();

        var mapper = scope.ServiceProvider.GetRequiredService<IRequestMapper<AspNetContext>>();

        Assert.IsType<CastingRequestMapper<AspNetContext>>(mapper);
    }
}
