using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Results;

namespace Benzene.Conformance.Test.Handlers;

public class VersionedRequest
{
}

public class VersionedReply
{
    public string Version { get; set; } = string.Empty;
}

/// <summary>
/// Canonical conformance handler (docs/specification/conformance/README.md): <c>conformance:versioned</c> at
/// version <c>v1</c>, declared the topic's default handler. With <see cref="VersionedV2ConformanceHandler"/>
/// it exercises version selection (core-concepts §2): an exact match, an absent version going to the
/// default, and an unknown version as <c>not-found</c>.
/// </summary>
[Message("conformance:versioned", "v1", IsDefault = true)]
public class VersionedV1ConformanceHandler : IMessageHandler<VersionedRequest, VersionedReply>
{
    public Task<IBenzeneResult<VersionedReply>> HandleAsync(VersionedRequest request)
    {
        return Task.FromResult(BenzeneResult.Ok(new VersionedReply { Version = "v1" }));
    }
}

/// <summary>
/// Canonical conformance handler: <c>conformance:versioned</c> at version <c>v2</c> (see
/// <see cref="VersionedV1ConformanceHandler"/>).
/// </summary>
[Message("conformance:versioned", "v2")]
public class VersionedV2ConformanceHandler : IMessageHandler<VersionedRequest, VersionedReply>
{
    public Task<IBenzeneResult<VersionedReply>> HandleAsync(VersionedRequest request)
    {
        return Task.FromResult(BenzeneResult.Ok(new VersionedReply { Version = "v2" }));
    }
}
