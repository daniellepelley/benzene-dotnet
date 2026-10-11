using System.Text;

namespace Benzene.Grpc;

/// <summary>
/// Converts between a Benzene error's <c>field</c> - an RFC 6901 JSON Pointer into the request's JSON
/// form (wire-contracts.md §1.3) - and the dotted path <c>google.rpc.BadRequest.FieldViolation.field</c>
/// carries on gRPC (wire-contracts.md §4.2).
/// </summary>
/// <remarks>
/// The pointer's segments are unescaped per RFC 6901 (<c>~1</c> → <c>/</c>, <c>~0</c> → <c>~</c>) and
/// joined with <c>.</c>; a segment made only of decimal digits is an array index and is written
/// <c>[n]</c>, attached to the segment before it: <c>/items/0/sku</c> ↔ <c>items[0].sku</c>. An error with
/// no field has an empty <c>FieldViolation.field</c>.
/// </remarks>
public static class GrpcFieldPath
{
    /// <summary>
    /// Converts a JSON Pointer to the dotted <c>FieldViolation.field</c> form.
    /// </summary>
    /// <param name="pointer">The error's field pointer, e.g. <c>/items/0/sku</c>.</param>
    /// <returns>
    /// The dotted path, e.g. <c>items[0].sku</c>; empty for a <c>null</c>/empty pointer (no field). A
    /// value that is not a pointer (no leading <c>/</c> - e.g. an application-authored field name) is
    /// returned unchanged.
    /// </returns>
    public static string FromPointer(string? pointer)
    {
        if (string.IsNullOrEmpty(pointer))
        {
            return string.Empty;
        }

        if (pointer[0] != '/')
        {
            return pointer;
        }

        var path = new StringBuilder();
        foreach (var token in pointer.Substring(1).Split('/'))
        {
            var segment = token.Replace("~1", "/").Replace("~0", "~");
            if (IsIndex(segment))
            {
                path.Append('[').Append(segment).Append(']');
                continue;
            }

            if (path.Length > 0)
            {
                path.Append('.');
            }

            path.Append(segment);
        }

        return path.ToString();
    }

    /// <summary>
    /// Converts a dotted <c>FieldViolation.field</c> back to a JSON Pointer - the reverse of
    /// <see cref="FromPointer"/>, so a Benzene gRPC client reports the same <c>field</c> an HTTP or
    /// envelope client would.
    /// </summary>
    /// <param name="dottedPath">The violation's field, e.g. <c>items[0].sku</c>.</param>
    /// <returns>The pointer, e.g. <c>/items/0/sku</c>; <c>null</c> for an empty field (no field).</returns>
    public static string? ToPointer(string? dottedPath)
    {
        if (string.IsNullOrEmpty(dottedPath))
        {
            return null;
        }

        var pointer = new StringBuilder();
        var name = new StringBuilder();
        var i = 0;
        while (i < dottedPath.Length)
        {
            var c = dottedPath[i];
            if (c == '.')
            {
                AppendName(pointer, name);
                i++;
                continue;
            }

            if (c == '[')
            {
                var close = dottedPath.IndexOf(']', i + 1);
                if (close > i)
                {
                    AppendName(pointer, name);
                    pointer.Append('/').Append(Escape(dottedPath.Substring(i + 1, close - i - 1)));
                    i = close + 1;
                    continue;
                }
            }

            name.Append(c);
            i++;
        }

        AppendName(pointer, name);
        return pointer.ToString();
    }

    private static void AppendName(StringBuilder pointer, StringBuilder name)
    {
        if (name.Length == 0)
        {
            return;
        }

        pointer.Append('/').Append(Escape(name.ToString()));
        name.Clear();
    }

    private static string Escape(string token) => token.Replace("~", "~0").Replace("/", "~1");

    private static bool IsIndex(string segment)
    {
        if (segment.Length == 0)
        {
            return false;
        }

        foreach (var c in segment)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }
}
