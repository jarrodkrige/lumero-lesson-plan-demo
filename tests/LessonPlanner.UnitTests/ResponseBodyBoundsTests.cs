using System.Net;
using LessonPlanner.Infrastructure;

namespace LessonPlanner.UnitTests;

public sealed class ResponseBodyBoundsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(16)]
    public async Task Bounded_streaming_and_buffered_responses_preserve_valid_content(int size)
    {
        foreach (var streamed in new[] { false, true })
        {
            using var handler = Handler(size, false);
            using var http = new HttpClient(handler);
            using var response = await http.GetAsync("https://mcp.example.org/mcp", streamed ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead);
            Assert.Equal(new string('a', size), await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Declared_oversized_response_is_rejected_before_reading()
    {
        using var handler = Handler(17, true);
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("https://mcp.example.org/mcp", HttpCompletionOption.ResponseHeadersRead));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Undeclared_oversized_response_is_rejected_during_streaming(bool synchronous)
    {
        using var handler = Handler(17, false);
        using var http = new HttpClient(handler);
        using var response = await http.GetAsync("https://mcp.example.org/mcp", HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync();
        if (synchronous) Assert.Throws<IOException>(() => stream.CopyTo(Stream.Null));
        else await Assert.ThrowsAsync<IOException>(() => stream.CopyToAsync(Stream.Null));
    }

    [Fact]
    public async Task Undeclared_oversized_response_is_rejected_during_buffering()
    {
        using var handler = Handler(17, false);
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("https://mcp.example.org/mcp"));
    }

    private static BoundedResponseHandler Handler(int size, bool declared) => new(16)
    { InnerHandler = new StubHandler(size, declared) };

    private sealed class StubHandler(int size, bool declared) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new StreamContent(new ForwardOnlyStream(new MemoryStream(Enumerable.Repeat((byte)'a', size).ToArray())));
            if (declared) content.Headers.ContentLength = size;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class ForwardOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
