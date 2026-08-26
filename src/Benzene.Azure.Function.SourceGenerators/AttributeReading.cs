using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Benzene.Azure.Function.SourceGenerators
{
    /// <summary>Shared helpers for turning attribute arguments into safe, fully-qualified emitted C#.</summary>
    internal static class AttributeReading
    {
        /// <summary>A correctly-escaped, quoted C# string literal (user-authored values may contain quotes/backslashes).</summary>
        public static string Literal(string? value) => SymbolDisplay.FormatLiteral(value ?? string.Empty, quote: true);

        /// <summary>Reads a named string argument, or a default when absent.</summary>
        public static string NamedString(AttributeData attribute, string name, string fallback) =>
            NamedStringIfPresent(attribute, name) ?? fallback;

        /// <summary>
        /// Reads a named string argument, distinguishing "absent" (<see langword="null"/>) from
        /// "explicitly set" (including to <c>""</c>) - <see cref="NamedString"/> collapses that
        /// distinction into its <c>fallback</c>, which is correct for most args but wrong for the
        /// required-vs-defaulted checks in <c>Transports/</c> (e.g. BENZ0008: an explicitly-empty
        /// <c>Name</c> is an error, an absent one correctly defaults).
        /// </summary>
        public static string? NamedStringIfPresent(AttributeData attribute, string name)
        {
            foreach (var arg in attribute.NamedArguments)
            {
                if (arg.Key == name && arg.Value.Value is string s)
                {
                    return s;
                }
            }

            return null;
        }

        /// <summary>
        /// Reads Name with the WP-C #40 validation every transport needs: an explicitly-set
        /// <c>""</c>/whitespace-only Name is an error (distinct from the absent case, which correctly
        /// defaults to <paramref name="fallback"/>). Returns <see langword="null"/> (valid) with
        /// <paramref name="name"/> set to the explicit or defaulted value; returns a
        /// <see cref="DiagnosticDescriptors.EmptyFunctionName"/> <see cref="PendingDiagnosticInfo"/>
        /// when Name was explicitly blank, with <paramref name="name"/> set to that offending value
        /// (still useful to the caller for building the BENZ0001 collision-check literal, since a
        /// blank name is itself a name that could collide with another blank one - see
        /// <see cref="TriggerInfo.ForDiagnostic"/>).
        /// </summary>
        public static PendingDiagnosticInfo? ValidateName(AttributeData attribute, string fallback, out string name)
        {
            var explicitName = NamedStringIfPresent(attribute, "Name");
            if (explicitName != null && explicitName.Trim().Length == 0)
            {
                name = explicitName;
                return new PendingDiagnosticInfo(DiagnosticDescriptors.EmptyFunctionName, Literal(explicitName));
            }

            name = explicitName ?? fallback;
            return null;
        }

        /// <summary>Reads a named bool argument, or a default when absent.</summary>
        public static bool NamedBool(AttributeData attribute, string name, bool fallback)
        {
            foreach (var arg in attribute.NamedArguments)
            {
                if (arg.Key == name && arg.Value.Value is bool b)
                {
                    return b;
                }
            }

            return fallback;
        }

        /// <summary>Reads a named string[] argument as a comma-separated list of quoted literals, or a fallback list.</summary>
        public static string NamedStringArrayCsv(AttributeData attribute, string name, params string[] fallback)
        {
            foreach (var arg in attribute.NamedArguments)
            {
                if (arg.Key == name && arg.Value.Kind == TypedConstantKind.Array && !arg.Value.IsNull)
                {
                    var items = arg.Value.Values
                        .Select(v => Literal(v.Value as string))
                        .Where(v => v != "\"\"");
                    var csv = string.Join(", ", items);
                    if (csv.Length > 0)
                    {
                        return csv;
                    }
                }
            }

            return string.Join(", ", fallback.Select(Literal));
        }

        /// <summary>
        /// Reads a named enum argument as a fully-qualified member expression (e.g.
        /// <c>global::Microsoft.Azure.Functions.Worker.AuthorizationLevel.Anonymous</c>), or the given
        /// default member expression when absent.
        /// </summary>
        public static string NamedEnumMember(AttributeData attribute, string name, string fallbackExpression)
        {
            foreach (var arg in attribute.NamedArguments)
            {
                if (arg.Key == name && arg.Value.Kind == TypedConstantKind.Enum && arg.Value.Type is INamedTypeSymbol enumType)
                {
                    var member = enumType.GetMembers()
                        .OfType<IFieldSymbol>()
                        .FirstOrDefault(f => f.HasConstantValue && Equals(f.ConstantValue, arg.Value.Value));

                    if (member != null)
                    {
                        return "global::" + enumType.ToDisplayString() + "." + member.Name;
                    }

                    // Unnamed/combined value: emit a cast so it still compiles.
                    return "(global::" + enumType.ToDisplayString() + ")" + arg.Value.Value;
                }
            }

            return fallbackExpression;
        }

        /// <summary>Reads a named <c>typeof(...)</c> argument as a fully-qualified type name (with <c>global::</c>), or null.</summary>
        public static string? NamedType(AttributeData attribute, string name)
        {
            foreach (var arg in attribute.NamedArguments)
            {
                if (arg.Key == name && arg.Value.Value is ITypeSymbol type)
                {
                    return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                }
            }

            return null;
        }

        /// <summary>Formats an optional <c>, Name = "value"</c> string binding argument, or empty when the value is empty.</summary>
        public static string OptionalStringArg(string name, string value) =>
            value.Length > 0 ? $", {name} = {Literal(value)}" : string.Empty;

        /// <summary>Formats an optional <c>, Name = true</c> boolean binding argument, or empty when false.</summary>
        public static string OptionalBoolArg(string name, bool value) =>
            value ? $", {name} = true" : string.Empty;

        /// <summary>The single required trigger name (first constructor argument), or null if absent/empty.</summary>
        public static string? TriggerName(AttributeData attribute)
        {
            if (attribute.ConstructorArguments.Length > 0 && attribute.ConstructorArguments[0].Value is string s && s.Length > 0)
            {
                return s;
            }

            return null;
        }

        /// <summary>
        /// The source location of an attribute application, for diagnostics (e.g. BENZ0001, BENZ0002).
        /// Falls back to <see cref="Location.None"/> when the syntax isn't available (e.g. the
        /// attribute came from metadata rather than source, which shouldn't happen here but shouldn't
        /// crash the generator either).
        ///
        /// Deliberately returns an EXTERNAL location (<c>Location.Create(string, TextSpan,
        /// LinePositionSpan)</c> - file path + span, no <see cref="Location.SourceTree"/>), not the
        /// tree-bound <c>SyntaxNode.GetLocation()</c> result directly (WP-C, #38 - the build-crash
        /// finding). <see cref="TriggerInfo.Location"/> is deliberately excluded from
        /// <see cref="TriggerInfo"/>'s equality (for incremental cache hits), so the incremental engine
        /// can hand a cached <see cref="TriggerInfo"/> - Location included - to a LATER round whose
        /// <see cref="Compilation"/> no longer contains that Location's tree (confirmed live: two
        /// independently-constructed <c>CSharpCompilation</c>s sharing one driver, and a genuine
        /// single-tree incremental edit via <c>SyntaxTree.WithChangedText</c> +
        /// <c>Compilation.ReplaceSyntaxTree</c>). Roslyn's own <c>GeneratorDriver.RunGeneratorsCore</c>
        /// then throws <see cref="ArgumentException"/> while suppression-checking every reported
        /// diagnostic against the compilation - AFTER this generator's <c>Execute</c> has already
        /// returned, so nothing on our side of the call (a try/catch around <c>ReportDiagnostic</c>
        /// included - confirmed NOT to catch this; the throw happens later, inside Roslyn's own driver
        /// code, off our call stack) can guard against it. A tree-bound Location is therefore never
        /// safe to persist across an incremental boundary; an external one - which carries no tree
        /// reference at all, so there is nothing for the compilation-membership check to reject - is
        /// unconditionally safe, and still renders the same <c>file(line,col): error BENZ0001: …</c>
        /// build-output location a tree-bound one would.
        /// </summary>
        public static Location AttributeLocation(AttributeData attribute)
        {
            var syntax = attribute.ApplicationSyntaxReference?.GetSyntax();
            if (syntax == null)
            {
                return Location.None;
            }

            var treeLocation = syntax.GetLocation();
            var lineSpan = treeLocation.GetLineSpan();
            return Location.Create(lineSpan.Path, treeLocation.SourceSpan, lineSpan.Span);
        }

        /// <summary>Turns an arbitrary trigger name into a valid, stable C# identifier for the generated class.</summary>
        public static string ToIdentifier(string name)
        {
            var sb = new StringBuilder();
            var capitalizeNext = true;
            foreach (var ch in name)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    sb.Append(capitalizeNext ? char.ToUpperInvariant(ch) : ch);
                    capitalizeNext = false;
                }
                else
                {
                    // Any separator (-, :, /, space, …) becomes a word boundary.
                    capitalizeNext = true;
                }
            }

            if (sb.Length == 0 || char.IsDigit(sb[0]))
            {
                sb.Insert(0, '_');
            }

            return sb.ToString();
        }
    }
}
