using System;
using System.IO;
using System.Threading.Tasks;
using Benzene.Abstractions.MessageHandlers;
using Benzene.CodeGen.Cli.Core.Commands.Diff;
using Benzene.Core.MessageHandlers;
using Benzene.Schema.OpenApi.Compatibility;
using Benzene.Schema.OpenApi.EventService;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Extensions;
using Xunit;

namespace Benzene.Test.Autogen.Schema.OpenApi.EventService;

// EventServiceDocumentDeserializer used to keep its EventServiceDocumentBuilder (hence its
// SchemaBuilder/SchemaRepository) as an instance field, never reset between Deserialize() calls.
// SchemaBuilder.AddSchema is first-write-wins, so calling Deserialize() twice on ONE instance with
// two documents that share a schema name (the normal case - a baseline vs. current spec of the same
// evolving service) silently resolved the SECOND document's schema to the FIRST document's old
// definition. That corrupted both `benzene diff` (DiffCommand) and
// SchemaCompatibility.EnsureBackwardCompatible(string,string) - both call Deserialize() twice on one
// shared instance - so a real breaking change (a property removed) was silently reported as no
// change at all.
public class EventServiceDocumentDeserializerTest
{
    // Two distinct CLR types that both produce the schema id "Order" (Swashbuckle's default
    // schema-id selector uses the bare Type.Name, which for a nested type excludes the declaring
    // type) - "BaselineTypes.Order" and "CurrentTypes.Order" collide on schema id even though C#
    // can't have two types literally named "Order" in one namespace. This is exactly the shape of
    // a real evolving service: baseline and current both publish a schema called "Order", and
    // current's shape has changed (Status removed).
    public static class BaselineTypes
    {
        public class Order { public string Id { get; set; } = ""; public string Status { get; set; } = ""; }
    }

    public static class CurrentTypes
    {
        public class Order { public string Id { get; set; } = ""; } // Status removed - breaking
    }

    public class CreateOrder { public string Id { get; set; } = ""; }

    private static string BaselineJson() =>
        new IMessageHandlerDefinition[]
        {
            MessageHandlerDefinition.CreateInstance("order:create", typeof(CreateOrder), typeof(BaselineTypes.Order)),
        }.ToEventServiceDocument().SerializeAsJson(OpenApiSpecVersion.OpenApi3_0);

    private static string CurrentJson() =>
        new IMessageHandlerDefinition[]
        {
            MessageHandlerDefinition.CreateInstance("order:create", typeof(CreateOrder), typeof(CurrentTypes.Order)),
        }.ToEventServiceDocument().SerializeAsJson(OpenApiSpecVersion.OpenApi3_0);

    [Fact]
    public void Deserialize_CalledTwiceOnOneInstance_SecondCallGetsItsOwnSchema_NotTheFirstCalls()
    {
        var baselineJson = BaselineJson();
        var currentJson = CurrentJson();

        // One shared instance, exactly as both DiffCommand and
        // SchemaCompatibility.EnsureBackwardCompatible(string,string) use it.
        var deserializer = new EventServiceDocumentDeserializer();
        var baseline = deserializer.Deserialize(baselineJson);
        var current = deserializer.Deserialize(currentJson);

        // #169: schema property keys are camelCase, matching the wire - "status", not "Status".
        Assert.True(baseline.Components.Schemas["Order"].Properties.ContainsKey("status"));
        // Before the fix: current's "Order" resolved to baseline's first-written definition and
        // still carried "status".
        Assert.False(current.Components.Schemas["Order"].Properties.ContainsKey("status"));
    }

    [Fact]
    public async Task DiffCommand_SharedSchemaNameAcrossTwoDeserializeCalls_DetectsThePropertyRemoval()
    {
        var baselinePath = Path.Combine(Path.GetTempPath(), $"benzene-wp-h-46-diff-baseline-{Guid.NewGuid():N}.spec.json");
        var currentPath = Path.Combine(Path.GetTempPath(), $"benzene-wp-h-46-diff-current-{Guid.NewGuid():N}.spec.json");
        File.WriteAllText(baselinePath, BaselineJson());
        File.WriteAllText(currentPath, CurrentJson());

        try
        {
            var payload = new DiffPayload
            {
                Baseline = baselinePath,
                Current = currentPath,
                FailOn = "breaking",
                Format = "text",
            };

            var exception = await Assert.ThrowsAsync<DiffFailedException>(
                () => new DiffCommand().ExecuteAsync(payload));

            Assert.True(exception.Report.HasBreakingChanges);
            Assert.Contains(exception.Report.Changes,
                x => x.Kind == SchemaChangeKind.PropertyRemoved && x.Compatibility == ChangeCompatibility.Breaking);
        }
        finally
        {
            File.Delete(baselinePath);
            File.Delete(currentPath);
        }
    }

    [Fact]
    public void SchemaCompatibility_EnsureBackwardCompatible_StringOverload_SharedSchemaNameAcrossTwoDeserializeCalls_Throws()
    {
        var baselineJson = BaselineJson();
        var currentJson = CurrentJson();

        // The (string,string) overload internally shares one EventServiceDocumentDeserializer
        // instance across both Deserialize() calls - the exact reuse pattern #46 fixes.
        var exception = Assert.Throws<SchemaCompatibilityException>(
            () => SchemaCompatibility.EnsureBackwardCompatible(baselineJson, currentJson));

        Assert.True(exception.Report.HasBreakingChanges);
        Assert.Contains(exception.Report.BreakingChanges, x => x.Kind == SchemaChangeKind.PropertyRemoved);
    }
}
