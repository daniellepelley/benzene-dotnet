using System.Collections.Generic;
using Benzene.Aws.Lambda.Core.TestHelpers;
using Benzene.Examples.Versioning.Services;
using Benzene.Testing;

namespace Benzene.Examples.Versioning.Tests;

/// <summary>
/// Boots the example's real <see cref="StartUp"/> into an in-memory AWS Lambda host - the same
/// construction a deployed function performs - so every test drives the actual versioning pipeline end
/// to end via <see cref="AwsLambdaBenzeneTestHost"/>. The shared <see cref="IProcessedLog"/> is reset per
/// test so fire-and-forget assertions don't leak between them.
/// </summary>
public abstract class VersioningTestBase
{
    protected readonly AwsLambdaBenzeneTestHost TestLambdaHosting;

    protected VersioningTestBase()
    {
        InMemoryProcessedLog.Clear();
        TestLambdaHosting = new AwsLambdaBenzeneTestHost(
            BenzeneTestHost.Create<StartUp>().BuildAwsLambdaHost());
    }

    protected static IReadOnlyList<string> ProcessedEntries => new InMemoryProcessedLog().Entries;
}
