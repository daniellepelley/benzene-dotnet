using System;
using System.Text.Json.Nodes;
using Benzene.Mcp;
using Xunit;

namespace Benzene.Test.Mcp;

public class McpArgsTest
{
    private static JsonObject Args(string json) => (JsonObject)JsonNode.Parse(json)!;

    [Fact]
    public void RequiredText_RefusesMissingOrBlank()
    {
        Assert.Equal("'name' is required.", Assert.Throws<McpRequestException>(() => McpArgs.RequiredText(Args("{}"), "name")).Message);
        Assert.Throws<McpRequestException>(() => McpArgs.RequiredText(Args("{\"name\":\"  \"}"), "name"));
        Assert.Equal("bob", McpArgs.RequiredText(Args("{\"name\":\" bob \"}"), "name"));
    }

    [Fact]
    public void Date_IsIsoOrRefused()
    {
        Assert.Equal("2026-03-01", McpArgs.RequiredDate(Args("{\"when\":\"2026-03-01\"}"), "when"));
        Assert.Equal(new DateOnly(2026, 3, 1), McpArgs.RequiredDateOnly(Args("{\"when\":\"2026-03-01\"}"), "when"));
        var ex = Assert.Throws<McpRequestException>(() => McpArgs.RequiredDate(Args("{\"when\":\"March 2026\"}"), "when"));
        Assert.Contains("yyyy-MM-dd", ex.Message);
    }

    [Fact]
    public void Decimal_AcceptsNumbersAndMoneyStrings()
    {
        Assert.Equal(12.5m, McpArgs.RequiredDecimal(Args("{\"amount\":12.5}"), "amount"));
        Assert.Equal(1234m, McpArgs.RequiredDecimal(Args("{\"amount\":\"£1,234.00\"}"), "amount"));
        Assert.Throws<McpRequestException>(() => McpArgs.RequiredDecimal(Args("{\"amount\":\"lots\"}"), "amount"));
    }

    [Fact]
    public void Flag_AcceptsTrueAsAStringAndChoiceEnforcesTheSet()
    {
        Assert.True(McpArgs.OptionalFlag(Args("{\"draft\":\"true\"}"), "draft"));
        Assert.False(McpArgs.OptionalFlag(Args("{\"draft\":false}"), "draft"));
        Assert.Null(McpArgs.OptionalFlag(Args("{}"), "draft"));

        Assert.Equal("Sales", McpArgs.OptionalChoice(Args("{\"kind\":\"sales\"}"), "kind", "Sales", "Purchases"));
        var ex = Assert.Throws<McpRequestException>(() => McpArgs.OptionalChoice(Args("{\"kind\":\"other\"}"), "kind", "Sales", "Purchases"));
        Assert.Contains("Sales, Purchases", ex.Message);
    }

    [Fact]
    public void Lists_AcceptArraysAndCommaSeparatedText_ObjectsAreRefusedByPosition()
    {
        Assert.Equal(new[] { "a", "b" }, McpArgs.OptionalList(Args("{\"tags\":[\"a\",\" b \"]}"), "tags"));
        Assert.Equal(new[] { "a", "b" }, McpArgs.OptionalList(Args("{\"tags\":\"a, b\"}"), "tags"));

        Assert.Equal(2, McpArgs.Objects(Args("{\"items\":[{},{}]}"), "items").Count);
        Assert.Single(McpArgs.Objects(Args("{\"items\":\"[{\\\"x\\\":1}]\"}"), "items"));
        var ex = Assert.Throws<McpRequestException>(() => McpArgs.Objects(Args("{\"items\":[{},1]}"), "items"));
        Assert.StartsWith("Item 2 of 'items'", ex.Message);
    }
}
