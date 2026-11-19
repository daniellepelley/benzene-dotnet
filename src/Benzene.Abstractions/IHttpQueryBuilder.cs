namespace Benzene.Abstractions;

/// <summary>
/// Optionally implemented by an <see cref="IHttpBuilder{T}"/> that also describes a query string.
/// A separate interface, not a member of <see cref="IHttpBuilder{T}"/>, so existing builders keep
/// compiling: the transport test helpers read the query through <c>source as IHttpQueryBuilder</c> and
/// send none when the builder doesn't implement it.
/// </summary>
public interface IHttpQueryBuilder
{
    /// <summary>
    /// Gets the query-string parameters, in order. A key that appears more than once is a repeated
    /// query key (<c>?id=a&amp;id=b</c>).
    /// </summary>
    IReadOnlyList<KeyValuePair<string, string>> Query { get; }
}
