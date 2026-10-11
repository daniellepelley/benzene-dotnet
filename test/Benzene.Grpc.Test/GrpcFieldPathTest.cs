using Xunit;

namespace Benzene.Grpc.Test;

// wire-contracts §4.2: FieldViolation.field is a dotted path - the §1.3 pointer's segments, unescaped
// per RFC 6901, joined with '.', with a segment made only of decimal digits written [n].
public class GrpcFieldPathTest
{
    [Theory]
    [InlineData("/name", "name")]
    [InlineData("/items/0/sku", "items[0].sku")]
    [InlineData("/address/line1", "address.line1")]
    [InlineData("/matrix/0/1", "matrix[0][1]")]
    [InlineData("/tags/12", "tags[12]")]
    [InlineData("/0/sku", "[0].sku")]
    [InlineData("/a~1b/c~0d", "a/b.c~d")]
    [InlineData("/v2name", "v2name")]
    public void FromPointer_ConvertsToTheDottedForm(string pointer, string expected)
    {
        Assert.Equal(expected, GrpcFieldPath.FromPointer(pointer));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void FromPointer_NoField_IsEmpty(string? pointer)
    {
        Assert.Equal(string.Empty, GrpcFieldPath.FromPointer(pointer));
    }

    [Fact]
    public void FromPointer_NotAPointer_PassesThroughUnchanged()
    {
        Assert.Equal("Name", GrpcFieldPath.FromPointer("Name"));
    }

    [Theory]
    [InlineData("name", "/name")]
    [InlineData("items[0].sku", "/items/0/sku")]
    [InlineData("address.line1", "/address/line1")]
    [InlineData("matrix[0][1]", "/matrix/0/1")]
    [InlineData("[0].sku", "/0/sku")]
    [InlineData("a/b.c~d", "/a~1b/c~0d")]
    public void ToPointer_ConvertsBackToAJsonPointer(string dotted, string expected)
    {
        Assert.Equal(expected, GrpcFieldPath.ToPointer(dotted));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ToPointer_NoField_IsNull(string? dotted)
    {
        Assert.Null(GrpcFieldPath.ToPointer(dotted));
    }

    [Theory]
    [InlineData("/name")]
    [InlineData("/items/0/sku")]
    [InlineData("/matrix/0/1")]
    [InlineData("/a~1b/c~0d")]
    public void RoundTrip_PreservesThePointer(string pointer)
    {
        Assert.Equal(pointer, GrpcFieldPath.ToPointer(GrpcFieldPath.FromPointer(pointer)));
    }
}
