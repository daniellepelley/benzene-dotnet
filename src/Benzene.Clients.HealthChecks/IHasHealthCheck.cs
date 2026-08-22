using Benzene.Abstractions.Results;
using Benzene.HealthChecks.Core;

namespace Benzene.Clients.HealthChecks
{
    /// <summary>
    /// The seam <see cref="ClientHealthCheck"/> sits on: a client that can call a downstream service's
    /// health check and report its own expected contract hash. <see cref="ServiceHealthCheckClient"/>
    /// is the built-in implementation; hand-write one only when the standard call isn't what you want
    /// (a bespoke transport, a canned response in a demo).
    /// </summary>
    public interface IHasHealthCheck
    {
        /// <summary>Gets this consumer's expected contract hash for the downstream service, for drift comparison.</summary>
        string HashCode { get; }

        /// <summary>Calls the downstream service's health check and returns its (possibly drift-annotated) response.</summary>
        Task<IBenzeneResult<HealthCheckResponse>> HealthCheckAsync();
    }
}
