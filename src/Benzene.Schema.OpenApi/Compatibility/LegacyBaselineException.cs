namespace Benzene.Schema.OpenApi.Compatibility;

/// <summary>
/// The compatibility baseline is a legacy-shape contract document (spec-0.2.0 and earlier: <c>openapi</c>
/// and <c>info</c>, no <c>schemaDialect</c>). Its schemas were derived by the old rules
/// (<c>additionalProperties: false</c>, a <c>Void</c> response component, reserved entries), so a diff
/// against a current document would report false breaking changes; re-baseline instead.
/// </summary>
public class LegacyBaselineException : InvalidOperationException
{
    /// <summary>Initializes the exception.</summary>
    public LegacyBaselineException()
        : base("The compatibility baseline is a legacy (spec-0.2.0) contract document - it has no 'schemaDialect'. " +
               "Its schemas were derived by the old rules, so comparing it would report false breaking changes. " +
               "Re-baseline: regenerate the baseline spec.json from the last released version of the service with the " +
               "current Benzene, commit it, and compare against that.")
    {
    }
}
