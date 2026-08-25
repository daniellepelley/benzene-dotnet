using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Benzene.Azure.Function.SourceGenerators
{
    /// <summary>
    /// Emits the Azure Functions <c>[Function]</c>/<c>[…Trigger]</c> boilerplate class per transport
    /// from a user-authored assembly-attribute declaration (e.g.
    /// <c>[assembly: BenzeneHttpTrigger(Name = "orders", Route = "…")]</c>), forwarding each trigger
    /// invocation into the built <c>IAzureFunctionApp</c>. The user declares <em>what</em> triggers
    /// they want (and their bindings — route, queue, hub, …); the generator writes the ceremony.
    /// See <c>work/archive/azure-functions-trigger-codegen-design-2026-08.md</c> and, for the
    /// diagnostics path, <c>work/bug-fix-designs-2026-08.md</c> (WP-5a).
    /// </summary>
    [Generator]
    public class AzureFunctionTriggerGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // One provider per transport, each reading its own assembly attribute into
            // value-equatable TriggerInfos (or, for a reader that hit a problem it needs to report -
            // e.g. BENZ0002 - a diagnostic-only TriggerInfo; see TriggerInfo.ForDiagnostic).
            var http = MakeProvider(context, Http.AttributeName, Http.Read);
            var serviceBus = MakeProvider(context, ServiceBus.AttributeName, ServiceBus.Read);
            var eventHub = MakeProvider(context, EventHub.AttributeName, EventHub.Read);
            var kafka = MakeProvider(context, Kafka.AttributeName, Kafka.Read);
            var queueStorage = MakeProvider(context, QueueStorage.AttributeName, QueueStorage.Read);
            var blobStorage = MakeProvider(context, BlobStorage.AttributeName, BlobStorage.Read);
            var eventGrid = MakeProvider(context, EventGrid.AttributeName, EventGrid.Read);
            var cosmosDb = MakeProvider(context, CosmosDb.AttributeName, CosmosDb.Read);
            var timer = MakeProvider(context, Timer.AttributeName, Timer.Read);

            // Every transport's triggers merge into ONE array before Execute runs, rather than each
            // transport getting its own RegisterSourceOutput (as before this change). A per-transport
            // output could only ever see its own triggers, so it could never catch a Function name
            // shared *across* transports - and round 6 proved that collision is real: a
            // BenzeneQueueTrigger(Name="dup") and a BenzeneKafkaTrigger(Name="dup") in the same
            // compilation collide just the same as two queue triggers named "dup" would (BENZ0001).
            var allTriggers = Merge(http, serviceBus, eventHub, kafka, queueStorage, blobStorage, eventGrid, cosmosDb, timer);

            context.RegisterSourceOutput(allTriggers, static (spc, triggers) => Execute(spc, triggers));
        }

        private static IncrementalValuesProvider<TriggerInfo> MakeProvider(
            IncrementalGeneratorInitializationContext context,
            string attributeMetadataName,
            System.Func<GeneratorAttributeSyntaxContext, ImmutableArray<TriggerInfo>> read)
        {
            return context.SyntaxProvider
                .ForAttributeWithMetadataName(
                    attributeMetadataName,
                    predicate: static (_, _) => true,
                    transform: (ctx, _) => read(ctx))
                .SelectMany(static (items, _) => items);
        }

        /// <summary>Collects and concatenates every transport's provider into one array, in the given order.</summary>
        private static IncrementalValueProvider<ImmutableArray<TriggerInfo>> Merge(
            params IncrementalValuesProvider<TriggerInfo>[] providers)
        {
            var combined = providers[0].Collect();
            for (var i = 1; i < providers.Length; i++)
            {
                var next = providers[i].Collect();
                combined = combined.Combine(next).Select(static (pair, _) => pair.Left.AddRange(pair.Right));
            }

            return combined;
        }

        private static void Execute(SourceProductionContext context, ImmutableArray<TriggerInfo> triggers)
        {
            if (triggers.IsDefaultOrEmpty)
            {
                return;
            }

            var candidates = new System.Collections.Generic.List<TriggerInfo>(triggers.Length);
            foreach (var trigger in triggers)
            {
                if (trigger.PendingDiagnostic is { } diagnostic)
                {
                    // e.g. BENZ0002 - a transport reader couldn't produce a valid trigger and asked
                    // for this to be reported instead of silently dropping the declaration.
                    context.ReportDiagnostic(diagnostic);
                    continue;
                }

                candidates.Add(trigger);
            }

            // A Function name must be unique across the whole app; a class name must be unique in the
            // generated namespace. The class name is deduped deterministically (auto-uniquified below)
            // since it's an internal implementation detail nothing outside the generated assembly sees.
            //
            // The Function name is NOT auto-uniquified the same way (this is a deliberate, recorded
            // decision - see DiagnosticDescriptors.DuplicateFunctionName / BENZ0001): it's externally
            // meaningful (bindings, host.json, scale rules, the portal's identity for the function), so
            // silently picking a different one would only move the failure from build time to
            // deployment time. Two or more triggers sharing a name is reported and none of them are
            // emitted - which one would be "correct" to keep is exactly the ambiguity the user needs to
            // resolve, so guessing would just trade one silent problem for another.
            var usedClassNames = new System.Collections.Generic.HashSet<string>();

            foreach (var group in candidates.GroupBy(t => t.FunctionNameLiteral))
            {
                var duplicates = group.ToList();
                if (duplicates.Count > 1)
                {
                    foreach (var duplicate in duplicates)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            DiagnosticDescriptors.DuplicateFunctionName,
                            duplicate.Location,
                            duplicate.FunctionNameLiteral));
                    }

                    continue;
                }

                var trigger = duplicates[0];
                var className = Unique(usedClassNames, trigger.ClassName);

                var sb = new StringBuilder();
                sb.AppendLine("// <auto-generated/>");
                sb.AppendLine("#nullable enable");
                sb.AppendLine("namespace Benzene.Azure.Function.Generated");
                sb.AppendLine("{");
                sb.AppendLine($"    public sealed class {className}");
                sb.AppendLine("    {");
                sb.AppendLine("        private readonly global::Benzene.Azure.Function.Core.IAzureFunctionApp _app;");
                sb.AppendLine($"        public {className}(global::Benzene.Azure.Function.Core.IAzureFunctionApp app) => _app = app;");
                sb.AppendLine();
                sb.AppendLine($"        [global::Microsoft.Azure.Functions.Worker.Function({trigger.FunctionNameLiteral})]");
                sb.AppendLine($"        public {trigger.ReturnType} Run(");
                sb.AppendLine($"            {trigger.ParameterList})");
                sb.AppendLine($"            => {trigger.DispatchExpression};");
                sb.AppendLine("    }");
                sb.AppendLine("}");

                context.AddSource($"{className}.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
            }
        }

        private static string Unique(System.Collections.Generic.HashSet<string> used, string candidate)
        {
            if (used.Add(candidate))
            {
                return candidate;
            }

            var i = 2;
            while (!used.Add(candidate + i))
            {
                i++;
            }

            return candidate + i;
        }
    }
}
