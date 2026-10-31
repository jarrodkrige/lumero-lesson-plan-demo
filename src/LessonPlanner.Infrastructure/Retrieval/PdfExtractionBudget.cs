using System.Diagnostics;
using LessonPlanner.Core;

namespace LessonPlanner.Infrastructure.Retrieval;

internal sealed class PdfExtractionBudget(TimeSpan timeout, CancellationToken cancellationToken)
{
    internal const int MaximumDecodedStreamBytes = 8 * 1024 * 1024;
    private const long MaximumTotalDecodedBytes = 64L * 1024 * 1024;
    private const long MaximumReadBytes = 128L * 1024 * 1024;
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private long totalDecodedBytes;
    private long totalReadBytes;
    private int operations;
    private int decodedStreams;

    internal void Check()
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (elapsed.Elapsed > timeout)
            throw new DomainException("source_timeout", "Curriculum extraction exceeded its time budget.");
        if (++operations > 30_000_000)
            throw new DomainException("source_pdf_limit", "The PDF exceeds the parser operation budget.");
    }

    internal void ChargeRead(int bytes)
    {
        Check();
        totalReadBytes += bytes;
        if (totalReadBytes > MaximumReadBytes)
            throw new DomainException("source_pdf_limit", "The PDF exceeds the parser read budget.");
    }

    internal void ChargeDecoded(long bytes)
    {
        Check();
        totalDecodedBytes += bytes;
        if (bytes > MaximumDecodedStreamBytes || totalDecodedBytes > MaximumTotalDecodedBytes || ++decodedStreams > 4096)
            throw new DomainException("source_pdf_limit", "The PDF exceeds the decoded-stream budget.");
    }
}

internal sealed class BudgetedPdfStream(byte[] bytes, PdfExtractionBudget budget) : Stream
{
    private readonly MemoryStream inner = new(bytes, writable: false);
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length { get { budget.Check(); return inner.Length; } }
    public override long Position
    {
        get { budget.Check(); return inner.Position; }
        set { budget.Check(); inner.Position = value; }
    }
    public override int Read(byte[] buffer, int offset, int count)
    {
        budget.Check();
        var read = inner.Read(buffer, offset, count);
        budget.ChargeRead(read);
        return read;
    }
    public override int Read(Span<byte> buffer)
    {
        budget.Check();
        var read = inner.Read(buffer);
        budget.ChargeRead(read);
        return read;
    }
    public override int ReadByte()
    {
        budget.Check();
        var read = inner.ReadByte();
        budget.ChargeRead(read < 0 ? 0 : 1);
        return read;
    }
    public override long Seek(long offset, SeekOrigin origin)
    {
        budget.Check();
        return inner.Seek(offset, origin);
    }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
