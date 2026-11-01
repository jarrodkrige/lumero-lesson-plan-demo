using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using LessonPlanner.Core;
using LessonPlanner.Infrastructure.Retrieval;

namespace LessonPlanner.UnitTests;

public sealed class CurriculumRetrievalSafetyTests
{
    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2001:4860:4860::8888")]
    public void PublicUnicastAddressesAreAllowed(string value) => Assert.True(PublicAddressPolicy.IsPublic(IPAddress.Parse(value)));

    [Theory]
    [InlineData("0.1.2.3")]
    [InlineData("10.1.2.3")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.254")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.0.0.9")]
    [InlineData("192.0.2.1")]
    [InlineData("192.31.196.1")]
    [InlineData("192.52.193.1")]
    [InlineData("192.88.99.1")]
    [InlineData("192.168.1.1")]
    [InlineData("192.175.48.1")]
    [InlineData("198.18.0.1")]
    [InlineData("198.19.255.254")]
    [InlineData("198.51.100.1")]
    [InlineData("203.0.113.1")]
    [InlineData("224.0.0.1")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("64:ff9b::808:808")]
    [InlineData("64:ff9b:1::1")]
    [InlineData("100::1")]
    [InlineData("2001::1")]
    [InlineData("2001:20::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2002:808:808::1")]
    [InlineData("2620:4f:8000::1")]
    [InlineData("3fff::1")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    [InlineData("ff02::1")]
    [InlineData("2606:4700:4700::1111%8")]
    public void LocalPrivateSpecialAndTransitionAddressesAreRejected(string value) =>
        Assert.False(PublicAddressPolicy.IsPublic(IPAddress.Parse(value)));

    [Fact]
    public void CatalogueContainsOnlyExplicitSourceIdentifiersAndHttpsDocuments()
    {
        Assert.Equal(5, CuratedCurriculumCatalogue.Sources.Count);
        Assert.All(CuratedCurriculumCatalogue.Sources, source =>
        {
            Assert.Equal("https", source.Url.Scheme);
            Assert.DoesNotContain(":", source.Id, StringComparison.Ordinal);
        });
        Assert.All(CuratedCurriculumCatalogue.Sources.Take(2), source =>
            Assert.EndsWith(".pdf", source.Url.AbsolutePath, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(SortingTeachingSources.All, CuratedCurriculumCatalogue.Sources.Skip(2));
        Assert.Contains(CuratedCurriculumCatalogue.Sources, source => source.Id == CuratedCurriculumCatalogue.OcrJ277Specification);
        Assert.Contains(CuratedCurriculumCatalogue.Sources, source => source.Id == CuratedCurriculumCatalogue.DfeComputingKeyStages3And4);
    }

    [Fact]
    public async Task UnknownIdentifiersCannotBecomeUrls()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(PdfResponse()));
        using var retriever = Retriever(handler);
        var exception = await Assert.ThrowsAsync<DomainException>(() => retriever.RetrieveAsync("https://attacker.example.org/file.pdf", CancellationToken.None));
        Assert.Equal("source_not_allowed", exception.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SocketUsesOneValidatedDnsSnapshotWithoutASecondHostnameLookup()
    {
        var resolver = new StubResolver(call => call == 1 ? [IPAddress.Parse("8.8.8.8")] : [IPAddress.Loopback]);
        var connector = new StubConnector(_ => new MemoryStream());
        var connection = new PinnedHttpsConnection(resolver, connector);
        using var stream = await connection.ConnectAsync(new("www.ocr.org.uk", 443), CancellationToken.None);
        Assert.Equal(1, resolver.Calls);
        var endpoint = Assert.Single(connector.Endpoints);
        Assert.Equal(IPAddress.Parse("8.8.8.8"), endpoint.Address);
        Assert.Equal(443, endpoint.Port);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("::ffff:10.1.2.3")]
    [InlineData("fe80::1")]
    public async Task MixedPublicAndForbiddenDnsAnswersFailClosed(string privateAddress)
    {
        var resolver = new StubResolver(_ => [IPAddress.Parse("8.8.8.8"), IPAddress.Parse(privateAddress)]);
        var connector = new StubConnector(_ => new MemoryStream());
        var connection = new PinnedHttpsConnection(resolver, connector);
        var exception = await Assert.ThrowsAsync<DomainException>(async () =>
            await connection.ConnectAsync(new("www.ocr.org.uk", 443), CancellationToken.None));
        Assert.Equal("source_address_rejected", exception.Code);
        Assert.Empty(connector.Endpoints);
    }

    [Fact]
    public async Task FailoverOnlyTriesValidatedIpEndpoints()
    {
        var resolver = new StubResolver(_ => [IPAddress.Parse("8.8.8.8"), IPAddress.Parse("1.1.1.1")]);
        var connector = new StubConnector(endpoint => endpoint.Address.Equals(IPAddress.Parse("8.8.8.8"))
            ? throw new SocketException((int)SocketError.ConnectionRefused) : new MemoryStream());
        using var stream = await new PinnedHttpsConnection(resolver, connector).ConnectAsync(new("www.ocr.org.uk", 443), CancellationToken.None);
        Assert.Equal(1, resolver.Calls);
        Assert.Equal(2, connector.Endpoints.Count);
        Assert.Equal(IPAddress.Parse("1.1.1.1"), connector.Endpoints[1].Address);
    }

    [Theory]
    [InlineData("www.ocr.org.uk", 80)]
    [InlineData("www.ocr.org.uk.attacker.example.org", 443)]
    [InlineData("127.0.0.1", 443)]
    public async Task PinnedConnectionAlsoChecksHostAndPort(string host, int port)
    {
        var resolver = new StubResolver(_ => [IPAddress.Parse("8.8.8.8")]);
        var connector = new StubConnector(_ => new MemoryStream());
        await Assert.ThrowsAsync<DomainException>(async () =>
            await new PinnedHttpsConnection(resolver, connector).ConnectAsync(new(host, port), CancellationToken.None));
        Assert.Equal(0, resolver.Calls);
        Assert.Empty(connector.Endpoints);
    }

    [Fact]
    public void DefaultHandlerHasNoRedirectProxyCookieOrDecompressionBypass()
    {
        using var handler = PinnedHttpsConnection.CreateHandler(new());
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.False(handler.UseCookies);
        Assert.False(handler.PreAuthenticate);
        Assert.Null(handler.Credentials);
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
        Assert.Null(handler.ActivityHeadersPropagator);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        Assert.Equal(0, handler.MaxResponseDrainSize);
        Assert.Equal(16, handler.MaxResponseHeadersLength);
        Assert.Equal(TimeSpan.Zero, handler.PooledConnectionLifetime);
        Assert.NotNull(handler.ConnectCallback);
    }

    [Fact]
    public async Task AllowedRedirectRetainsCanonicalProvenanceAndRawPayloadHash()
    {
        var handler = new StubHandler((_, call) => Task.FromResult(call == 1
            ? Redirect("https://ocr.org.uk/Images/558027-specification-gcse-computer-science-j277.pdf") : PdfResponse()));
        using var retriever = Retriever(handler);
        var result = await retriever.RetrieveAsync(CuratedCurriculumCatalogue.OcrJ277Specification, CancellationToken.None);
        Assert.Equal(CuratedCurriculumCatalogue.Sources[0].Url, result.Reference.Url);
        Assert.Equal(RetrievalRenderingFixtures.Timestamp, result.Reference.RetrievedAt);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(RetrievalRenderingFixtures.TransportPayload)), result.Reference.Sha256);
        Assert.Contains("sorting", result.Reference.Extract, StringComparison.Ordinal);
        Assert.InRange(result.Reference.Extract.Length, 1, 512);
        Assert.Equal(RetrievalRenderingFixtures.OcrText, result.Content);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(HttpVersion.Version11, request.Version);
            Assert.Equal(HttpVersionPolicy.RequestVersionExact, request.VersionPolicy);
            Assert.Equal("identity", request.AcceptEncoding);
            Assert.True(request.CloseConnection);
            Assert.False(request.HasCookie);
        });
    }

    [Theory]
    [InlineData("http://www.ocr.org.uk/Images/558027-specification-gcse-computer-science-j277.pdf")]
    [InlineData("https://127.0.0.1/file.pdf")]
    [InlineData("https://www.ocr.org.uk.attacker.example.org/file.pdf")]
    [InlineData("https://www.ocr.org.uk@attacker.example.org/file.pdf")]
    [InlineData("https://www.ocr.org.uk:8443/Images/558027-specification-gcse-computer-science-j277.pdf")]
    [InlineData("https://www.ocr.org.uk/Images/unapproved.pdf")]
    [InlineData("https://www.ocr.org.uk/Images/558027-specification-gcse-computer-science-j277.pdf?next=private")]
    [InlineData("https://www.ocr.org.uk/Images/558027-specification-gcse-computer-science-j277.pdf#fragment")]
    [InlineData("//attacker.example.org/file.pdf")]
    [InlineData("https://assets.publishing.service.gov.uk/media/5a7cb981ed915d682236228d/SECONDARY_national_curriculum_-_Computing.pdf")]
    public async Task EveryRedirectMustStayOnThatSourcesExactAllowlist(string target)
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Redirect(target)));
        using var retriever = Retriever(handler);
        var exception = await Assert.ThrowsAsync<DomainException>(() => retriever.RetrieveAsync(CuratedCurriculumCatalogue.OcrJ277Specification, CancellationToken.None));
        Assert.Equal("source_url_rejected", exception.Code);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RelativeRedirectLoopsAreRejectedBeforeAnotherRequest()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Redirect("/Images/558027-specification-gcse-computer-science-j277.pdf")));
        using var retriever = Retriever(handler);
        var exception = await Assert.ThrowsAsync<DomainException>(() => retriever.RetrieveAsync(CuratedCurriculumCatalogue.OcrJ277Specification, CancellationToken.None));
        Assert.Equal("source_redirect_rejected", exception.Code);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RedirectBudgetIsEnforced()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Redirect("https://ocr.org.uk/Images/558027-specification-gcse-computer-science-j277.pdf")));
        using var retriever = Retriever(handler, new() { MaximumRedirects = 0 });
        var exception = await Assert.ThrowsAsync<DomainException>(() => retriever.RetrieveAsync(CuratedCurriculumCatalogue.OcrJ277Specification, CancellationToken.None));
        Assert.Equal("source_redirect_rejected", exception.Code);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task MissingRedirectLocationIsRejected()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)));
        using var retriever = Retriever(handler);
        var exception = await Assert.ThrowsAsync<DomainException>(() => retriever.RetrieveAsync(CuratedCurriculumCatalogue.OcrJ277Specification, CancellationToken.None));
        Assert.Equal("source_redirect_rejected", exception.Code);
    }

    [Theory]
    [InlineData(HttpStatusCode.PartialContent)]
    [InlineData(HttpStatusCode.NotModified)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task OnlyACompleteSuccessfulResponseIsAccepted(HttpStatusCode status)
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)));
        using var retriever = Retriever(handler);
        var exception = await Assert.ThrowsAsync<DomainException>(() => retriever.RetrieveAsync(CuratedCurriculumCatalogue.OcrJ277Specification, CancellationToken.None));
        Assert.Equal("source_unavailable", exception.Code);
    }

    [Theory]
    [InlineData("text/html", null)]
    [InlineData("application/octet-stream", null)]
    [InlineData("application/pdf", "gzip")]
    [InlineData("application/pdf", "br")]
    [InlineData("application/pdf", "deflate")]
    public async Task UnexpectedMediaTypesAndEncodedResponsesAreRejected(string mediaType, string? encoding)
    {
        var handler = new StubHandler((_, _) =>
        {
            var response = PdfResponse();
            response.Content.Headers.ContentType = new(mediaType);
            if (encoding is not null) response.Content.Headers.ContentEncoding.Add(encoding);
            return Task.FromResult(response);
        });
        using var retriever = Retriever(handler);
        var exception = await Assert.ThrowsAsync<DomainException>(() => retriever.RetrieveAsync(CuratedCurriculumCatalogue.OcrJ277Specification, CancellationToken.None));
        Assert.Equal("source_invalid_response", exception.Code);
    }

    [Fact]
    public async Task DeclaredOversizeIsRejectedBeforeReadingTheBody()
    {
        var stream = new NonSeekableStream(new byte[10]);
        var handler = new StubHandler((_, _) =>
        {
            var response = PdfResponse(new StreamContent(stream));
            response.Content.Headers.ContentLength = 1000;
            return Task.FromResult(response);
        });
        using var retriever = Retriever(handler, new() { MaximumDownloadBytes = 64 });
        var exception = await Assert.ThrowsAsync<DomainException>(() => retriever.RetrieveAsync(CuratedCurriculumCatalogue.OcrJ277Specification, CancellationToken.None));
        Assert.Equal("source_payload_limit", exception.Code);
        Assert.Equal(0, stream.Reads);
    }

    [Fact]
    public async Task UnknownLengthDownloadsAreStillBounded()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(PdfResponse(new StreamContent(new NonSeekableStream(new byte[65])))));
        using var retriever = Retriever(handler, new() { MaximumDownloadBytes = 64 });
        var exception = await Assert.ThrowsAsync<DomainException>(() => retriever.RetrieveAsync(CuratedCurriculumCatalogue.OcrJ277Specification, CancellationToken.None));
        Assert.Equal("source_payload_limit", exception.Code);
    }

    [Fact]
    public async Task TruncatedDownloadsCannotCreateProvenance()
    {
        var handler = new StubHandler((_, _) =>
        {
            var response = PdfResponse();
            response.Content.Headers.ContentLength = RetrievalRenderingFixtures.TransportPayload.Length + 1;
            return Task.FromResult(response);
        });
        using var retriever = Retriever(handler);
        var exception = await Assert.ThrowsAsync<DomainException>(() => retriever.RetrieveAsync(CuratedCurriculumCatalogue.OcrJ277Specification, CancellationToken.None));
        Assert.Equal("source_invalid_response", exception.Code);
    }

    [Fact]
    public async Task ResponseHeadersHaveATotalTimeBudget()
    {
        var handler = new StubHandler(async (token, _) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return PdfResponse();
        });
        using var retriever = Retriever(handler, FastTimeouts());
        var exception = await Assert.ThrowsAsync<DomainException>(() => retriever.RetrieveAsync(CuratedCurriculumCatalogue.OcrJ277Specification, CancellationToken.None));
        Assert.Equal("source_timeout", exception.Code);
    }

    [Fact]
    public async Task ResponseBodyReadsShareTheTimeBudget()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(PdfResponse(new StreamContent(new StallingStream()))));
        using var retriever = Retriever(handler, FastTimeouts());
        var exception = await Assert.ThrowsAsync<DomainException>(() => retriever.RetrieveAsync(CuratedCurriculumCatalogue.OcrJ277Specification, CancellationToken.None));
        Assert.Equal("source_timeout", exception.Code);
    }

    [Fact]
    public async Task CallerCancellationIsNotRewrittenAsASourceFailure()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var handler = new StubHandler((_, _) => Task.FromResult(PdfResponse()));
        using var retriever = Retriever(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retriever.RetrieveAsync(CuratedCurriculumCatalogue.OcrJ277Specification, cancellation.Token));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("\0")]
    [InlineData("\u202E")]
    [InlineData("\u2066")]
    [InlineData("\uFFFD")]
    public async Task InvalidExtractedTextIsRejected(string invalid)
    {
        var handler = new StubHandler((_, _) => Task.FromResult(PdfResponse()));
        using var retriever = Retriever(handler, text: RetrievalRenderingFixtures.OcrText + invalid);
        var exception = await Assert.ThrowsAsync<DomainException>(() => retriever.RetrieveAsync(CuratedCurriculumCatalogue.OcrJ277Specification, CancellationToken.None));
        Assert.Equal("source_invalid_text", exception.Code);
    }

    [Fact]
    public async Task AChangedOrUnrelatedDocumentCannotMasqueradeAsTheCatalogueSource()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(PdfResponse()));
        using var retriever = Retriever(handler, text: RetrievalRenderingFixtures.DfeText);
        var exception = await Assert.ThrowsAsync<DomainException>(() => retriever.RetrieveAsync(CuratedCurriculumCatalogue.OcrJ277Specification, CancellationToken.None));
        Assert.Equal("source_invalid_text", exception.Code);
    }

    [Fact]
    public async Task ExtractedTextBudgetIsEnforcedBeforeProvenanceIsReturned()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(PdfResponse()));
        using var retriever = Retriever(handler, text: new string('a', 180_001));
        var exception = await Assert.ThrowsAsync<DomainException>(() => retriever.RetrieveAsync(CuratedCurriculumCatalogue.OcrJ277Specification, CancellationToken.None));
        Assert.Equal("source_text_limit", exception.Code);
    }

    [Fact]
    public async Task KeyStageThreeSourceUsesItsOwnIdentityMarkers()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(PdfResponse()));
        using var retriever = Retriever(handler, text: RetrievalRenderingFixtures.DfeText);
        var source = await retriever.RetrieveAsync(CuratedCurriculumCatalogue.DfeComputingKeyStages3And4, CancellationToken.None);
        Assert.Equal(CuratedCurriculumCatalogue.Sources[1].Url, source.Reference.Url);
        Assert.Contains("robot", source.Content, StringComparison.Ordinal);
    }

    private static CurriculumRetrievalOptions FastTimeouts() => new()
    {
        TotalTimeout = TimeSpan.FromMilliseconds(100), ConnectTimeout = TimeSpan.FromMilliseconds(50),
        ExtractionTimeout = TimeSpan.FromMilliseconds(50)
    };
    private static CuratedCurriculumRetriever Retriever(StubHandler handler, CurriculumRetrievalOptions? options = null,
        string text = RetrievalRenderingFixtures.OcrText) => new(options ?? new(), new FixedTimeProvider(), handler, new StubExtractor(text));
    private static HttpResponseMessage PdfResponse(HttpContent? content = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content ?? new ByteArrayContent(RetrievalRenderingFixtures.TransportPayload) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        return response;
    }
    private static HttpResponseMessage Redirect(string location) => new(HttpStatusCode.Redirect)
    {
        Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) }
    };
    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => RetrievalRenderingFixtures.Timestamp;
    }
    private sealed class StubExtractor(string text) : ICurriculumTextExtractor
    {
        public string Extract(byte[] payload, CurriculumRetrievalOptions options, CancellationToken cancellationToken) => text;
    }
    private sealed record RequestSnapshot(Version Version, HttpVersionPolicy VersionPolicy, string AcceptEncoding,
        bool CloseConnection, bool HasCookie);
    private sealed class StubHandler(Func<CancellationToken, int, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        internal List<RequestSnapshot> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new(request.Version, request.VersionPolicy, string.Join(",", request.Headers.AcceptEncoding),
                request.Headers.ConnectionClose == true, request.Headers.Contains("Cookie")));
            return respond(cancellationToken, Requests.Count);
        }
    }
    private sealed class StubResolver(Func<int, IPAddress[]> resolve) : IHostAddressResolver
    {
        internal int Calls { get; private set; }
        public ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) => new(resolve(++Calls));
    }
    private sealed class StubConnector(Func<IPEndPoint, Stream> connect) : IAddressConnector
    {
        internal List<IPEndPoint> Endpoints { get; } = [];
        public ValueTask<Stream> ConnectAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
        {
            Endpoints.Add(endpoint);
            return new(connect(endpoint));
        }
    }
    private class NonSeekableStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream inner = new(bytes, writable: false);
        internal int Reads { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { Reads++; return inner.Read(buffer, offset, count); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reads++;
            return inner.ReadAsync(buffer, cancellationToken);
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
    private sealed class StallingStream() : NonSeekableStream([])
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
