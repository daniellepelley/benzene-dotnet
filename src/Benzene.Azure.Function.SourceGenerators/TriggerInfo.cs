using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Benzene.Azure.Function.SourceGenerators
{
    /// <summary>
    /// A diagnostic a transport reader wants reported, deferred so it can be turned into a real
    /// <see cref="Diagnostic"/> against a <em>freshly-resolved</em> <see cref="Location"/> at report
    /// time rather than baked in at read time - see <see cref="TriggerInfo.Location"/> for why. Value
    /// equality (descriptor id + message args) drives <see cref="TriggerInfo"/>'s own cache-hit
    /// equality; deliberately never carries a <see cref="Location"/> itself.
    /// </summary>
    internal readonly struct PendingDiagnosticInfo : IEquatable<PendingDiagnosticInfo>
    {
        public PendingDiagnosticInfo(DiagnosticDescriptor descriptor, params string[] messageArgs)
        {
            Descriptor = descriptor;
            MessageArgs = messageArgs.ToImmutableArray();
        }

        public DiagnosticDescriptor Descriptor { get; }

        public ImmutableArray<string> MessageArgs { get; }

        /// <summary>Builds the real <see cref="Diagnostic"/> against a location resolved at report time.</summary>
        public Diagnostic ToDiagnostic(Location location) =>
            Diagnostic.Create(Descriptor, location, MessageArgs.Cast<object>().ToArray());

        public bool Equals(PendingDiagnosticInfo other) =>
            Descriptor.Id == other.Descriptor.Id && MessageArgs.SequenceEqual(other.MessageArgs);

        public override bool Equals(object? obj) => obj is PendingDiagnosticInfo other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = Descriptor.Id.GetHashCode();
                foreach (var arg in MessageArgs)
                {
                    hash = hash * 31 + arg.GetHashCode();
                }

                return hash;
            }
        }
    }

    /// <summary>
    /// The fully-resolved shape of one trigger function to emit, reduced to strings so the incremental
    /// pipeline can cache on value equality (an unchanged set of declarations re-emits nothing). Each
    /// transport builds its own <see cref="ParameterList"/> and <see cref="DispatchExpression"/>, so
    /// two-parameter (Blob) and generic (Cosmos) shapes fit the same model.
    ///
    /// Doubles as the vehicle for a transport reader's diagnostics (see <see cref="ForDiagnostic"/>):
    /// a reader that can't produce a valid trigger (e.g. a CosmosDb trigger missing DocumentType, for
    /// BENZ0002) reports that instead of silently dropping the declaration, without needing a second,
    /// parallel incremental provider just to carry diagnostics through to
    /// <see cref="AzureFunctionTriggerGenerator.Execute"/> - where all diagnostics (including the
    /// cross-transport BENZ0001 duplicate-name check) are reported. A reader that CAN produce a valid
    /// trigger but wants to additionally warn (e.g. BENZ0009 - both queue and topic set) attaches
    /// <see cref="AdvisoryDiagnostics"/> instead - the trigger is still emitted, the warning fires
    /// alongside it.
    /// </summary>
    internal sealed class TriggerInfo : IEquatable<TriggerInfo>
    {
        public TriggerInfo(
            string className,
            string functionNameLiteral,
            string parameterList,
            string returnType,
            string dispatchExpression,
            Location location,
            ImmutableArray<PendingDiagnosticInfo> advisoryDiagnostics = default)
        {
            ClassName = className;
            FunctionNameLiteral = functionNameLiteral;
            ParameterList = parameterList;
            ReturnType = returnType;
            DispatchExpression = dispatchExpression;
            Location = location;
            AdvisoryDiagnostics = advisoryDiagnostics.IsDefault ? ImmutableArray<PendingDiagnosticInfo>.Empty : advisoryDiagnostics;
            BlockingDiagnostic = null;
        }

        private TriggerInfo(string functionNameLiteral, Location location, PendingDiagnosticInfo blockingDiagnostic)
        {
            ClassName = string.Empty;
            FunctionNameLiteral = functionNameLiteral;
            ParameterList = string.Empty;
            ReturnType = string.Empty;
            DispatchExpression = string.Empty;
            Location = location;
            AdvisoryDiagnostics = ImmutableArray<PendingDiagnosticInfo>.Empty;
            BlockingDiagnostic = blockingDiagnostic;
        }

        /// <summary>
        /// Not a trigger to emit - a diagnostic a transport reader wants reported instead. Execute
        /// reports it and moves on; none of the other properties are meaningful for this instance.
        /// <paramref name="functionNameLiteral"/> is still the attempted/intended name (even though
        /// nothing will be emitted for it) so the cross-transport BENZ0001 collision check - which
        /// runs over the FULL declared set, including entries like this one - can still see it (#32:
        /// a collision where one side is broken must not be masked by that side's own diagnostic).
        /// </summary>
        public static TriggerInfo ForDiagnostic(string functionNameLiteral, Location location, PendingDiagnosticInfo diagnostic) =>
            new(functionNameLiteral, location, diagnostic);

        /// <summary>The generated class name (unique within the generated namespace).</summary>
        public string ClassName { get; }

        /// <summary>The quoted C# literal for the Azure Function name (unique across the app).</summary>
        public string FunctionNameLiteral { get; }

        /// <summary>The full parameter list of the generated <c>Run</c> method, incl. the binding attribute(s).</summary>
        public string ParameterList { get; }

        /// <summary>The <c>Run</c> method's return type, fully qualified.</summary>
        public string ReturnType { get; }

        /// <summary>The body expression forwarding into the app, e.g. <c>global::….HandleHttpRequest(_app, req)</c>.</summary>
        public string DispatchExpression { get; }

        /// <summary>
        /// Where the trigger attribute was declared, for diagnostics only (e.g. pointing BENZ0001 at
        /// the offending declaration). Deliberately excluded from <see cref="Equals(TriggerInfo)"/>:
        /// it plays no part in the emitted source, and unlike the string fields above, Roslyn's
        /// <see cref="Microsoft.CodeAnalysis.Location"/> isn't guaranteed to compare stably run to run
        /// the way a plain value would - including it would only cost cache hits for no benefit.
        ///
        /// This IS the root cause of the #38 build crash: because it's excluded from equality, the
        /// incremental engine is free to keep reusing an OLD cached <see cref="TriggerInfo"/> instance
        /// (old <see cref="Location"/> and all) whenever a freshly-recomputed instance compares equal
        /// to it - which happens whenever every OTHER field is unchanged, even if the freshly-computed
        /// instance's own Location would have been perfectly valid. If that old Location's
        /// <see cref="Microsoft.CodeAnalysis.Location.SourceTree"/> is no longer part of the CURRENT
        /// <see cref="Compilation"/> (e.g. the file was reparsed via <c>SyntaxTree.WithChangedText</c>
        /// even though the trigger declaration's own text didn't change), feeding it straight into
        /// <see cref="Diagnostic.Create(DiagnosticDescriptor, Location, object[])"/> throws
        /// <see cref="ArgumentException"/> during suppression-checking and crashes the whole build.
        /// This is a well-known Roslyn incremental-generator hazard for any value-equatable model type
        /// that (deliberately, for cache hits) excludes its Location from equality. Confirmed live: the
        /// throw happens inside <c>GeneratorDriver.RunGeneratorsCore</c>'s own suppression-filtering
        /// pass, AFTER <see cref="AzureFunctionTriggerGenerator"/>'s <c>Execute</c> has already
        /// returned - so no amount of try/catch around <c>SourceProductionContext.ReportDiagnostic</c>
        /// on our side of that call can ever catch it (verified: it doesn't).
        ///
        /// The actual fix is therefore upstream, at the source: every <see cref="TriggerInfo"/> this
        /// package builds gets its Location from
        /// <see cref="AttributeReading.AttributeLocation(AttributeData)"/>, which returns an EXTERNAL
        /// location (file path + span, no <see cref="Microsoft.CodeAnalysis.Location.SourceTree"/>) -
        /// see that method's doc comment. An external Location has no tree reference to go stale in the
        /// first place, so the compilation-membership check this whole hazard turns on can never reject
        /// it, regardless of how long the incremental engine ends up caching this instance for.
        /// </summary>
        public Location Location { get; }

        /// <summary>
        /// Set when this instance represents a diagnostic to report rather than a trigger to emit
        /// (e.g. BENZ0002/BENZ0003.../BENZ0008). See <see cref="ForDiagnostic"/>.
        /// </summary>
        public PendingDiagnosticInfo? BlockingDiagnostic { get; }

        /// <summary>
        /// Diagnostics to report ALONGSIDE a normal emission - the trigger is still generated, these
        /// just warn (e.g. BENZ0009). Empty for the overwhelming majority of triggers.
        /// </summary>
        public ImmutableArray<PendingDiagnosticInfo> AdvisoryDiagnostics { get; }

        public bool Equals(TriggerInfo? other) =>
            other is not null
            && ClassName == other.ClassName
            && FunctionNameLiteral == other.FunctionNameLiteral
            && ParameterList == other.ParameterList
            && ReturnType == other.ReturnType
            && DispatchExpression == other.DispatchExpression
            && Nullable.Equals(BlockingDiagnostic, other.BlockingDiagnostic)
            && AdvisoryDiagnostics.SequenceEqual(other.AdvisoryDiagnostics);

        public override bool Equals(object? obj) => Equals(obj as TriggerInfo);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = 17;
                hash = hash * 31 + ClassName.GetHashCode();
                hash = hash * 31 + FunctionNameLiteral.GetHashCode();
                hash = hash * 31 + ParameterList.GetHashCode();
                hash = hash * 31 + ReturnType.GetHashCode();
                hash = hash * 31 + DispatchExpression.GetHashCode();
                hash = hash * 31 + (BlockingDiagnostic?.GetHashCode() ?? 0);
                foreach (var advisory in AdvisoryDiagnostics)
                {
                    hash = hash * 31 + advisory.GetHashCode();
                }

                return hash;
            }
        }
    }
}
