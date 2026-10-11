namespace Benzene.Abstractions.Serialization;

/// <summary>
/// Names a type's members the way the service's JSON serializer writes them - its naming policy and
/// any rename attributes applied - so a producer that only knows a .NET member name (a validation
/// library's property path) can address the same value in the request's JSON form.
/// </summary>
/// <remarks>
/// This is what lets every validation integration emit <c>errors[].field</c> as a JSON Pointer into
/// the handler's request type in its JSON form (wire-contracts.md §1.3): <c>Items[0].Sku</c> becomes
/// <c>/items/0/sku</c> under the default camelCase policy, and <c>/line_items/0/sku</c> under a
/// snake_case one. <c>Benzene.Core.MessageHandlers</c>' <c>JsonSerializer</c> implements this from its
/// own <c>JsonSerializerOptions</c>, and <c>AddBenzene</c> registers it, so the pointer follows the
/// options the service actually serializes with; <see cref="JsonPropertyNameResolver.Default"/> is the
/// fallback when nothing is registered.
/// </remarks>
public interface IJsonPropertyNameResolver
{
    /// <summary>
    /// Returns the JSON property name the serializer writes for the member named
    /// <paramref name="memberName"/> on <paramref name="type"/>.
    /// </summary>
    /// <param name="type">The type that declares (or inherits) the member.</param>
    /// <param name="memberName">The .NET member name, exactly as declared.</param>
    /// <returns>
    /// The JSON property name, or <c>null</c> when <paramref name="type"/> has no serialized member of
    /// that name (it does not exist, or the serializer ignores it) - a caller then has no location to
    /// point at.
    /// </returns>
    string? GetJsonPropertyName(Type type, string memberName);
}
