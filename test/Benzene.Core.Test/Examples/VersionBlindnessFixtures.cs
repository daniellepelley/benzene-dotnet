using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Results;

namespace Benzene.Test.Examples;

// Shared fixtures for WP-P (work/archive/bug-fix-designs-round7-10-2026-08.md) regression tests: a topic
// with two registered handler versions whose request types are genuinely different shapes, so a
// version-blind lookup is observably wrong rather than accidentally correct. When these were written
// the selector fell back to the max-by-ordinal version ("v2") for an unversioned lookup; under the
// spec's selection rule (core-concepts §2) the topic has no default handler, so an unversioned lookup
// finds nothing at all. Either way a request that declares "v1" but is (incorrectly) looked up
// unversioned does not reach the V1 handler/schema - exactly the failure mode #69/#70 describe.
public static class VersionBlindnessDefaults
{
    public const string Topic = "test:version-blindness";
}

public class VersionBlindnessV1Request
{
    public int Id { get; set; }
}

public class VersionBlindnessV2Request
{
    [Required]
    public string Id { get; set; } = string.Empty;
}

[Message(VersionBlindnessDefaults.Topic, "v1")]
public class VersionBlindnessV1Handler : IMessageHandler<VersionBlindnessV1Request, Void>
{
    public Task<IBenzeneResult<Void>> HandleAsync(VersionBlindnessV1Request request)
    {
        return Task.FromResult(BenzeneResult.Ok(new Void()));
    }
}

[Message(VersionBlindnessDefaults.Topic, "v2")]
public class VersionBlindnessV2Handler : IMessageHandler<VersionBlindnessV2Request, Void>
{
    public Task<IBenzeneResult<Void>> HandleAsync(VersionBlindnessV2Request request)
    {
        return Task.FromResult(BenzeneResult.Ok(new Void()));
    }
}
