using System;
using System.Threading.Tasks;
using Benzene.Mcp;
using Xunit;

namespace Benzene.Test.Mcp;

public class McpBuilderTest
{
    [Fact]
    public void Build_DefaultsToTheMcpPath()
    {
        var options = new McpBuilder().Tool("a", "A tool.", "topic:a").Build();

        Assert.Equal("/mcp", options.Path);
        Assert.Equal("topic:a", Assert.Single(options.Tools).Topic);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("")]
    [InlineData("dots.are.out")]
    public void Build_RejectsAnInvalidToolName(string name)
    {
        var builder = new McpBuilder().Tool(name, "A tool.", "topic:a");

        var ex = Assert.Throws<ArgumentException>(() => builder.Build());
        Assert.Contains("not valid", ex.Message);
    }

    [Fact]
    public void Build_RejectsADuplicateToolName()
    {
        var builder = new McpBuilder().Tool("a", "A tool.", "topic:a").Tool("a", "Another.", "topic:b");

        var ex = Assert.Throws<ArgumentException>(() => builder.Build());
        Assert.Contains("registered twice", ex.Message);
    }

    [Fact]
    public void Build_RejectsAToolWithNeitherTopicNorBody_AndOneWithBoth()
    {
        var neither = new McpBuilder().Tool(new McpToolDefinition("a", "A tool.", McpSchema.Object()));
        Assert.Contains("neither a topic nor a body", Assert.Throws<ArgumentException>(() => neither.Build()).Message);

        var both = new McpBuilder().Tool(new McpToolDefinition("a", "A tool.", McpSchema.Object())
        {
            Topic = "topic:a",
            Invoke = (_, _, _) => Task.FromResult(McpToolResult.Ok("x")),
        });
        Assert.Contains("both a topic and a body", Assert.Throws<ArgumentException>(() => both.Build()).Message);
    }

    [Fact]
    public void Build_RejectsAToolWithoutADescription()
    {
        var builder = new McpBuilder().Tool("a", " ", "topic:a");

        Assert.Contains("no description", Assert.Throws<ArgumentException>(() => builder.Build()).Message);
    }
}
