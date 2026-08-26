using Benzene.Abstractions.Serialization;

namespace Benzene.Cache.Core;

#nullable enable

/// <summary>
/// The <see cref="ISerializer"/> used by <see cref="CacheWriteActions{T}"/> and any concrete provider
/// (e.g. <c>RedisCacheService</c>) when nothing DI-injects an <see cref="ISerializer"/> of its own. One
/// shared, process-wide <c>System.Text.Json</c>-backed instance - not allocated fresh per cache-entry
/// or per-service instance (#145).
/// </summary>
public static class CacheSerializerDefaults
{
    public static ISerializer Serializer { get; } = new Benzene.Core.MessageHandlers.Serialization.JsonSerializer();
}
