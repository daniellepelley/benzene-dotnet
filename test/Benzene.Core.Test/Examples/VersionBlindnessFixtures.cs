using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Results;

namespace Benzene.Test.Examples;

// Shared fixtures for WP-P (work/bug-fix-designs-round7-10-2026-08.md) regression tests: a topic
// with two registered handler versions whose request types are genuinely different shapes, so a
// version-blind lookup (falling back to VersionSelector's unversioned max-by-ordinal default) is
// observably wrong rather than accidentally correct. "v2" sorts higher than "v1" by ordinal
// comparison, so a request that declares "v1" but is (incorrectly) looked up unversioned would
// resolve to the V2 handler/schema instead - exactly the failure mode #69/#70 describe.
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
