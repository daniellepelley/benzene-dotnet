using System;
using System.Threading.Tasks;
using Benzene.Abstractions.DI;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.BenzeneMessage;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.MessageHandlers.Filters;
using Benzene.Core.Messages.BenzeneMessage;
using Benzene.Core.Middleware;
using Benzene.Microsoft.Dependencies;
using Benzene.Results;
using Benzene.Test.Examples;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Xunit;

namespace Benzene.Test.Core.Core.Filters;

public class FiltersPipelineTest
{
    [Theory]
    [InlineData("foo", BenzeneResultStatus.Ok)]
    [InlineData("foo-bar-foo-bar", BenzeneResultStatus.Ignored)]
    public async Task Send_HealthCheck(string name, string expectedStatus)
    {
        var serviceCollection = ServiceResolverMother.CreateServiceCollection();
        serviceCollection.UsingBenzene(x => x.AddBenzeneMessage());

        var pipeline = new MiddlewarePipelineBuilder<BenzeneMessageContext>(new MicrosoftBenzeneServiceContainer(serviceCollection));

        pipeline
            .UseMessageHandlers(x => x.UseFilters());

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

    // The three fixtures/cases below exercise DependencyExtensions.AddFilters(Type[]) directly, against
    // a fresh, throwaway IServiceCollection built only from the explicit candidate types each test
    // passes in - unlike Send_HealthCheck above (which goes through the AppDomain-wide UseFilters()
    // scan), so adding these fixture classes to the test assembly can never change what any other
    // test's bare UseFilters() call discovers.

    private class RequestA
    {
        public string Name { get; set; }
    }

    private class RequestB
    {
        public Guid Id { get; set; }
    }

    private class RequestC
    {
        public int Value { get; set; }
    }

    // #226: a class implementing IFilter<T> for more than one T. filterType.GetInterface("IFilter`1")
    // matched by simple interface name only, so resolving this class's interface threw
    // AmbiguousMatchException at registration time - before either closed interface was ever reached.
    private class MultiTopicFilter : IFilter<RequestA>, IFilter<RequestB>
    {
        public bool Filter(RequestA value) => value.Name == "foo";

        public bool Filter(RequestB value) => value.Id != Guid.Empty;
    }

    private class SingleFilterForA : IFilter<RequestA>
    {
        public bool Filter(RequestA value) => true;
    }

    private class SingleFilterForC : IFilter<RequestC>
    {
        public bool Filter(RequestC value) => true;
    }

    private static IServiceProvider BuildProvider(Action<IBenzeneServiceContainer> configure)
    {
        var services = new ServiceCollection();
        configure(new MicrosoftBenzeneServiceContainer(services));
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddFilters_ClassImplementingIFilterForTwoTypes_RegistersBothClosedInterfacesAndRoutesCorrectly()
    {
        var provider = BuildProvider(x => x.AddFilters(new[] { typeof(MultiTopicFilter) }));

        var filterForA = provider.GetService<IFilter<RequestA>>();
        var filterForB = provider.GetService<IFilter<RequestB>>();

        Assert.IsType<MultiTopicFilter>(filterForA);
        Assert.IsType<MultiTopicFilter>(filterForB);

        Assert.True(filterForA.Filter(new RequestA { Name = "foo" }));
        Assert.False(filterForA.Filter(new RequestA { Name = "bar" }));
        Assert.True(filterForB.Filter(new RequestB { Id = Guid.NewGuid() }));
        Assert.False(filterForB.Filter(new RequestB { Id = Guid.Empty }));
    }

    [Fact]
    public void AddFilters_NoCandidateImplementsIFilter_RegistersNothingAndDoesNotThrow()
    {
        var provider = BuildProvider(x => x.AddFilters(Array.Empty<Type>()));

        Assert.Null(provider.GetService<IFilter<RequestA>>());
    }

    [Fact]
    public void AddFilters_MultipleDistinctFilterClasses_EachRegistersItsOwnClosedInterface()
    {
        var provider = BuildProvider(x => x.AddFilters(new[] { typeof(SingleFilterForA), typeof(SingleFilterForC) }));

        Assert.IsType<SingleFilterForA>(provider.GetService<IFilter<RequestA>>());
        Assert.IsType<SingleFilterForC>(provider.GetService<IFilter<RequestC>>());
        // Neither class touches RequestB - nothing should be registered for it.
        Assert.Null(provider.GetService<IFilter<RequestB>>());
    }
}
