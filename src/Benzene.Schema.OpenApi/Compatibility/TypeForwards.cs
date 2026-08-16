using System.Runtime.CompilerServices;
using Benzene.Schema.OpenApi.Compatibility;

// The compatibility taxonomy and rule table moved to Benzene.Schema.Compatibility so components that
// only need to classify a change - the mesh aggregator, above all - do not inherit an OpenAPI
// toolchain to do it. The namespace did not change, so source compatibility is untouched; these
// forwards keep already-compiled assemblies that reference these types through
// Benzene.Schema.OpenApi working as well.
[assembly: TypeForwardedTo(typeof(SchemaChangeKind))]
[assembly: TypeForwardedTo(typeof(SchemaDirection))]
[assembly: TypeForwardedTo(typeof(ChangeCompatibility))]
[assembly: TypeForwardedTo(typeof(SchemaChange))]
[assembly: TypeForwardedTo(typeof(SchemaCompatibilityReport))]
[assembly: TypeForwardedTo(typeof(SchemaCompatibilityRules))]
[assembly: TypeForwardedTo(typeof(JsonSchemaComparer))]
