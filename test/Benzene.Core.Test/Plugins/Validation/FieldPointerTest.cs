using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Benzene.Abstractions.Serialization;
using Benzene.Abstractions.Validation;
using Xunit;

namespace Benzene.Test.Plugins.Validation;

// wire-contracts §1.3: errors[].field is a JSON Pointer into the handler's request type in its JSON
// form - the serializer's naming policy and rename attributes applied - and a path that names no
// single location omits field.
public class FieldPointerTest
{
    public class PointerOrder
    {
        public string Name { get; set; } = string.Empty;
        public List<PointerItem> Items { get; set; } = new();
        public PointerItem[] Extras { get; set; } = System.Array.Empty<PointerItem>();
        public IEnumerable<string> Tags { get; set; } = new List<string>();
        public PointerAddress? Address { get; set; }
        public int? Quantity { get; set; }
        public int[][] Matrix { get; set; } = System.Array.Empty<int[]>();
        public Dictionary<string, PointerItem> ByKey { get; set; } = new();

        [JsonPropertyName("line_items")]
        public List<PointerItem> LineItems { get; set; } = new();

        [JsonPropertyName("a/b~c")]
        public string Odd { get; set; } = string.Empty;

        [JsonIgnore]
        public string Secret { get; set; } = string.Empty;

        // A public field: System.Text.Json does not serialize fields unless IncludeFields is set, so
        // under the default options it has no location in the JSON form.
        public string Field = string.Empty;
    }

    public class PointerItem
    {
        public string Sku { get; set; } = string.Empty;
    }

    public class PointerAddress
    {
        public string Line1 { get; set; } = string.Empty;
    }

    [Theory]
    [InlineData("Name", "/name")]
    [InlineData("Items[0].Sku", "/items/0/sku")]
    [InlineData("Items[12]", "/items/12")]
    [InlineData("Extras[1].Sku", "/extras/1/sku")]
    [InlineData("Tags[3]", "/tags/3")]
    [InlineData("Address.Line1", "/address/line1")]
    [InlineData("Quantity", "/quantity")]
    [InlineData("Matrix[0][1]", "/matrix/0/1")]
    [InlineData("LineItems[2].Sku", "/line_items/2/sku")]
    [InlineData("Odd", "/a~1b~0c")]
    [InlineData("name", "/name")]
    public void FromPropertyPath_PointsIntoTheJsonForm(string propertyPath, string expected)
    {
        Assert.Equal(expected, FieldPointer.FromPropertyPath(typeof(PointerOrder), propertyPath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("Missing")]
    [InlineData("Items[0].Missing")]
    [InlineData("Secret")]
    [InlineData("Field")]
    [InlineData("ByKey[0].Sku")]
    [InlineData("Items[x].Sku")]
    [InlineData("Name[0]")]
    [InlineData("Items[0")]
    [InlineData("Items..Sku")]
    [InlineData(".Name")]
    [InlineData("Name.")]
    public void FromPropertyPath_NoSingleLocation_IsNull(string? propertyPath)
    {
        Assert.Null(FieldPointer.FromPropertyPath(typeof(PointerOrder), propertyPath));
    }

    [Fact]
    public void FromPropertyPath_FollowsTheConfiguredNamingPolicy()
    {
        var names = new JsonPropertyNameResolver(new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        });

        Assert.Equal("/items/0/sku", FieldPointer.FromPropertyPath(typeof(PointerOrder), "Items[0].Sku", names));
        Assert.Equal("/address/line1", FieldPointer.FromPropertyPath(typeof(PointerOrder), "Address.Line1", names));
        // A rename attribute wins over the policy, exactly as it does on the wire.
        Assert.Equal("/line_items/0/sku", FieldPointer.FromPropertyPath(typeof(PointerOrder), "LineItems[0].Sku", names));
    }

    [Fact]
    public void FromPropertyPath_NoNamingPolicy_KeepsTheDotNetNames()
    {
        var names = new JsonPropertyNameResolver(new JsonSerializerOptions());

        Assert.Equal("/Items/0/Sku", FieldPointer.FromPropertyPath(typeof(PointerOrder), "Items[0].Sku", names));
    }

    [Fact]
    public void JsonPropertyNameResolver_DoesNotLockTheCallersOptions()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var names = new JsonPropertyNameResolver(options);

        Assert.Equal("name", names.GetJsonPropertyName(typeof(PointerOrder), nameof(PointerOrder.Name)));
        Assert.False(options.IsReadOnly);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a/b", "a~1b")]
    [InlineData("a~b", "a~0b")]
    [InlineData("~/", "~0~1")]
    public void Escape_FollowsRfc6901(string token, string expected)
    {
        Assert.Equal(expected, FieldPointer.Escape(token));
    }
}
