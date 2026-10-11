using System.Reflection;
using System.Text;
using Benzene.Abstractions.Schema;

namespace Benzene.Schema.OpenApi.Derivation;

/// <summary>
/// The component naming rule of contract-document.md §4.1: a type's declared
/// <see cref="SchemaNameAttribute"/> name if it has one, else its simple name (no namespace), with a
/// generic instance's type-argument names before the definition's (<c>Page&lt;Order&gt;</c> →
/// <c>OrderPage</c>); every character outside <c>[A-Za-z0-9._-]</c> dropped. Deterministic: it reads
/// only the type, never registration order.
/// </summary>
public static class ComponentNames
{
    /// <summary>The component name of <paramref name="type"/>.</summary>
    /// <param name="type">The payload type.</param>
    /// <returns>A valid component key.</returns>
    public static string For(Type type)
    {
        var declared = type.GetCustomAttribute<SchemaNameAttribute>(inherit: false);
        if (declared != null && !string.IsNullOrWhiteSpace(declared.Name))
        {
            return Sanitize(declared.Name);
        }

        return Sanitize(DefaultName(type));
    }

    /// <summary>
    /// Drops every character a component key may not carry (rule 3: <c>^[A-Za-z0-9._-]+$</c>).
    /// </summary>
    /// <param name="name">The raw name.</param>
    /// <returns>The valid key.</returns>
    public static string Sanitize(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (IsValid(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    /// <summary>Whether <paramref name="name"/> is a valid component key.</summary>
    /// <param name="name">The key.</param>
    /// <returns>True when it is non-empty and every character is in <c>[A-Za-z0-9._-]</c>.</returns>
    public static bool IsValid(string? name) => !string.IsNullOrEmpty(name) && name.All(IsValid);

    private static bool IsValid(char ch)
        => ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-';

    private static string DefaultName(Type type)
    {
        if (type.IsArray)
        {
            return For(type.GetElementType()!) + "Array";
        }

        if (!type.IsConstructedGenericType)
        {
            return StripArity(type.Name);
        }

        var arguments = string.Concat(type.GetGenericArguments().Select(For));
        return arguments + StripArity(type.GetGenericTypeDefinition().Name);
    }

    private static string StripArity(string name)
    {
        var tick = name.IndexOf('`');
        return tick < 0 ? name : name.Substring(0, tick);
    }
}
