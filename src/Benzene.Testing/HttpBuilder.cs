using Benzene.Abstractions;

namespace Benzene.Testing;

public class HttpBuilder<T> : IHttpBuilder<T>, IHttpQueryBuilder
{
    public IDictionary<string, string> Headers { get; private set; }
    public string Method { get; }
    public string Path { get; }
    public T? Message { get; }

    /// <inheritdoc />
    public IReadOnlyList<KeyValuePair<string, string>> Query => _query;

    private readonly List<KeyValuePair<string, string>> _query = new();

    internal HttpBuilder(string method, string path, T? message)
    {
        Message = message;
        Method = method;
        Path = path;
        Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public static HttpBuilder<T> Create(string method, string path, T? message = default)
    {
        return new HttpBuilder<T>(method, path, message);
    }


    public HttpBuilder<T> WithHeaders(IDictionary<string, string> headers)
    {
        // Merge into our own dictionary (additive, last-wins) rather than aliasing the caller's
        // collection - matching the sibling MessageBuilderExtensions.WithHeaders. Aliasing dropped any
        // earlier WithHeader values and coupled later mutations to the caller's (possibly read-only) dict.
        foreach (var header in headers)
        {
            Headers[header.Key] = header.Value;
        }

        return this;
    }

    /// <summary>
    /// Adds query-string values for <paramref name="key"/>. Each value is one occurrence of the key, so
    /// several values, or several calls, make a repeated query key (<c>?id=a&amp;id=b</c>).
    /// </summary>
    /// <param name="key">The query-string key.</param>
    /// <param name="values">The values, in order.</param>
    /// <returns>This builder, for chaining.</returns>
    public HttpBuilder<T> WithQuery(string key, params string[] values)
    {
        foreach (var value in values)
        {
            _query.Add(new KeyValuePair<string, string>(key, value));
        }

        return this;
    }

    public HttpBuilder<T> WithHeader(string key, string value)
    {
        // Overwrite (last-wins), like a real header map; Dictionary.Add threw on a duplicate key.
        Headers[key] = value;
        return this;
    }
}

public static class HttpBuilder 
{
    public static HttpBuilder<object> Create(string method, string path)
    {
        return new HttpBuilder<object>(method, path, null);
    }
    
    public static HttpBuilder<T> Create<T>(string method, string path, T? message = default)
    {
        return new HttpBuilder<T>(method, path, message);
    }
}
