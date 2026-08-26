using System;
using System.Collections.Generic;
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
    /// diagnostics path, <c>work/bug-fix-designs-2026-08.md</c> (WP-5a) and
    /// <c>work/archive/bug-fix-designs-round7-10-2026-08.md</c> (WP-C).
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

            // A Function name must be unique ACROSS THE WHOLE APP, and Azure Functions doesn't know or
            // care which binding produced it - round 6 proved the collision is cross-transport (a
            // BenzeneQueueTrigger(Name="dup") and a BenzeneKafkaTrigger(Name="dup") collide just the
            // same as two queue triggers named "dup" would). So collision detection stays a GLOBAL view
            // over every transport's triggers, computed here...
            //
            // ...but emission does NOT: each transport gets its OWN RegisterSourceOutput below (restored
            // from the pre-WP-5-merge shape - WP-C, #38's incrementality-regression half). A transport's
            // own class names can never collide with another transport's (each appends its own distinct
            // suffix - "…HttpFunction", "…ServiceBusFunction", … - see each Read() below), so
            // per-transport emission needs no cross-transport class-name coordination either. This
            // restores the incremental granularity WP-5's merge-into-one-array lost: an edit to one
            // transport's declarations no longer forces every OTHER transport to re-emit - see
            // RegisterTransport's doc comment for the (non-obvious) comparer this actually depends on.
            //
            // #32: the collision view is computed over the FULL declared set, INCLUDING an entry that
            // carries its own blocking diagnostic (e.g. a CosmosDb trigger missing DocumentType) - so a
            // collision where one side is broken still reports BENZ0001 for the other side instead of
            // being masked by the broken side's own diagnostic. TriggerInfo.ForDiagnostic always
            // records the attempted FunctionNameLiteral for exactly this reason.
            var allTriggers = Merge(http, serviceBus, eventHub, kafka, queueStorage, blobStorage, eventGrid, cosmosDb, timer);
            var duplicateNames = allTriggers
                .Select(static (triggers, _) => ComputeDuplicateNames(triggers))
                .WithComparer(SequenceEqualComparer.Instance);

            // Tracking names exist purely so a test can assert the incremental granularity this
            // restores (GeneratorDriverRunResult.Results[i].TrackedSteps) - they have no effect on
            // emitted output.
            RegisterTransport(context, "http", http, duplicateNames);
            RegisterTransport(context, "serviceBus", serviceBus, duplicateNames);
            RegisterTransport(context, "eventHub", eventHub, duplicateNames);
            RegisterTransport(context, "kafka", kafka, duplicateNames);
            RegisterTransport(context, "queueStorage", queueStorage, duplicateNames);
            RegisterTransport(context, "blobStorage", blobStorage, duplicateNames);
            RegisterTransport(context, "eventGrid", eventGrid, duplicateNames);
            RegisterTransport(context, "cosmosDb", cosmosDb, duplicateNames);
            RegisterTransport(context, "timer", timer, duplicateNames);
        }

        private static IncrementalValuesProvider<TriggerInfo> MakeProvider(
            IncrementalGeneratorInitializationContext context,
            string attributeMetadataName,
            Func<GeneratorAttributeSyntaxContext, ImmutableArray<TriggerInfo>> read)
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

        /// <summary>The set of Function-name literals used by more than one declared trigger (§32/§38 - see Initialize).</summary>
        private static ImmutableArray<string> ComputeDuplicateNames(ImmutableArray<TriggerInfo> triggers)
        {
            if (triggers.IsDefaultOrEmpty)
            {
                return ImmutableArray<string>.Empty;
            }

            return triggers
                .Where(static t => t.FunctionNameLiteral.Length > 0)
                .GroupBy(static t => t.FunctionNameLiteral, StringComparer.Ordinal)
                .Where(static g => g.Count() > 1)
                .Select(static g => g.Key)
                .OrderBy(static k => k, StringComparer.Ordinal)
                .ToImmutableArray();
        }

        /// <summary>
        /// Registers ONE transport's own <c>RegisterSourceOutput</c>: its own triggers, combined with
        /// the (small, globally-computed) set of colliding Function names.
        ///
        /// Two comparers do the actual incrementality work here, and both are load-bearing -
        /// <c>ImmutableArray&lt;T&gt;</c>'s own <see cref="IEquatable{T}"/> implementation compares the
        /// underlying array by REFERENCE, not content, so without them EVERY node below would report
        /// "changed" on every single run regardless of whether anything relevant actually changed,
        /// silently defeating per-transport registration entirely (confirmed live: without
        /// <see cref="TriggerInfoSequenceComparer"/>, editing one transport still forced every other
        /// transport's <c>RegisterSourceOutput</c> callback to re-run - the exact regression this
        /// restructure exists to fix):
        /// - <see cref="TriggerInfoSequenceComparer"/> on <c>transport.Collect()</c>: this transport's
        ///   own triggers, so an edit elsewhere (including to another transport) that leaves THIS
        ///   transport's own declarations unchanged doesn't count as a change here.
        /// - <see cref="SequenceEqualComparer"/> on <c>duplicateNames</c> (installed where it's built, in
        ///   <see cref="Initialize"/>): the globally-computed set of colliding names, so a change to
        ///   another transport that doesn't alter which names collide doesn't propagate into this
        ///   transport's combine either, even though the global view underneath it was recomputed.
        /// </summary>
        private static void RegisterTransport(
            IncrementalGeneratorInitializationContext context,
            string trackingName,
            IncrementalValuesProvider<TriggerInfo> transport,
            IncrementalValueProvider<ImmutableArray<string>> duplicateNames)
        {
            var collected = transport.Collect().WithComparer(TriggerInfoSequenceComparer.Instance);
            var combined = collected.Combine(duplicateNames).WithTrackingName(trackingName);
            context.RegisterSourceOutput(combined, static (spc, pair) => Execute(spc, pair.Left, pair.Right));
        }

        private static void Execute(SourceProductionContext context, ImmutableArray<TriggerInfo> triggers, ImmutableArray<string> duplicateNames)
        {
            if (triggers.IsDefaultOrEmpty)
            {
                return;
            }

            // Class names only need to be unique WITHIN this transport's own output - each transport's
            // ClassName carries its own distinct suffix (e.g. "…QueueFunction" vs "…KafkaFunction"), so
            // two different transports can never collide here even though each now runs its own
            // RegisterSourceOutput independently.
            var usedClassNames = new HashSet<string>();

            foreach (var trigger in triggers)
            {
                // A Function name must be unique across the whole app; deliberately NOT auto-renamed
                // the way the class name below is - see DiagnosticDescriptors.DuplicateFunctionName.
                var isDuplicate = trigger.FunctionNameLiteral.Length > 0 && duplicateNames.Contains(trigger.FunctionNameLiteral);
                if (isDuplicate)
                {
                    context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.DuplicateFunctionName, trigger.Location, trigger.FunctionNameLiteral));
                }

                if (trigger.BlockingDiagnostic is { } blocking)
                {
                    // e.g. BENZ0002.../BENZ0008 - a transport reader couldn't produce a valid trigger
                    // and asked for this to be reported instead of silently dropping the declaration.
                    context.ReportDiagnostic(blocking.ToDiagnostic(trigger.Location));
                    continue;
                }

                if (isDuplicate)
                {
                    // Neither colliding declaration is emitted - which one would be "correct" to keep
                    // is exactly the ambiguity the user needs to resolve, so the generator doesn't guess.
                    continue;
                }

                foreach (var advisory in trigger.AdvisoryDiagnostics)
                {
                    // e.g. BENZ0009 - non-blocking: the trigger below is still generated.
                    context.ReportDiagnostic(advisory.ToDiagnostic(trigger.Location));
                }

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

        private static string Unique(HashSet<string> used, string candidate)
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

        /// <summary>Sequence-equality comparer so a content-unchanged duplicate-name set doesn't invalidate the per-transport combine (see <see cref="RegisterTransport"/>).</summary>
        private sealed class SequenceEqualComparer : IEqualityComparer<ImmutableArray<string>>
        {
            public static readonly SequenceEqualComparer Instance = new();

            public bool Equals(ImmutableArray<string> x, ImmutableArray<string> y) =>
                x.IsDefaultOrEmpty ? y.IsDefaultOrEmpty : !y.IsDefaultOrEmpty && x.SequenceEqual(y, StringComparer.Ordinal);

            public int GetHashCode(ImmutableArray<string> obj)
            {
                if (obj.IsDefaultOrEmpty)
                {
                    return 0;
                }

                unchecked
                {
                    var hash = 17;
                    foreach (var s in obj)
                    {
                        hash = hash * 31 + s.GetHashCode();
                    }

                    return hash;
                }
            }
        }

        /// <summary>Sequence-equality comparer so a content-unchanged transport doesn't invalidate its own <c>RegisterSourceOutput</c> combine (see <see cref="RegisterTransport"/>).</summary>
        private sealed class TriggerInfoSequenceComparer : IEqualityComparer<ImmutableArray<TriggerInfo>>
        {
            public static readonly TriggerInfoSequenceComparer Instance = new();

            public bool Equals(ImmutableArray<TriggerInfo> x, ImmutableArray<TriggerInfo> y) =>
                x.IsDefaultOrEmpty ? y.IsDefaultOrEmpty : !y.IsDefaultOrEmpty && x.SequenceEqual(y);

            public int GetHashCode(ImmutableArray<TriggerInfo> obj)
            {
                if (obj.IsDefaultOrEmpty)
                {
                    return 0;
                }

                unchecked
                {
                    var hash = 17;
                    foreach (var t in obj)
                    {
                        hash = hash * 31 + t.GetHashCode();
                    }

                    return hash;
                }
            }
        }
    }
}
