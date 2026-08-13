using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Benzene.CodeGen.Core;

/// <summary>
/// A minimal, dependency-free implementation of RFC 8785 (the JSON Canonicalization Scheme, JCS):
/// object members sorted by their UTF-16 code unit sequence, numbers formatted per the ECMAScript
/// <c>Number::toString</c> algorithm JCS mandates, no insignificant whitespace, and the I-JSON
/// (RFC 7493) string-escaping rules. This is what <see cref="ContractHash"/> canonicalizes over
/// before hashing - see <c>docs/specification/contract-document.md</c> §6.2/§6.3 for why JCS (rather
/// than a documented member order, as <c>Benzene.Mesh.Contracts.MeshHashing</c>'s sibling
/// <c>descriptorHash</c> uses) is required here: <c>contractHash</c> is compared across independently
/// implemented ports, so canonicalization must be mechanical rather than a hand-implementable
/// convention four implementations could each get subtly wrong.
/// </summary>
/// <remarks>
/// There is no off-the-shelf RFC 8785 library for .NET (unlike npm's <c>canonicalize</c> or PyPI's
/// <c>rfc8785</c>), so this hand-rolls the scheme. The trickiest part - number formatting - works by
/// taking .NET's own shortest-round-trip decimal digit sequence (the same well-defined digit
/// sequence ECMAScript's <c>Number::toString</c> is built on; .NET Core 3.0+'s default
/// <see cref="double.ToString(IFormatProvider)"/> is shortest-round-trip, just like JS's) and
/// re-deriving ECMAScript's decimal/exponential placement rules from it directly, so the specific
/// notation .NET happens to pick (plain decimal vs. its own scientific notation, threshold and all)
/// is irrelevant to the output.
/// </remarks>
public static class JsonCanonicalizer
{
    /// <summary>Renders <paramref name="node"/> as its RFC 8785 canonical JSON string.</summary>
    public static string Canonicalize(JsonNode? node)
    {
        var builder = new StringBuilder();
        WriteNode(node, builder);
        return builder.ToString();
    }

    private static void WriteNode(JsonNode? node, StringBuilder sb)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case JsonObject obj:
                WriteObject(obj, sb);
                break;
            case JsonArray array:
                WriteArray(array, sb);
                break;
            case JsonValue value:
                WriteValue(value, sb);
                break;
            default:
                throw new NotSupportedException($"Unsupported JsonNode type for JCS canonicalization: {node.GetType()}");
        }
    }

    private static void WriteObject(JsonObject obj, StringBuilder sb)
    {
        sb.Append('{');

        var first = true;
        foreach (var member in obj.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
        {
            if (!first)
            {
                sb.Append(',');
            }

            first = false;

            WriteString(member.Key, sb);
            sb.Append(':');
            WriteNode(member.Value, sb);
        }

        sb.Append('}');
    }

    private static void WriteArray(JsonArray array, StringBuilder sb)
    {
        sb.Append('[');

        var first = true;
        foreach (var item in array)
        {
            if (!first)
            {
                sb.Append(',');
            }

            first = false;
            WriteNode(item, sb);
        }

        sb.Append(']');
    }

    private static void WriteValue(JsonValue value, StringBuilder sb)
    {
        // Every JsonValue this type encounters is parsed from JSON text (via JsonNode.Parse), so it
        // is always backed by a JsonElement - this is the primary, always-taken path.
        if (value.TryGetValue<JsonElement>(out var element))
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    WriteString(element.GetString() ?? string.Empty, sb);
                    return;
                case JsonValueKind.True:
                    sb.Append("true");
                    return;
                case JsonValueKind.False:
                    sb.Append("false");
                    return;
                case JsonValueKind.Null:
                    sb.Append("null");
                    return;
                case JsonValueKind.Number:
                    sb.Append(FormatNumber(element.GetDouble()));
                    return;
                default:
                    throw new NotSupportedException($"Unsupported JSON value kind for JCS canonicalization: {element.ValueKind}");
            }
        }

        // Defensive fallback for a JsonValue built programmatically from a CLR primitive rather than
        // parsed from JSON text - not exercised by ContractHash today (which always parses from
        // serialized JSON) but cheap to support correctly rather than throw.
        var raw = value.GetValue<object>();
        switch (raw)
        {
            case string s:
                WriteString(s, sb);
                return;
            case bool b:
                sb.Append(b ? "true" : "false");
                return;
            case null:
                sb.Append("null");
                return;
            case double or float or int or long or short or byte or decimal:
                sb.Append(FormatNumber(Convert.ToDouble(raw, CultureInfo.InvariantCulture)));
                return;
            default:
                throw new NotSupportedException($"Unsupported JSON leaf value type for JCS canonicalization: {raw.GetType()}");
        }
    }

    /// <summary>
    /// Writes <paramref name="s"/> as a JSON string literal per RFC 8785 §3.2.2.2 / RFC 7493 (I-JSON):
    /// <c>"</c> and <c>\</c> are backslash-escaped, the C0 control characters below U+0020 use the
    /// shorthand escapes where one exists (<c>\b \f \n \r \t</c>) or <c>\u00XX</c> (lowercase hex)
    /// otherwise, and every other character - including <c>/</c> and all non-ASCII text - is written
    /// as-is (JCS does not require escaping outside the C0 control range).
    /// </summary>
    private static void WriteString(string s, StringBuilder sb)
    {
        sb.Append('"');

        foreach (var ch in s)
        {
            switch (ch)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (ch < 0x20)
                    {
                        sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(ch);
                    }

                    break;
            }
        }

        sb.Append('"');
    }

    /// <summary>
    /// Formats <paramref name="value"/> per the ECMAScript <c>Number::toString</c> algorithm JCS
    /// mandates (RFC 8785 §3.2.2.3): the shortest decimal digit sequence that round-trips back to
    /// the same IEEE-754 double, placed as a plain integer, plain decimal, or exponential form
    /// depending on its magnitude, with unsigned zero and a lowercase <c>e</c> exponent marker.
    /// </summary>
    private static string FormatNumber(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new NotSupportedException("JCS numbers must be finite - NaN/Infinity are not valid JSON.");
        }

        if (value == 0)
        {
            // RFC 8785 §3.2.2.3: zero is always rendered unsigned, regardless of IEEE 754's sign bit
            // (so -0.0 also renders as "0").
            return "0";
        }

        var negative = value < 0;
        var (digits, exponent) = ShortestRoundTripDigits(Math.Abs(value));
        var formatted = FormatDigits(digits, exponent);

        return negative ? "-" + formatted : formatted;
    }

    /// <summary>
    /// Extracts the shortest round-trip decimal digit sequence of <paramref name="absoluteValue"/>
    /// (positive, non-zero, finite) and the decimal exponent <c>n</c> such that the value equals
    /// <c>0.&lt;digits&gt; * 10^n</c> - the same (digits, n) pair the ECMAScript spec's own
    /// <c>Number::toString</c> algorithm is defined over. .NET Core 3.0+'s invariant-culture
    /// <see cref="double.ToString(IFormatProvider)"/> already produces the shortest round-trippable
    /// decimal representation (in whatever notation .NET itself picks); this just re-parses that
    /// string into the digit/exponent form so <see cref="FormatDigits"/> can re-apply ECMAScript's
    /// own placement rules independently of .NET's notation choice.
    /// </summary>
    private static (string Digits, int Exponent) ShortestRoundTripDigits(double absoluteValue)
    {
        var text = absoluteValue.ToString(CultureInfo.InvariantCulture);

        var exponentIndex = text.IndexOf('E');
        string mantissa;
        var exponent = 0;
        if (exponentIndex >= 0)
        {
            mantissa = text.Substring(0, exponentIndex);
            exponent = int.Parse(text.Substring(exponentIndex + 1), CultureInfo.InvariantCulture);
        }
        else
        {
            mantissa = text;
        }

        var dotIndex = mantissa.IndexOf('.');
        string integerPart;
        string fractionPart;
        if (dotIndex >= 0)
        {
            integerPart = mantissa.Substring(0, dotIndex);
            fractionPart = mantissa.Substring(dotIndex + 1);
        }
        else
        {
            integerPart = mantissa;
            fractionPart = string.Empty;
        }

        var combined = integerPart + fractionPart;
        var pointPosition = integerPart.Length;

        var firstSignificant = 0;
        while (firstSignificant < combined.Length - 1 && combined[firstSignificant] == '0')
        {
            firstSignificant++;
        }

        var digitsEnd = combined.Length;
        while (digitsEnd > firstSignificant + 1 && combined[digitsEnd - 1] == '0')
        {
            digitsEnd--;
        }

        var digits = combined.Substring(firstSignificant, digitsEnd - firstSignificant);
        var n = pointPosition - firstSignificant + exponent;

        return (digits, n);
    }

    /// <summary>
    /// Applies the ECMAScript <c>Number::toString</c> placement rules to a (digits, n) pair, per
    /// ECMA-262's own four-branch definition: an integer with trailing zeroes, a decimal fraction
    /// with the point inside the digit string, a decimal fraction with leading zeroes after the
    /// point, or exponential notation - chosen purely from the digit count <c>k</c> and <c>n</c>,
    /// never from whatever notation the source string used.
    /// </summary>
    private static string FormatDigits(string digits, int n)
    {
        var k = digits.Length;

        if (k <= n && n <= 21)
        {
            return digits + new string('0', n - k);
        }

        if (0 < n && n <= 21)
        {
            return digits.Substring(0, n) + "." + digits.Substring(n);
        }

        if (-6 < n && n <= 0)
        {
            return "0." + new string('0', -n) + digits;
        }

        var exponent = n - 1;
        var exponentSign = exponent >= 0 ? "+" : "-";
        var exponentDigits = Math.Abs(exponent).ToString(CultureInfo.InvariantCulture);

        return k == 1
            ? $"{digits}e{exponentSign}{exponentDigits}"
            : $"{digits[0]}.{digits.Substring(1)}e{exponentSign}{exponentDigits}";
    }
}
