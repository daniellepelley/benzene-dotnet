namespace Benzene.Abstractions.Schema;

/// <summary>
/// Declares the component name a payload type's schema takes in the service's contract document
/// (contract-document.md §4.1 rule 5) - the remedy for a component-name collision, where two
/// different types would otherwise both default to the same simple name.
/// </summary>
/// <remarks>
/// <para>
/// Without this attribute a type's component name is its simple name (no namespace), with a generic
/// instance's type-argument names before the definition's (<c>Page&lt;Order&gt;</c> →
/// <c>OrderPage</c>). Names are not part of the contract hash - two ports that name differently still
/// hash identically - but they are what generated clients call their types, so two different types
/// may never share one: derivation fails, before any message is handled, naming both types.
/// </para>
/// <para>
/// The name must match <c>^[A-Za-z0-9._-]+$</c>; any other character is dropped.
/// </para>
/// </remarks>
/// <example><code>[SchemaName("PaymentItem")] public record Item(string PaymentId);</code></example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum | AttributeTargets.Interface,
    Inherited = false, AllowMultiple = false)]
public sealed class SchemaNameAttribute : Attribute
{
    /// <summary>Declares <paramref name="name"/> as the type's component name.</summary>
    /// <param name="name">The component name.</param>
    public SchemaNameAttribute(string name)
    {
        Name = name;
    }

    /// <summary>The declared component name.</summary>
    public string Name { get; }
}
