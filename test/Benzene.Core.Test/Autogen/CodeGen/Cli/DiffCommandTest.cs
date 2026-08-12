using System;
using System.IO;
using System.Threading.Tasks;
using Benzene.Abstractions.MessageHandlers;
using Benzene.CodeGen.Cli.Core.Commands.Diff;
using Benzene.Core.MessageHandlers;
using Benzene.Schema.OpenApi.EventService;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Extensions;
using Xunit;

namespace Benzene.Test.Autogen.CodeGen.Cli;

// `benzene diff` wraps SchemaCompatibility.Compare/SchemaCompatibilityComparer over two files on
// disk and turns the report into a CI-friendly exit code: throws (-> non-zero, per Phase 2's
// fail-loud CLI) when the report trips --fail-on, otherwise returns normally (-> exit 0).
public class DiffCommandTest
{
    public class CreateOrderV1 { public string Id { get; set; } = ""; public string Note { get; set; } = ""; }

    // Note removed from the request - a Warning (not Breaking): the service just ignores a field an
    // old client still sends.
    public class CreateOrderV2 { public string Id { get; set; } = ""; }

    public class OrderV1 { public string Id { get; set; } = ""; public string Status { get; set; } = ""; }

    // Additive: v1 + an optional field.
    public class OrderV1Plus { public string Id { get; set; } = ""; public string Status { get; set; } = ""; public string Notes { get; set; } = ""; }

    // Breaking: Status removed from the response - an existing client reading Status breaks.
    public class OrderV2 { public string Id { get; set; } = ""; }

    private static string WriteContract(Type requestType, Type responseType)
    {
        var document = new IMessageHandlerDefinition[]
        {
            MessageHandlerDefinition.CreateInstance("order:create", requestType, responseType),
        }.ToEventServiceDocument();

        var path = Path.Combine(Path.GetTempPath(), $"benzene-diff-{Guid.NewGuid():N}.spec.json");
        File.WriteAllText(path, document.SerializeAsJson(OpenApiSpecVersion.OpenApi3_0));
        return path;
    }

    private static async Task RunAsync(string baseline, string current, Action<DiffPayload> configure = null)
    {
        var payload = new DiffPayload
        {
            Baseline = baseline,
            Current = current,
            FailOn = "breaking",
            Format = "text",
        };
        configure?.Invoke(payload);

        await new DiffCommand().ExecuteAsync(payload);
    }

    [Fact]
    public async Task ExecuteAsync_AdditiveChange_DoesNotThrow()
    {
        var baseline = WriteContract(typeof(CreateOrderV1), typeof(OrderV1));
        var current = WriteContract(typeof(CreateOrderV1), typeof(OrderV1Plus));
        try
        {
            await RunAsync(baseline, current);
        }
        finally
        {
            File.Delete(baseline);
            File.Delete(current);
        }
    }

    [Fact]
    public async Task ExecuteAsync_BreakingChange_DefaultFailOnBreaking_Throws()
    {
        var baseline = WriteContract(typeof(CreateOrderV1), typeof(OrderV1));
        var current = WriteContract(typeof(CreateOrderV1), typeof(OrderV2)); // Status removed
        try
        {
            var exception = await Assert.ThrowsAsync<DiffFailedException>(() => RunAsync(baseline, current));
            Assert.True(exception.Report.HasBreakingChanges);
        }
        finally
        {
            File.Delete(baseline);
            File.Delete(current);
        }
    }

    [Fact]
    public async Task ExecuteAsync_BreakingChange_WarnOnly_DoesNotThrow()
    {
        var baseline = WriteContract(typeof(CreateOrderV1), typeof(OrderV1));
        var current = WriteContract(typeof(CreateOrderV1), typeof(OrderV2)); // Status removed - breaking
        try
        {
            await RunAsync(baseline, current, payload => payload.WarnOnly = "true");
        }
        finally
        {
            File.Delete(baseline);
            File.Delete(current);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WarningOnlyChange_FailOnBreaking_DoesNotThrow()
    {
        // Note removed from the request is a Warning, not Breaking - --fail-on breaking (the
        // default) tolerates it.
        var baseline = WriteContract(typeof(CreateOrderV1), typeof(OrderV1));
        var current = WriteContract(typeof(CreateOrderV2), typeof(OrderV1));
        try
        {
            await RunAsync(baseline, current, payload => payload.FailOn = "breaking");
        }
        finally
        {
            File.Delete(baseline);
            File.Delete(current);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WarningOnlyChange_FailOnWarning_Throws()
    {
        var baseline = WriteContract(typeof(CreateOrderV1), typeof(OrderV1));
        var current = WriteContract(typeof(CreateOrderV2), typeof(OrderV1));
        try
        {
            var exception = await Assert.ThrowsAsync<DiffFailedException>(
                () => RunAsync(baseline, current, payload => payload.FailOn = "warning"));
            Assert.False(exception.Report.HasBreakingChanges);
            Assert.True(exception.Report.HasWarnings);
        }
        finally
        {
            File.Delete(baseline);
            File.Delete(current);
        }
    }

    [Fact]
    public async Task ExecuteAsync_JsonFormat_Throws_AndWritesParseableJsonBeforeThrowing()
    {
        var baseline = WriteContract(typeof(CreateOrderV1), typeof(OrderV1));
        var current = WriteContract(typeof(CreateOrderV1), typeof(OrderV2)); // breaking
        try
        {
            var originalOut = Console.Out;
            using var capturedOut = new StringWriter();
            Console.SetOut(capturedOut);
            try
            {
                await Assert.ThrowsAsync<DiffFailedException>(
                    () => RunAsync(baseline, current, payload => payload.Format = "json"));
            }
            finally
            {
                Console.SetOut(originalOut);
            }

            // Substring checks, not a strict JToken.Parse of the whole capture: Console.Out is a
            // process-wide global and xunit runs other test classes concurrently, so an unrelated
            // test writing to Console at the same moment can interleave into this capture (same
            // reasoning CommandRouterTest/HelpCommandTest already follow for their Console capture).
            var output = capturedOut.ToString();
            Assert.Contains("\"Kind\": \"PropertyRemoved\"", output);
            Assert.Contains("\"HasBreakingChanges\": true", output);
        }
        finally
        {
            File.Delete(baseline);
            File.Delete(current);
        }
    }

    [Fact]
    public async Task ExecuteAsync_MissingBaseline_Throws()
    {
        var current = WriteContract(typeof(CreateOrderV1), typeof(OrderV1));
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() => RunAsync(null, current));
        }
        finally
        {
            File.Delete(current);
        }
    }

    [Fact]
    public async Task ExecuteAsync_MissingCurrent_Throws()
    {
        var baseline = WriteContract(typeof(CreateOrderV1), typeof(OrderV1));
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() => RunAsync(baseline, null));
        }
        finally
        {
            File.Delete(baseline);
        }
    }

    [Fact]
    public async Task ExecuteAsync_InvalidFailOn_Throws()
    {
        var baseline = WriteContract(typeof(CreateOrderV1), typeof(OrderV1));
        var current = WriteContract(typeof(CreateOrderV1), typeof(OrderV1));
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(
                () => RunAsync(baseline, current, payload => payload.FailOn = "bogus"));
        }
        finally
        {
            File.Delete(baseline);
            File.Delete(current);
        }
    }

    [Fact]
    public async Task ExecuteAsync_IdenticalContract_DoesNotThrow()
    {
        var baseline = WriteContract(typeof(CreateOrderV1), typeof(OrderV1));
        var current = WriteContract(typeof(CreateOrderV1), typeof(OrderV1));
        try
        {
            await RunAsync(baseline, current);
        }
        finally
        {
            File.Delete(baseline);
            File.Delete(current);
        }
    }
}
