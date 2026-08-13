using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Benzene.Outbox;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Benzene.Test.Outbox;

public class BufferedOutboxStageTest
{
    private static OutboxEnvelope NewEnvelope(string id = "env-1")
        => new(id, "test:topic", "\"payload\"", typeof(string).AssemblyQualifiedName!, new Dictionary<string, string>(), DateTimeOffset.UtcNow);

    [Fact]
    public async Task StageAsync_BuffersTheEnvelope_WithoutPersistingIt()
    {
        var stage = new BufferedOutboxStage();

        await stage.StageAsync(NewEnvelope());

        Assert.Single(stage.DrainStaged());
    }

    [Fact]
    public async Task DrainStaged_ReturnsEverythingStaged_AndClearsTheBuffer()
    {
        var stage = new BufferedOutboxStage();
        await stage.StageAsync(NewEnvelope("env-1"));
        await stage.StageAsync(NewEnvelope("env-2"));

        var drained = stage.DrainStaged();

        Assert.Equal(2, drained.Count);
        Assert.Empty(stage.DrainStaged());
    }

    [Fact]
    public void Dispose_WithNoStagedEnvelopes_DoesNotLog()
    {
        var mockLogger = new Mock<ILogger>();
        var stage = new BufferedOutboxStage(mockLogger.Object);

        stage.Dispose();

        mockLogger.Verify(x => x.Log(
            It.IsAny<LogLevel>(),
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception>(),
            (Func<It.IsAnyType, Exception, string>)It.IsAny<object>()), Times.Never);
    }

    [Fact]
    public async Task Dispose_WithUndrainedStagedEnvelopes_LogsAWarning()
    {
        var mockLogger = new Mock<ILogger>();
        var stage = new BufferedOutboxStage(mockLogger.Object);
        await stage.StageAsync(NewEnvelope());

        stage.Dispose();

        mockLogger.Verify(x => x.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("1")),
            It.IsAny<Exception>(),
            (Func<It.IsAnyType, Exception, string>)It.IsAny<object>()), Times.Once);
    }
}
