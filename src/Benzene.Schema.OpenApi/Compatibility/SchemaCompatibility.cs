using Benzene.Schema.OpenApi.EventService;

namespace Benzene.Schema.OpenApi.Compatibility;

/// <summary>
/// Entry point for comparing two versions of a service's message contract for backward
/// compatibility. <see cref="Compare(EventServiceDocument, EventServiceDocument)"/> reports every
/// change; <see cref="EnsureBackwardCompatible(EventServiceDocument, EventServiceDocument, SchemaCompatibilityRules?)"/>
/// turns that into a pass/fail gate that throws on breaking changes - drop it into a test to fail
/// CI when a service's contract stops being backward compatible with a committed baseline.
/// </summary>
public static class SchemaCompatibility
{
    /// <summary>Compares two contracts using the default rules and returns every change.</summary>
    /// <param name="baseline">The previous/committed contract.</param>
    /// <param name="current">The current contract.</param>
    /// <returns>The comparison report.</returns>
    public static SchemaCompatibilityReport Compare(EventServiceDocument baseline, EventServiceDocument current) =>
        new SchemaCompatibilityComparer().Compare(baseline, current);

    /// <summary>Compares two contracts using custom rules and returns every change.</summary>
    /// <param name="baseline">The previous/committed contract.</param>
    /// <param name="current">The current contract.</param>
    /// <param name="rules">The rules classifying each kind of change.</param>
    /// <returns>The comparison report.</returns>
    public static SchemaCompatibilityReport Compare(EventServiceDocument baseline, EventServiceDocument current,
        SchemaCompatibilityRules rules) =>
        new SchemaCompatibilityComparer(rules).Compare(baseline, current);

    /// <summary>
    /// Compares the <paramref name="current"/> contract against the <paramref name="baseline"/> and
    /// throws <see cref="SchemaCompatibilityException"/> if there are any breaking changes; otherwise
    /// returns the report (which may still contain additive changes and warnings).
    /// </summary>
    /// <param name="baseline">The previous/committed contract.</param>
    /// <param name="current">The current contract.</param>
    /// <param name="rules">Optional rules; defaults to <see cref="SchemaCompatibilityRules.Default"/>.</param>
    /// <returns>The comparison report, when there are no breaking changes.</returns>
    /// <exception cref="SchemaCompatibilityException">The current contract has breaking changes.</exception>
    public static SchemaCompatibilityReport EnsureBackwardCompatible(EventServiceDocument baseline,
        EventServiceDocument current, SchemaCompatibilityRules? rules = null)
    {
        var report = rules == null ? Compare(baseline, current) : Compare(baseline, current, rules);

        if (report.HasBreakingChanges)
        {
            throw new SchemaCompatibilityException(report);
        }

        return report;
    }

    /// <summary>
    /// Loads a baseline contract from its serialized JSON (e.g. a committed <c>spec.json</c>) and
    /// checks the <paramref name="current"/> contract is backward compatible with it.
    /// </summary>
    /// <param name="baselineJson">The baseline contract's serialized JSON.</param>
    /// <param name="current">The current contract (e.g. built from the service's handlers).</param>
    /// <param name="rules">Optional rules; defaults to <see cref="SchemaCompatibilityRules.Default"/>.</param>
    /// <returns>The comparison report, when there are no breaking changes.</returns>
    /// <exception cref="SchemaCompatibilityException">The current contract has breaking changes.</exception>
    /// <exception cref="LegacyBaselineException">The baseline is a legacy-shape document (no <c>schemaDialect</c>).</exception>
    public static SchemaCompatibilityReport EnsureBackwardCompatible(string baselineJson,
        EventServiceDocument current, SchemaCompatibilityRules? rules = null) =>
        EnsureBackwardCompatible(ReadBaseline(baselineJson), current, rules);

    /// <summary>
    /// Loads both the baseline and current contracts from their serialized JSON and checks the
    /// current is backward compatible with the baseline.
    /// </summary>
    /// <param name="baselineJson">The baseline contract's serialized JSON.</param>
    /// <param name="currentJson">The current contract's serialized JSON.</param>
    /// <param name="rules">Optional rules; defaults to <see cref="SchemaCompatibilityRules.Default"/>.</param>
    /// <returns>The comparison report, when there are no breaking changes.</returns>
    /// <exception cref="SchemaCompatibilityException">The current contract has breaking changes.</exception>
    /// <exception cref="LegacyBaselineException">The baseline is a legacy-shape document (no <c>schemaDialect</c>).</exception>
    public static SchemaCompatibilityReport EnsureBackwardCompatible(string baselineJson, string currentJson,
        SchemaCompatibilityRules? rules = null)
    {
        RefuseLegacyBaseline(baselineJson);
        var deserializer = new EventServiceDocumentDeserializer();
        return EnsureBackwardCompatible(deserializer.Deserialize(baselineJson), deserializer.Deserialize(currentJson), rules);
    }

    /// <summary>
    /// Reads a baseline with the conformant parse - after first refusing a legacy-shape baseline (no
    /// <c>schemaDialect</c>) with the actionable <see cref="LegacyBaselineException"/> ("re-baseline")
    /// rather than the parse's generic unknown-dialect error.
    /// </summary>
    /// <exception cref="LegacyBaselineException">The baseline is a legacy-shape document.</exception>
    public static EventServiceDocument ReadBaseline(string baselineJson)
    {
        RefuseLegacyBaseline(baselineJson);
        return new EventServiceDocumentDeserializer().Deserialize(baselineJson);
    }

    private static void RefuseLegacyBaseline(string baselineJson)
    {
        if (EventServiceDocumentDeserializer.IsLegacyShape(baselineJson))
        {
            throw new LegacyBaselineException();
        }
    }
}
