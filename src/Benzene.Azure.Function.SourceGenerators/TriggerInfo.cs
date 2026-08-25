using System;
using Microsoft.CodeAnalysis;

namespace Benzene.Azure.Function.SourceGenerators
{
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
    /// cross-transport BENZ0001 duplicate-name check) are reported.
    /// </summary>
    internal sealed class TriggerInfo : IEquatable<TriggerInfo>
    {
        public TriggerInfo(
            string className,
            string functionNameLiteral,
            string parameterList,
            string returnType,
            string dispatchExpression,
            Location location)
            : this(className, functionNameLiteral, parameterList, returnType, dispatchExpression, location, pendingDiagnostic: null)
        {
        }

        private TriggerInfo(
            string className,
            string functionNameLiteral,
            string parameterList,
            string returnType,
            string dispatchExpression,
            Location location,
            Diagnostic? pendingDiagnostic)
        {
            ClassName = className;
            FunctionNameLiteral = functionNameLiteral;
            ParameterList = parameterList;
            ReturnType = returnType;
            DispatchExpression = dispatchExpression;
            Location = location;
            PendingDiagnostic = pendingDiagnostic;
        }

        /// <summary>
        /// Not a trigger to emit - a diagnostic a transport reader wants reported instead. Execute
        /// reports it and moves on; none of the other properties are meaningful for this instance.
        /// </summary>
        public static TriggerInfo ForDiagnostic(Diagnostic diagnostic) =>
            new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, diagnostic.Location, diagnostic);

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
        /// </summary>
        public Location Location { get; }

        /// <summary>Set when this instance represents a diagnostic to report rather than a trigger to emit. See <see cref="ForDiagnostic"/>.</summary>
        public Diagnostic? PendingDiagnostic { get; }

        public bool Equals(TriggerInfo? other) =>
            other is not null
            && ClassName == other.ClassName
            && FunctionNameLiteral == other.FunctionNameLiteral
            && ParameterList == other.ParameterList
            && ReturnType == other.ReturnType
            && DispatchExpression == other.DispatchExpression
            && Equals(PendingDiagnostic, other.PendingDiagnostic);

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
                hash = hash * 31 + (PendingDiagnostic?.GetHashCode() ?? 0);
                return hash;
            }
        }
    }
}
