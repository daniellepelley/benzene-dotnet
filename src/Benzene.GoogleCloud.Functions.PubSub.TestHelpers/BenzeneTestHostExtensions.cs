using Benzene.Abstractions.Middleware;
using Benzene.Core.MessageHandlers.StartUpChecks;
using Benzene.Microsoft.Dependencies;
using Benzene.Testing;
using CloudNative.CloudEvents;
using Google.Cloud.Functions.Framework;
using Google.Events.Protobuf.Cloud.PubSub.V1;

namespace Benzene.GoogleCloud.Functions.PubSub.TestHelpers;

/// <summary>
/// Provides the Google Cloud Functions Pub/Sub bridge for <see cref="BenzeneTestHostBuilder{TStartUp}"/>.
/// </summary>
public static class BenzeneTestHostExtensions
{
    /// <summary>
    /// Builds an <see cref="ICloudEventFunction{TData}"/> from the StartUp, configured services, and
    /// any overrides registered on <paramref name="builder"/> - the same construction
    /// <see cref="GooglePubSubFunctionHost{TStartUp}"/> performs for a real deployment, with a seam
    /// for test overrides. Dispatch into it with <see cref="SendPubSubAsync"/>.
    /// </summary>
    /// <typeparam name="TStartUp">The <see cref="BenzeneStartUp"/> to run.</typeparam>
    /// <param name="builder">The test host builder, with any <c>WithServices</c>/<c>WithConfiguration</c> overrides already applied.</param>
    /// <returns>The built Cloud Function.</returns>
    public static ICloudEventFunction<MessagePublishedData> BuildGooglePubSubFunctionHost<TStartUp>(this BenzeneTestHostBuilder<TStartUp> builder)
        where TStartUp : BenzeneStartUp, new()
    {
        return builder.Build((startUp, services, configuration) =>
        {
            var container = new MicrosoftBenzeneServiceContainer(services);
            var appBuilder = new GooglePubSubFunctionApplicationBuilder(container);

            startUp.Configure(appBuilder, configuration);

            var app = appBuilder.Build(new MicrosoftServiceResolverFactory(services).WithStartUpChecks());

            return new TestGooglePubSubFunction(app);
        });
    }

    /// <summary>
    /// Sends <paramref name="data"/> through <paramref name="function"/>, wrapped in a minimal
    /// <see cref="CloudEvent"/> envelope (its contents aren't read by anything in the pipeline -
    /// Pub/Sub-specific data already lives on <paramref name="data"/> itself).
    /// </summary>
    /// <param name="function">The Cloud Function to dispatch into (typically built by <see cref="BuildGooglePubSubFunctionHost{TStartUp}"/>).</param>
    /// <param name="data">The Pub/Sub message payload to send (typically built by <see cref="PubSubMessageBuilder"/>).</param>
    /// <param name="cancellationToken">
    /// The token to stand in for the Cloud Functions Framework's invocation token, forwarded through
    /// to <see cref="GooglePubSubFunctionHost{TStartUp}.HandleAsync"/> so a test can assert a handler
    /// observes it via <c>ICancellationTokenAccessor</c>. Defaults to <see cref="CancellationToken.None"/>.
    /// </param>
    /// <returns>A task that completes once <paramref name="function"/> has finished handling the message.</returns>
    public static Task SendPubSubAsync(this ICloudEventFunction<MessagePublishedData> function, MessagePublishedData data, CancellationToken cancellationToken = default)
    {
        var cloudEvent = new CloudEvent
        {
            Type = "google.cloud.pubsub.topic.v1.messagePublished",
            Source = new Uri("https://benzene.test/pubsub-test-helper"),
            Id = Guid.NewGuid().ToString()
        };
        return function.HandleAsync(cloudEvent, data, cancellationToken);
    }

    private sealed class TestGooglePubSubFunction : ICloudEventFunction<MessagePublishedData>
    {
        private readonly IEntryPointMiddlewareApplication<MessagePublishedData> _app;

        public TestGooglePubSubFunction(IEntryPointMiddlewareApplication<MessagePublishedData> app)
        {
            _app = app;
        }

        public Task HandleAsync(CloudEvent cloudEvent, MessagePublishedData data, CancellationToken cancellationToken) => _app.SendAsync(data, cancellationToken);
    }
}
