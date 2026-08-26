using System.IO;
using System.Text.Json;
using Benzene.GoogleCloud.Functions.Http.TestHelpers;
using Xunit;

namespace Benzene.TestHelpers.SmokeTest;

// #81: Benzene.GoogleCloud.Functions.Http.TestHelpers had zero coverage from Benzene.sln - this
// puts a basic scenario into the standard test baseline.
public class GoogleCloudHttpSmokeTest
{
    [Fact]
    public void Build_SetsMethodPathHeaderAndBody()
    {
        var context = new HttpContextBuilder("POST", "/hello")
            .WithHeader("x-trace-id", "abc123")
            .WithBody(new SmokeMessage { Name = "World" })
            .Build();

        Assert.Equal("POST", context.Request.Method);
        Assert.Equal("/hello", context.Request.Path.Value);
        Assert.Equal("abc123", context.Request.Headers["x-trace-id"].ToString());

        context.Request.Body.Position = 0;
        using var reader = new StreamReader(context.Request.Body);
        var body = JsonSerializer.Deserialize<SmokeMessage>(reader.ReadToEnd());
        Assert.Equal("World", body!.Name);
    }
}
