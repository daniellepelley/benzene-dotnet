using System;
using Benzene.SchemaRegistry.Core;
using Xunit;

namespace Benzene.Test.SchemaRegistry;

public class SchemaDefinitionTest
{
    // Regression test for #93: constructing a SchemaDefinition with a null/empty/whitespace Subject
    // used to sail through uncaught, so RegisterAsync later crashed with a raw, confusing
    // ArgumentNullException ("Value cannot be null. (Parameter 'key')") from deep inside a
    // Dictionary<string,...> null-key lookup. The constructor must now reject it immediately with a
    // clear ArgumentException naming the actual problem.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_NullOrEmptyOrWhitespaceSubject_ThrowsClearArgumentException(string? subject)
    {
        var ex = Assert.Throws<ArgumentException>(() => new SchemaDefinition(subject!, "{\"v\":1}"));

        Assert.Equal("subject", ex.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_NullOrEmptyOrWhitespaceSchema_ThrowsClearArgumentException(string? schema)
    {
        var ex = Assert.Throws<ArgumentException>(() => new SchemaDefinition("orders-value", schema!));

        Assert.Equal("schema", ex.ParamName);
    }

    [Fact]
    public void Constructor_ValidSubjectAndSchema_Succeeds()
    {
        var definition = new SchemaDefinition("orders-value", "{\"v\":1}", SchemaFormat.Json);

        Assert.Equal("orders-value", definition.Subject);
        Assert.Equal("{\"v\":1}", definition.Schema);
        Assert.Equal(SchemaFormat.Json, definition.Format);
    }
}
