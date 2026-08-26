using Amazon.Lambda;
using Amazon.Lambda.Model;
using Benzene.Mesh.Contracts;

namespace Benzene.Mesh.Discovery.Aws;

/// <summary>
/// Discovers Benzene services by enumerating the AWS Lambda functions in the account (paginated
/// <c>ListFunctions</c>), reading each function's tags (<c>ListTags</c>), keeping those that match the
/// <see cref="MeshDiscoveryFilter"/> (by default, carry the <c>benzene</c> tag), and emitting a
/// registry entry bound to the AWS-Lambda-Invoke interrogation source
/// (<see cref="MeshServiceSource.AwsLambdaInvoke"/>) so the existing <c>LambdaMeshServiceSource</c>
/// interrogates each without any HTTP surface.
/// </summary>
/// <remarks>
/// Uses <c>ListFunctions</c> + per-function <c>ListTags</c> only (no ResourceGroupsTagging API), so it
/// needs no dependency beyond the already-approved <c>AWSSDK.Lambda</c>. IAM: <c>lambda:ListFunctions</c>,
/// <c>lambda:ListTags</c>, and <c>lambda:InvokeFunction</c> (for the later interrogation). An optional
/// <c>benzene:mesh-path</c> tag is carried into <c>SourceOptions</c> for services that serve the
/// descriptor at a non-default path.
/// </remarks>
public class AwsLambdaDiscoveryProvider : IMeshDiscoveryProvider
{
    /// <summary>The tag whose value (when present) overrides the mesh descriptor path for a service.</summary>
    public const string MeshPathTag = "benzene:mesh-path";

    /// <summary>
    /// Upper bound on concurrent <c>ListTags</c> calls during discovery. Keeps a large account from
    /// firing hundreds of tag reads at once and hitting the Lambda control-plane's request-rate limit,
    /// while still collapsing the previously-sequential per-function reads into a handful of round-trips.
    /// </summary>
    private const int MaxConcurrentTagReads = 8;

    private readonly IAmazonLambda _lambda;

    /// <summary>Initializes the provider over an AWS Lambda client.</summary>
    /// <param name="lambda">The AWS Lambda client used to list functions and their tags.</param>
    public AwsLambdaDiscoveryProvider(IAmazonLambda lambda)
    {
        _lambda = lambda;
    }

    /// <inheritdoc />
    public string Key => MeshServiceSource.AwsLambdaInvoke;

    /// <inheritdoc />
    public async Task<IReadOnlyList<MeshServiceRegistryEntry>> DiscoverAsync(
        MeshDiscoveryFilter filter, CancellationToken cancellationToken = default)
    {
        // Enumerate every function first (paginated), then read their tags concurrently. The per-function
        // ListTags call was previously awaited one-at-a-time, so discovery cost N sequential round-trips
        // across the whole account - the dominant part of a mesh refresh. Concurrency is bounded so a
        // large account can't fire hundreds of ListTags at once and trip the Lambda control-plane's
        // request-rate limit.
        var functions = new List<FunctionConfiguration>();
        string? marker = null;

        do
        {
            var response = await _lambda.ListFunctionsAsync(
                new ListFunctionsRequest { Marker = marker }, cancellationToken);

            if (response.Functions != null)
            {
                functions.AddRange(response.Functions);
            }

            marker = response.NextMarker;
        }
        while (!string.IsNullOrEmpty(marker));

        using var throttle = new SemaphoreSlim(MaxConcurrentTagReads);
        var tagged = await Task.WhenAll(functions.Select(async function =>
        {
            await throttle.WaitAsync(cancellationToken);
            try
            {
                var tagsResponse = await _lambda.ListTagsAsync(
                    new ListTagsRequest { Resource = function.FunctionArn }, cancellationToken);
                return (function, tags: (IReadOnlyDictionary<string, string>?)(tagsResponse.Tags ?? new Dictionary<string, string>()));
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // A single function's tags being unreadable (deleted between ListFunctions and here,
                // or an access-denied on that one function's ARN) must not lose every other function's
                // result - it can't be tag-matched anyway without its tags, so it's dropped below
                // rather than failing the whole Task.WhenAll (and therefore the whole discovery run).
                return (function, tags: (IReadOnlyDictionary<string, string>?)null);
            }
            finally
            {
                throttle.Release();
            }
        }));

        // Order-preserving (Task.WhenAll keeps source order), so the discovered registry is stable
        // across runs regardless of which tag read completes first.
        var entries = new List<MeshServiceRegistryEntry>();
        foreach (var (function, tags) in tagged)
        {
            if (tags == null || !filter.Matches(tags))
            {
                continue;
            }

            // MeshDiscoveryFilter.Regions is documented "AWS/Azure" region scoping, and
            // MeshDiscoveryRunner passes the SAME filter instance to every provider - so an operator
            // who sets it expects it honored here too, not silently ignored the way it was before this
            // fix (corrected 2026-08-23: found by adversarial review of the mesh discovery backends).
            // The function's region isn't a separate ListFunctions field; it's embedded in FunctionArn
            // (arn:aws:lambda:{region}:{account}:function:{name}), same as every other AWS ARN.
            if (filter.Regions != null && !RegionMatches(function.FunctionArn, filter.Regions))
            {
                continue;
            }

            var options = new Dictionary<string, string> { ["functionName"] = function.FunctionName };
            if (tags.TryGetValue(MeshPathTag, out var meshPath) && !string.IsNullOrWhiteSpace(meshPath))
            {
                options["meshPath"] = meshPath;
            }

            entries.Add(new MeshServiceRegistryEntry(
                function.FunctionName,
                specUrl: string.Empty,
                healthUrl: string.Empty,
                MeshServiceSource.AwsLambdaInvoke,
                options));
        }

        return entries;
    }

    /// <summary>
    /// Whether <paramref name="functionArn"/>'s region segment is one of <paramref name="regions"/>
    /// (case-insensitive, matching <c>AzureAppServiceDiscoveryProvider</c>'s own region comparison). An
    /// ARN whose region can't be read (null/malformed) is never excluded by this check - unknown is not
    /// evidence of "wrong region", the same "fail open on an unreadable dimension, not closed" stance
    /// <c>Benzene.Mesh.Discovery.Azure.AzureAppServiceDiscoveryProvider</c> takes when
    /// <c>resource.Location</c> is null.
    /// </summary>
    private static bool RegionMatches(string? functionArn, IReadOnlyList<string> regions)
    {
        var region = RegionFromArn(functionArn);
        return region == null || regions.Any(r => string.Equals(r, region, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Extracts the region segment from a Lambda ARN (<c>arn:aws:lambda:{region}:{account}:function:{name}</c>).</summary>
    private static string? RegionFromArn(string? arn)
    {
        if (string.IsNullOrEmpty(arn))
        {
            return null;
        }

        var parts = arn.Split(':');
        return parts.Length > 3 && !string.IsNullOrEmpty(parts[3]) ? parts[3] : null;
    }
}
