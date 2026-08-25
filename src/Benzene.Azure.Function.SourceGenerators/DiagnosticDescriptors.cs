using Microsoft.CodeAnalysis;

namespace Benzene.Azure.Function.SourceGenerators
{
    /// <summary>
    /// Every diagnostic <see cref="AzureFunctionTriggerGenerator"/> (or a transport reader under
    /// <c>Transports/</c>) can report, in one place. Future generator complaints should add a
    /// descriptor here rather than inventing a new reporting mechanism - see
    /// <c>work/bug-fix-designs-2026-08.md</c>, WP-5a.
    /// </summary>
    internal static class DiagnosticDescriptors
    {
        private const string Category = "Benzene.SourceGenerators";

        /// <summary>
        /// Two (or more) generated triggers would emit the same <c>[Function(name)]</c> literal. This
        /// is checked globally across every transport - not per-transport - because Azure Functions
        /// doesn't know or care which binding produced the name: a
        /// <c>BenzeneQueueTrigger(Name = "dup")</c> and a <c>BenzeneKafkaTrigger(Name = "dup")</c> in
        /// the same compilation collide exactly as two queue triggers named "dup" would.
        ///
        /// Deliberately NOT auto-renamed the way the generated class name is: a Function name is
        /// externally meaningful (bindings, host.json, scale rules, the portal's identity for the
        /// function), so silently picking a different one would only move the failure from build time
        /// to deployment time. The name is the user's to fix.
        /// </summary>
        public static readonly DiagnosticDescriptor DuplicateFunctionName = new(
            id: "BENZ0001",
            title: "Duplicate Azure Function name",
            messageFormat:
                "Azure Function name {0} is used by more than one Benzene trigger declaration in this " +
                "compilation. Function names must be unique across the app (Azure Functions doesn't " +
                "distinguish which transport produced the name), so this trigger was not generated - " +
                "give it a distinct Name.",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        /// <summary>
        /// A <c>[assembly: BenzeneCosmosDbTrigger(...)]</c> declaration is missing the required
        /// <c>DocumentType</c> (the change-feed binding is generic over it). Previously this was
        /// silently skipped - a declared trigger that is silently not generated is the worst outcome,
        /// so it's reported and fails the build instead.
        /// </summary>
        public static readonly DiagnosticDescriptor CosmosDbTriggerMissingDocumentType = new(
            id: "BENZ0002",
            title: "CosmosDb trigger missing DocumentType",
            messageFormat:
                "[assembly: BenzeneCosmosDbTrigger(Name = {0}, ...)] does not set DocumentType. The " +
                "Cosmos DB change-feed trigger is generic over the document type and can't be " +
                "generated without it - set DocumentType = typeof(YourDocument).",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);
    }
}
