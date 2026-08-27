using System.Text.Json.Serialization;

namespace Benzene.Test.Autogen.CodeGen.Model;

// #166: a request DTO carrying both an enum shape the server serializes as a string (via
// JsonStringEnumConverter) and one it serializes as its raw number (the System.Text.Json default) -
// the two shapes OpenApiSchemaCSharpTypeBuilder must emit as real C# enums, not empty classes.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OrderStatus
{
    Pending,
    Shipped,
    Delivered,
}

public enum Priority
{
    Low,
    Medium,
    High = 5,
}

public class EnumRequest
{
    public OrderStatus Status { get; set; }
    public Priority Level { get; set; }
}
