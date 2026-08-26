using System.Threading.Tasks;
using Benzene.Cache.Redis;
using Moq;
using StackExchange.Redis;

namespace Benzene.Test.Cache.Redis.Mocks;

internal class MockConnectionFactory : IRedisConnectionFactory
{
    public Mock<IDatabase> DataBaseMock { get; }
    public Mock<IConnectionMultiplexer> ConnectionMultiplexerMock { get; }
    public int ConnectCallCount { get; private set; }

    public MockConnectionFactory()
    {
        DataBaseMock = new Mock<IDatabase>();
        ConnectionMultiplexerMock = new Mock<IConnectionMultiplexer>();
        ConnectionMultiplexerMock.Setup(x => x.GetDatabase(-1, null)).Returns(DataBaseMock.Object);
    }

    public Task<IConnectionMultiplexer> ConnectAsync(ConfigurationOptions options)
    {
        ConnectCallCount++;
        return Task.FromResult(ConnectionMultiplexerMock.Object);
    }
}
