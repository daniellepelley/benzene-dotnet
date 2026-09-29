using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Benzene.Mcp;
using Xunit;

namespace Benzene.Test.Mcp;

public class McpSchemaTest
{
    public enum Colour { Red, DarkBlue }

    public class Line
    {
        public string Account { get; set; } = string.Empty;
        public decimal Amount { get; set; }
    }

    public class PostJournalRequest
    {
        [Required]
        [Description("The date of the journal.")]
        public DateOnly Date { get; set; }

        [JsonPropertyName("ref")]
        public string? Reference { get; set; }

        public int? Sequence { get; set; }

        public bool Draft { get; set; }

        public Guid BooksId { get; set; }

        public Colour Colour { get; set; }

        public List<Line> Lines { get; set; } = new();

        public Dictionary<string, string> Tags { get; set; } = new();

        public PostJournalRequest? Parent { get; set; }

        [JsonIgnore]
        public string Secret { get; set; } = string.Empty;
    }

    [Fact]
    public void For_MapsAPocoToAnObjectSchemaTheModelCanRead()
    {
        var schema = McpSchema.For<PostJournalRequest>();

        Assert.Equal("object", schema["type"]!.GetValue<string>());
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        var props = (JsonObject)schema["properties"]!;

        Assert.Equal("string", props["date"]!["type"]!.GetValue<string>());
        Assert.Equal("date", props["date"]!["format"]!.GetValue<string>());
        Assert.Equal("The date of the journal.", props["date"]!["description"]!.GetValue<string>());
        Assert.Equal("date", ((JsonArray)schema["required"]!)[0]!.GetValue<string>());

        Assert.NotNull(props["ref"]);
        Assert.Null(props["reference"]);
        Assert.Equal("integer", props["sequence"]!["type"]!.GetValue<string>());
        Assert.Equal("boolean", props["draft"]!["type"]!.GetValue<string>());
        Assert.Equal("uuid", props["booksId"]!["format"]!.GetValue<string>());
        Assert.Equal("darkBlue", ((JsonArray)props["colour"]!["enum"]!)[1]!.GetValue<string>());
        Assert.Equal("array", props["lines"]!["type"]!.GetValue<string>());
        Assert.Equal("number", props["lines"]!["items"]!["properties"]!["amount"]!["type"]!.GetValue<string>());
        Assert.Equal("string", props["tags"]!["additionalProperties"]!["type"]!.GetValue<string>());
        Assert.Equal("object", props["parent"]!["type"]!.GetValue<string>());
        Assert.Null(props["secret"]);
    }

    [Fact]
    public void Object_BuildsTheHandWrittenShape()
    {
        var schema = McpSchema.Object(
            ("name", McpSchema.Text("A name."), true),
            ("when", McpSchema.Date("When."), false),
            ("kind", McpSchema.Choice("Which kind.", "a", "b"), false));

        Assert.Equal("name", ((JsonArray)schema["required"]!)[0]!.GetValue<string>());
        Assert.Single((JsonArray)schema["required"]!);
        Assert.EndsWith("Written yyyy-MM-dd.", schema["properties"]!["when"]!["description"]!.GetValue<string>());
        Assert.Equal(2, ((JsonArray)schema["properties"]!["kind"]!["enum"]!).Count);
    }
}
