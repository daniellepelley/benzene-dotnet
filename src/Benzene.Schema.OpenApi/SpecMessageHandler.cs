using Benzene.Abstractions.DI;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.Messages;
using Benzene.Results;

namespace Benzene.Schema.OpenApi;

public class SpecMessageHandler : IMessageHandler<SpecRequest, RawStringMessage>
{
    private readonly IServiceResolver _serviceResolver;

    public SpecMessageHandler(IServiceResolver serviceResolver)
    {
        _serviceResolver = serviceResolver;
    }
    
    public Task<IBenzeneResult<RawStringMessage>> HandleAsync(SpecRequest request)
    {
        // wire-contracts §7: an absent body, an empty body, null and {} all mean benzene/json (the
        // contract document); values compare case-insensitively; a type or format this service does not
        // support is answered not-implemented - never with a different document.
        var specRequest = SpecBuilder.Normalize(request);
        if (!SpecBuilder.IsSupported(specRequest))
        {
            return BenzeneResult.NotImplemented<RawStringMessage>(
                $"Spec type '{specRequest.Type}' in format '{specRequest.Format}' is not supported. " +
                $"Supported types: {SpecBuilder.BenzeneType}, {SpecBuilder.OpenApiType}, {SpecBuilder.AsyncApiType}; " +
                $"formats: {SpecBuilder.JsonFormat}, {SpecBuilder.YamlFormat}.").AsTask();
        }

        // The spec is deterministic for a given (type, format), so serve it from the memoizing
        // SpecCache when one is registered (UseSpec registers a singleton) - only the first request
        // for each combination pays the full schema-generation/serialization cost. Fall back to a
        // direct build if no cache is registered, so behaviour is unchanged when it isn't.
        var cache = _serviceResolver.TryGetService<SpecCache>();
        var output = cache != null
            ? cache.GetOrBuild(specRequest, r => CreateSpec(_serviceResolver, r))
            : CreateSpec(_serviceResolver, specRequest);

        return BenzeneResult.Ok(new RawStringMessage(output)).AsTask();
    }

    private static string CreateSpec(IServiceResolver resolver, SpecRequest specRequest)
    {
        var specBuilder = new SpecBuilder();
        return specBuilder.CreateSpec(resolver, specRequest);
    }
}