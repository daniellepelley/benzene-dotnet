using Benzene.Core.Messages.BenzeneMessage;
using Benzene.Mcp;
using Xunit;

namespace Benzene.Test.Mcp;

public class McpResultsTest
{
    [Fact]
    public void Success_IsTheBodyOrTheStatusWhenThereIsNone()
    {
        Assert.Equal("{\"a\":1}", McpResults.ToToolResult(new BenzeneMessageResponse { IsSuccessful = true, StatusCode = "ok", Body = "{\"a\":1}" }).Text);
        var empty = McpResults.ToToolResult(new BenzeneMessageResponse { IsSuccessful = true, StatusCode = "accepted", Body = "" });
        Assert.Equal("accepted", empty.Text);
        Assert.False(empty.IsError);
    }

    [Theory]
    [InlineData("{\"type\":\"about:blank\",\"title\":\"Not found\",\"detail\":\"No such order.\"}", "not-found: No such order.")]
    [InlineData("{\"errors\":[{\"message\":\"Name is required.\"},{\"message\":\"Date is in the future.\"}]}", "not-found: Name is required. Date is in the future.")]
    [InlineData("{\"message\":\"Nope.\"}", "not-found: Nope.")]
    [InlineData("plain text", "not-found: plain text")]
    [InlineData("", "not-found: The service answered 'not-found'.")]
    public void Failure_NamesTheStatusAndTheMostSpecificMessage(string body, string expected)
    {
        var result = McpResults.ToToolResult(new BenzeneMessageResponse { IsSuccessful = false, StatusCode = "not-found", Body = body });

        Assert.True(result.IsError);
        Assert.Equal(expected, result.Text);
    }
}
