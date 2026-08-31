using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Benzene.CodeGen.Cli.Core.Commands.HealthCheck;

public static class Extensions
{
    /// <summary>
    /// Pretty-prints <paramref name="json"/> if it parses as JSON. A non-JSON body (empty string,
    /// plain text, an HTML error page, etc.) is "a response shape this tool doesn't recognise" -
    /// the same tolerance <see cref="HealthCheckCommand"/>'s own <c>IsHealthy</c> already applies
    /// one step later - so it's written out verbatim instead of throwing.
    /// </summary>
    public static void WriteJson(this TextWriter source, string json)
    {
        string output;
        try
        {
            output = JValue.Parse(json).ToString(Formatting.Indented);
        }
        catch (JsonException)
        {
            source.WriteLine(json);
            return;
        }

        source.WriteLine(output);
    }
}