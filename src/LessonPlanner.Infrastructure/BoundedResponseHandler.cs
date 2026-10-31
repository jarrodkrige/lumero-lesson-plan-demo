using System.Net;

namespace LessonPlanner.Infrastructure;

public sealed class BoundedResponseHandler(int maximumBytes) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (maximumBytes <= 0) throw new InvalidOperationException("A positive response body limit is required.");
        var response = await base.SendAsync(request, cancellationToken);
        if (response.Content.Headers.ContentLength > maximumBytes)
        {
            response.Dispose();
            throw new HttpRequestException("The remote response exceeded the configured body limit.");
        }
        response.Content = new LimitedContent(response.Content, maximumBytes);
        return response;
    }

    private sealed class LimitedContent : HttpContent
    {
        private readonly HttpContent inner;
        private readonly int limit;
        public LimitedContent(HttpContent inner, int limit)
        {
            this.inner = inner;
            this.limit = limit;
            foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = inner.Headers.ContentLength ?? 0;
            return inner.Headers.ContentLength.HasValue;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            await using var bounded = await CreateContentReadStreamAsync(cancellationToken);
            await bounded.CopyToAsync(stream, cancellationToken);
        }

        protected override Task<Stream> CreateContentReadStreamAsync() => CreateContentReadStreamAsync(CancellationToken.None);

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            new LimitedReadStream(await inner.ReadAsStreamAsync(cancellationToken), limit);

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class LimitedReadStream(Stream inner, int limit) : Stream
    {
        private int consumed;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => consumed; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer[..Bound(buffer.Length)]));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer[..Bound(buffer.Length)], cancellationToken));

        private int Bound(int requested)
        {
            if (consumed > limit) throw TooLarge();
            return (int)Math.Min(requested, (long)limit - consumed + 1);
        }

        private int Count(int count)
        {
            consumed += count;
            if (consumed > limit) throw TooLarge();
            return count;
        }

        private static IOException TooLarge() => new("The remote response exceeded the configured body limit.");
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
