using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using LessonPlanner.Core;

namespace LessonPlanner.Infrastructure.Retrieval;

public sealed class CuratedCurriculumRetriever : ICurriculumRetriever, IDisposable
{
    private readonly HttpClient client;
    private readonly CurriculumRetrievalOptions options;
    private readonly TimeProvider timeProvider;
    private readonly ICurriculumTextExtractor extractor;

    public CuratedCurriculumRetriever(CurriculumRetrievalOptions? options = null, TimeProvider? timeProvider = null)
        : this(options ?? new(), timeProvider ?? TimeProvider.System, null, null)
    {
    }

    internal CuratedCurriculumRetriever(CurriculumRetrievalOptions options, TimeProvider timeProvider,
        HttpMessageHandler? handler, ICurriculumTextExtractor? extractor)
    {
        options.Validate();
        this.options = options;
        this.timeProvider = timeProvider;
        this.extractor = extractor ?? new BoundedPdfTextExtractor();
        client = new HttpClient(handler ?? PinnedHttpsConnection.CreateHandler(options), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public IReadOnlyList<CurriculumSource> Sources => CuratedCurriculumCatalogue.Sources;

    public async Task<RetrievedSource> RetrieveAsync(string sourceId, CancellationToken cancellationToken)
    {
        var entry = CuratedCurriculumCatalogue.Find(sourceId);
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.TotalTimeout);
        try
        {
            byte[] payload;
            try { payload = await DownloadAsync(entry, deadline.Token).ConfigureAwait(false); }
            catch (IOException)
            {
                throw new DomainException("source_unavailable", "The curriculum source download was incomplete.");
            }
            using var extractionDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            extractionDeadline.CancelAfter(options.ExtractionTimeout);
            var extracted = entry.Format == CurriculumSourceFormat.Pdf
                ? extractor.Extract(payload, options, extractionDeadline.Token)
                : await OakLessonTextExtractor.ExtractAsync(payload, entry, extractionDeadline.Token).ConfigureAwait(false);
            var text = CurriculumText.ValidateAndNormalize(extracted,
                entry, options, extractionDeadline.Token);
            extractionDeadline.Token.ThrowIfCancellationRequested();
            var hash = Convert.ToHexStringLower(SHA256.HashData(payload));
            var extract = entry.Format == CurriculumSourceFormat.Pdf
                ? CurriculumText.ShortExtract(text, entry.ExtractAnchor, options.MaximumExtractCharacters)
                : FullTeachingExtract(text, options.MaximumExtractCharacters);
            var reference = new SourceReference(entry.Source.Id, entry.Source.Title, entry.Source.Url,
                timeProvider.GetUtcNow(), hash, extract);
            return new(reference, text);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DomainException("source_timeout", "Curriculum retrieval exceeded its time budget.");
        }
        catch (HttpRequestException exception) when (exception.InnerException is DomainException failure)
        {
            throw new DomainException(failure.Code, failure.Message);
        }
        catch (HttpRequestException)
        {
            throw new DomainException("source_unavailable", "The curriculum source could not be downloaded securely.");
        }
        catch (FormatException)
        {
            throw new DomainException("source_invalid_response", "The curriculum source returned malformed response metadata.");
        }
    }

    private async Task<byte[]> DownloadAsync(CatalogueEntry entry, CancellationToken cancellationToken)
    {
        var location = entry.Source.Url;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var redirects = 0; ; redirects++)
        {
            entry.RequireAllowedLocation(location);
            if (!visited.Add(location.AbsoluteUri))
                throw new DomainException("source_redirect_rejected", "The curriculum source returned a redirect loop.");

            using var request = new HttpRequestMessage(HttpMethod.Get, location)
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            var mediaType = entry.Format == CurriculumSourceFormat.Pdf ? "application/pdf" : "text/html";
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(mediaType));
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
            request.Headers.ConnectionClose = true;
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (IsRedirect(response.StatusCode))
            {
                if (redirects >= options.MaximumRedirects || response.Headers.Location is not { } redirect)
                    throw new DomainException("source_redirect_rejected", "The curriculum source returned an unsupported redirect.");
                location = redirect.IsAbsoluteUri ? redirect : new Uri(location, redirect);
                entry.RequireAllowedLocation(location);
                continue;
            }

            if (response.StatusCode != HttpStatusCode.OK)
                throw new DomainException("source_unavailable", "The curriculum source did not return a complete document.");
            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, mediaType, StringComparison.OrdinalIgnoreCase) ||
                response.Content.Headers.ContentEncoding.Any(encoding => !string.Equals(encoding, "identity", StringComparison.OrdinalIgnoreCase)))
                throw new DomainException("source_invalid_response", "The curriculum source returned an unsupported media type or encoding.");
            if (entry.Format == CurriculumSourceFormat.OakLessonHtml &&
                !string.Equals(response.Content.Headers.ContentType?.CharSet?.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase))
                throw new DomainException("source_invalid_response", "The teaching source must declare UTF-8 text.");

            var maximumBytes = entry.Format == CurriculumSourceFormat.OakLessonHtml
                ? Math.Min(options.MaximumDownloadBytes, 1024 * 1024)
                : options.MaximumDownloadBytes;
            var length = response.Content.Headers.ContentLength;
            if (length is <= 0 || length > maximumBytes)
                throw new DomainException("source_payload_limit", "The curriculum source exceeds the download budget or is empty.");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
            try
            {
                while (true)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    if (output.Length + read > maximumBytes)
                        throw new DomainException("source_payload_limit", "The curriculum source exceeds the download budget.");
                    output.Write(buffer, 0, read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (output.Length == 0 || (length is not null && output.Length != length.Value))
                throw new DomainException("source_invalid_response", "The curriculum source returned an empty or incomplete document.");
            return output.ToArray();
        }
    }

    private static string FullTeachingExtract(string text, int maximumCharacters)
    {
        var extract = text.Replace('\n', ' ');
        if (extract.Length > maximumCharacters)
            throw new DomainException("source_text_limit", "The teaching evidence exceeds the extract budget.");
        return extract;
    }

    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.MovedPermanently or
        HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    public void Dispose() => client.Dispose();
}
