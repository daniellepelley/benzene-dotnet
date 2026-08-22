using System.Text;

namespace Benzene.Core.DI;

/// <summary>
/// Provides dependency injection registration validation and diagnostic capabilities.
/// </summary>
public class RegistrationCheck : IRegistrationCheck
{
    private readonly IDictionary<string, IDictionary<string, string[]>> _registrations;

    private RegistrationCheck(IDictionary<string, IDictionary<string, Type[]>> registrations)
    {
        _registrations = registrations.ToDictionary(x => x.Key,
            x => Convert(x.Value));
    }

    /// <summary>
    /// Creates a new registration check instance from an array of types.
    /// </summary>
    /// <param name="types">The types to scan for <see cref="IRegistrations"/> implementations.</param>
    /// <returns>A new <see cref="RegistrationCheck"/> instance configured with discovered registrations.</returns>
    public static RegistrationCheck Create(params Type[] types)
    {
        var registrationTypes =  types.Where(x => x.IsClass && !x.IsAbstract && typeof(IRegistrations)
                .IsAssignableFrom(x))
            .ToArray();

        var registrationDictionary = registrationTypes
            .Select(registrationType => (IRegistrations)Activator.CreateInstance(registrationType))
            .Where(registrations => registrations != null)
            .GroupBy(x => x.PackageName)
            .ToDictionary(registrations => registrations.First().PackageName, registrations => DictionaryCombine(registrations.Select(x => x.GetRegistrations())));

        return new RegistrationCheck(registrationDictionary);
    }

    private static IDictionary<string, string[]> Convert(IDictionary<string, Type[]> source)
    {
        return source.ToDictionary(
            x => x.Key,
            x => x.Value.Select(type => GetSimpleTypeName(type.FullName)).ToArray());
    }

    /// <summary>
    /// Checks if a type is registered in the dependency injection container and provides registration guidance if missing.
    /// </summary>
    /// <param name="typeName">The full name of the type to check.</param>
    /// <returns>A diagnostic message indicating registration status and guidance for missing registrations.</returns>
    public string CheckType(string typeName)
    {
        typeName = GetSimpleTypeName(typeName);

        var matches = _registrations
            .SelectMany(x => x.Value.SelectMany(x1 => GetMatches(x.Key, typeName, x1)))  
            .GroupBy(x => new { x.Type, x.Package, x.Method })
            .Select(x => x.First())
            .ToArray();

        return FormatResponse(matches);
    }

    /// <summary>
    /// Best-effort, container-agnostic enrichment for a resolve failure: scans the exception and its
    /// inner exceptions for any type name Benzene knows how to register, and returns registration
    /// guidance for the innermost match. Unlike matching a single container's exact message wording,
    /// this works across DI containers, framework versions, and cultures. It never throws - a
    /// diagnostic aid must not be able to surface its own failure in place of the real resolution
    /// error.
    /// </summary>
    /// <remarks>
    /// The chain is walked innermost-first because that end of it is the root cause. An outer frame
    /// names the type whose construction was <em>attempted</em>; the innermost names the one that was
    /// actually missing, which is the one the developer has to register.
    /// </remarks>
    /// <param name="exception">The resolve exception thrown by the underlying container.</param>
    /// <returns>Registration guidance, or an empty string if nothing recognizable was found.</returns>
    public string CheckException(Exception exception)
    {
        try
        {
            foreach (var current in ChainInnermostFirst(exception))
            {
                foreach (var candidate in ExtractCandidateTypeNames(current.Message))
                {
                    var result = CheckType(candidate);
                    if (!string.IsNullOrEmpty(result))
                    {
                        return result;
                    }
                }
            }
        }
        catch
        {
            // Never let the diagnostic replace the real error it was meant to explain.
        }

        return string.Empty;
    }

    /// <summary>
    /// Registration guidance for a failed resolve, leading with the root cause found in the exception
    /// chain and offering the requested type's own registration only as secondary context. Never throws.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The requested type is the reliable branch — the caller always knows it, on any container,
    /// regardless of exception wording — but preferring it was a false-positive machine. A transitive
    /// failure (the requested type is registered; something it needs is not) produced a confident hint
    /// naming a call the developer had already made, and suppressed the one that would have fixed it.
    /// The observed case: resolving <c>MessageRouter&lt;ApiGatewayContext&gt;</c> fails on
    /// <c>IDefaultStatuses</c>, and the developer was told to add <c>.AddMessageHandlers(...)</c>,
    /// which was already there, instead of <c>.AddBenzene()</c>, which was not.
    /// </para>
    /// <para>
    /// So the chain wins when it has an answer, and the direct hint follows it clearly marked as the
    /// weaker one. It is kept rather than dropped because the chain branch depends on parsing a
    /// container's message and can come back empty on a container whose wording nothing recognises.
    /// </para>
    /// </remarks>
    /// <param name="requestedType">The type the caller was trying to resolve.</param>
    /// <param name="exception">The resolve exception thrown by the underlying container.</param>
    /// <returns>Registration guidance, or an empty string if nothing recognizable was found.</returns>
    public string Describe(Type requestedType, Exception exception)
    {
        try
        {
            var rootCause = CheckException(exception);
            var direct = requestedType?.FullName is { } name ? CheckType(name) : string.Empty;

            if (string.IsNullOrEmpty(rootCause))
            {
                return direct;
            }

            if (string.IsNullOrEmpty(direct) || direct == rootCause)
            {
                return rootCause;
            }

            return new StringBuilder(rootCause)
                .AppendLine()
                .AppendLine($"If that call is already there, {DisplayName(requestedType!)} itself comes from:")
                .Append(direct)
                .ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    // "MessageRouter<ApiGatewayContext>", not "MessageRouter`1" — this appears in a sentence a
    // developer reads, and CLR arity notation is noise there.
    private static string DisplayName(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.Name;
        }

        var name = type.Name.Split('`')[0];
        return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(DisplayName))}>";
    }

    private static List<Exception> ChainInnermostFirst(Exception exception)
    {
        var chain = new List<Exception>();
        for (var current = exception; current != null; current = current.InnerException)
        {
            chain.Add(current);
        }

        chain.Reverse();
        return chain;
    }

    // Pulls likely type names out of a container's error message without assuming any one container's
    // wording. Quoted tokens first (Microsoft DI and Autofac, like most containers, quote the type),
    // then whitespace-delimited tokens that look namespaced (e.g. SimpleInjector's unquoted "No
    // registration for type Namespace.Type ..."). All parsing is bounds-safe, so a message in any shape
    // yields candidates or nothing, never an exception.
    private static IEnumerable<string> ExtractCandidateTypeNames(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var quote in new[] { '\'', '"' })
        {
            var parts = message.Split(quote);
            // Odd segments are the quoted contents.
            for (var i = 1; i < parts.Length; i += 2)
            {
                var token = parts[i].Trim();
                if (token.Length > 0 && seen.Add(token))
                {
                    yield return token;
                }
            }
        }

        foreach (var raw in message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim('.', ',', ';', ':', '(', ')');
            if (token.Contains('.') && seen.Add(token))
            {
                yield return token;
            }
        }
    }

    // package is the assembly the registration call lives in; methodRegistrations maps one registration
    // call (".AddBenzene()") to the types it registers. The two used to be handed to RegistrationMatch
    // the wrong way round, so the message read "<assembly> is registered in .AddX() from <type>".
    private static RegistrationMatch[] GetMatches(string package, string typeNameToMatch, KeyValuePair<string, string[]> methodRegistrations)
    {
        return methodRegistrations.Value
            .Where(registeredType => IsMatch(typeNameToMatch, registeredType))
            .Select(registeredType => new RegistrationMatch(registeredType, methodRegistrations.Key, package))
            .ToArray();
    }

    private static bool IsMatch(string inputType, string registeredType)
    {
        if (registeredType.Contains("<>"))
        {
            return inputType.StartsWith(registeredType.Split(">").First());
        }

        return inputType == registeredType;
    }

    private static string GetSimpleTypeName(string typeName)
    {
        var parts = typeName.Split(new[] { '[', ',', ']' }, StringSplitOptions.RemoveEmptyEntries);

        return parts.Length == 1
            ? parts[0].Replace("`1", "<>")
            : $"{parts[0].Replace("`1", "")}<{parts[1]}>";
    }

    private static string FormatResponse(RegistrationMatch[] matches)
    {
        var stringBuilder = new StringBuilder();

        // Several registration calls can supply the same type. Saying "one of" is the honest framing:
        // a hint stated with more confidence than it deserves costs more than no hint at all.
        var lead = matches.Length > 1
            ? "You might be missing one of these in your dependency registration"
            : "You might be missing this in your dependency registration";

        foreach (var item in matches)
        {
            stringBuilder.AppendLine();
            stringBuilder.AppendLine($"{item.Type} is registered in {item.Method} from {item.Package}");
            stringBuilder.AppendLine();
            stringBuilder.AppendLine(lead);
            stringBuilder.AppendLine();
            stringBuilder.AppendLine($"    .UsingBenzene(x => x{item.Method})");
            stringBuilder.AppendLine();
        }

        return stringBuilder.ToString();
    }
    
    /// <summary>
    /// Combines multiple dictionaries into a single dictionary, using the first occurrence of each key.
    /// </summary>
    /// <typeparam name="TKey">The type of dictionary keys.</typeparam>
    /// <typeparam name="TValue">The type of dictionary values.</typeparam>
    /// <param name="source">The collection of dictionaries to combine.</param>
    /// <returns>A combined dictionary containing all unique keys from the source dictionaries.</returns>
    public static IDictionary<TKey, TValue> DictionaryCombine<TKey, TValue>(IEnumerable<IDictionary<TKey, TValue>> source)
    {
        var output = new Dictionary<TKey, TValue>();

        foreach (var dictionary in source)
        {
            foreach (var keyValue in dictionary)
            {
                output.TryAdd(keyValue.Key, keyValue.Value); 
            }
        }

        return output;
    }
}
