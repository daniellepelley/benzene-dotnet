using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Abstractions.Validation;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.BenzeneMessage;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.Messages.BenzeneMessage;
using Benzene.Core.Middleware;
using Benzene.DataAnnotations;
using Benzene.FluentValidation;
using Benzene.Microsoft.Dependencies;
using Benzene.Results;
using Benzene.Test.Examples;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Xunit;

namespace Benzene.Test.Plugins.DataAnnotations;

public class DataAnnotationsValidationsPipelineTest
{
    [Theory]
    [InlineData("foo", BenzeneResultStatus.Ok)]
    [InlineData("foo-bar-foo-bar", BenzeneResultStatus.ValidationError)]
    public async Task ValidationTest(string name, string expectedStatus)
    {
        var serviceCollection = ServiceResolverMother.CreateServiceCollection();
        serviceCollection.UsingBenzene(x => x.AddBenzeneMessage());

        var pipeline = new MiddlewarePipelineBuilder<BenzeneMessageContext>(new MicrosoftBenzeneServiceContainer(serviceCollection));

        pipeline
            .UseMessageHandlers(x => x.UseDataAnnotationsValidation());

        var aws = new BenzeneMessageApplication(pipeline.Build());

        var request = new BenzeneMessageRequest
        {
            Topic = Defaults.Topic,
            Body = JsonConvert.SerializeObject(new ExampleRequestPayload
            {
                Name = name
            })
        };

        var response = await aws.HandleAsync(request, new MicrosoftServiceResolverFactory(serviceCollection.BuildServiceProvider()));

        Assert.NotNull(response);
        Assert.Equal(expectedStatus, response.StatusCode);
    }

    public class StatusMappedRequest
    {
        [Required]
        public string Name { get; set; }
    }

    // #99: a handler decorated [ValidationStatus] must have its status honoured by
    // Benzene.DataAnnotations the same way Benzene.FluentValidation already honours it, once a
    // shared IValidationStatusMapper is registered (here, Benzene.FluentValidation's
    // DefaultValidationStatusMapper - the mechanism is shared, DataAnnotations doesn't ship its own).
    [ValidationStatus(BenzeneResultStatus.BadRequest)]
    public class StatusMappedHandler : IMessageHandler<StatusMappedRequest, string>
    {
        public Task<IBenzeneResult<string>> HandleAsync(StatusMappedRequest request)
        {
            return Task.FromResult(BenzeneResult.Ok("ok"));
        }
    }

    public class UndecoratedRequest
    {
        [Required]
        public string Name { get; set; }
    }

    public class UndecoratedHandler : IMessageHandler<UndecoratedRequest, string>
    {
        public Task<IBenzeneResult<string>> HandleAsync(UndecoratedRequest request)
        {
            return Task.FromResult(BenzeneResult.Ok("ok"));
        }
    }

    [Fact]
    public async Task ValidationStatusAttribute_OverridesFailureStatus_WhenMapperRegistered()
    {
        var serviceCollection = ServiceResolverMother.CreateServiceCollection();
        serviceCollection.UsingBenzene(x => x.AddBenzeneMessage());
        serviceCollection.AddSingleton<IValidationStatusMapper, DefaultValidationStatusMapper>();

        var pipeline = new MiddlewarePipelineBuilder<BenzeneMessageContext>(new MicrosoftBenzeneServiceContainer(serviceCollection));

        pipeline.UseMessageHandlers(x => x
            .UseDataAnnotationsValidation()
            .AddMessageHandler<StatusMappedHandler, StatusMappedRequest, string>("da-status-mapped-test"));

        var aws = new BenzeneMessageApplication(pipeline.Build());

        var request = new BenzeneMessageRequest
        {
            Topic = "da-status-mapped-test",
            Body = JsonConvert.SerializeObject(new StatusMappedRequest { Name = null })
        };

        var response = await aws.HandleAsync(request, new MicrosoftServiceResolverFactory(serviceCollection.BuildServiceProvider()));

        Assert.NotNull(response);
        Assert.Equal(BenzeneResultStatus.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UndecoratedHandler_KeepsValidationError_WhenMapperRegistered()
    {
        // Regression: a registered mapper must not change the outcome for a handler that carries
        // no [ValidationStatus] attribute - the mapper's own no-attribute fallback is ValidationError.
        var serviceCollection = ServiceResolverMother.CreateServiceCollection();
        serviceCollection.UsingBenzene(x => x.AddBenzeneMessage());
        serviceCollection.AddSingleton<IValidationStatusMapper, DefaultValidationStatusMapper>();

        var pipeline = new MiddlewarePipelineBuilder<BenzeneMessageContext>(new MicrosoftBenzeneServiceContainer(serviceCollection));

        pipeline.UseMessageHandlers(x => x
            .UseDataAnnotationsValidation()
            .AddMessageHandler<UndecoratedHandler, UndecoratedRequest, string>("da-status-undecorated-test"));

        var aws = new BenzeneMessageApplication(pipeline.Build());

        var request = new BenzeneMessageRequest
        {
            Topic = "da-status-undecorated-test",
            Body = JsonConvert.SerializeObject(new UndecoratedRequest { Name = null })
        };

        var response = await aws.HandleAsync(request, new MicrosoftServiceResolverFactory(serviceCollection.BuildServiceProvider()));

        Assert.NotNull(response);
        Assert.Equal(BenzeneResultStatus.ValidationError, response.StatusCode);
    }
}
