using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Benzene.AspNet.Core;
using Benzene.Http.RequestBody;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Benzene.Test.Asp;

/// <summary>
/// Coverage for <see cref="AspNetMessageBodyGetter"/>'s async-buffering behavior: it serves the body
/// from the scoped <see cref="HttpRequestBodyBuffer"/> when the async pre-read populated it (the
/// normal, non-blocking path), reads the stream asynchronously via
/// <see cref="IHttpRequestBodyReader{AspNetContext}"/>, and only falls back to reading the stream from
/// <c>GetBody</c> when nothing buffered it.
/// </summary>
public class AspNetMessageBodyGetterTest
{
    private static AspNetContext ContextWithBody(string body)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return new AspNetContext(httpContext);
    }

    /// <summary>A body stream that always fails to read, to exercise <c>ReadBodyAsync</c>'s catch path.</summary>
    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("stream unavailable");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void GetBody_WhenBuffered_ReturnsBufferedValue_WithoutTouchingTheStream()
    {
        var buffer = new HttpRequestBodyBuffer();
        buffer.Set("{\"buffered\":true}");
        var getter = new AspNetMessageBodyGetter(buffer, NullLogger<AspNetMessageBodyGetter>.Instance);

        // The stream body differs from the buffered value - proving GetBody serves the buffer, not the stream.
        var result = getter.GetBody(ContextWithBody("{\"fromStream\":true}"));

        Assert.Equal("{\"buffered\":true}", result);
    }

    [Fact]
    public async Task ReadBodyAsync_ReadsTheStreamBody()
    {
        var getter = new AspNetMessageBodyGetter(new HttpRequestBodyBuffer(), NullLogger<AspNetMessageBodyGetter>.Instance);

        var result = await getter.ReadBodyAsync(ContextWithBody("{\"name\":\"orders\"}"));

        Assert.Equal("{\"name\":\"orders\"}", result);
    }

    [Fact]
    public void GetBody_WhenNotBuffered_FallsBackToReadingTheStream()
    {
        // No buffering middleware ran (IsBuffered == false), so GetBody must still return the body by
        // reading the stream itself.
        var getter = new AspNetMessageBodyGetter(new HttpRequestBodyBuffer(), NullLogger<AspNetMessageBodyGetter>.Instance);

        var result = getter.GetBody(ContextWithBody("{\"fallback\":true}"));

        Assert.Equal("{\"fallback\":true}", result);
    }

    [Fact]
    public async Task ReadBodyAsync_LeavesBodyReadableForDownstream()
    {
        // EnableBuffering + position reset means a component reading the body after the pre-read still sees it.
        var context = ContextWithBody("{\"reread\":true}");
        var getter = new AspNetMessageBodyGetter(new HttpRequestBodyBuffer(), NullLogger<AspNetMessageBodyGetter>.Instance);

        await getter.ReadBodyAsync(context);

        using var reader = new StreamReader(context.HttpContext.Request.Body);
        Assert.Equal("{\"reread\":true}", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task ReadBodyAsync_StreamThrows_LogsTheSwallowedExceptionAndReturnsNull()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Body = new ThrowingStream();
        var context = new AspNetContext(httpContext);

        var mockLogger = new Mock<ILogger<AspNetMessageBodyGetter>>();
        var getter = new AspNetMessageBodyGetter(new HttpRequestBodyBuffer(), mockLogger.Object);

        var result = await getter.ReadBodyAsync(context);

        Assert.Null(result);
        // The "documented deliberate fallback path" no longer swallows the exception silently - it's
        // logged, so a body read failure is diagnosable instead of just showing up downstream as an
        // unexpectedly empty request.
        mockLogger.Verify(x => x.Log(
            LogLevel.Debug,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.Is<Exception>(ex => ex is IOException),
            (Func<It.IsAnyType, Exception, string>)It.IsAny<object>()));
    }
}
