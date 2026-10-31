# Curriculum retrieval and lesson artifacts

These Infrastructure components implement the typed Core boundaries. They do not provision cloud resources, publish quizzes, discover arbitrary URLs, or call a hosted document/QR service.

## Integration

The application composition root should register:

```csharp
services.AddSingleton<ICurriculumRetriever, CuratedCurriculumRetriever>();
services.AddSingleton<ILessonDocumentRenderer, OpenXmlLessonDocumentRenderer>();
services.AddSingleton<IQuizQrRenderer>(_ => new LocalQuizQrRenderer());
```

The implementations are in `LessonPlanner.Infrastructure.Retrieval` and `LessonPlanner.Infrastructure.Rendering`. `CuratedCurriculumRetriever` owns its HTTP client and is disposed by the service provider. Optional `CurriculumRetrievalOptions` and `TimeProvider` dependencies can be registered centrally. Options are validated against hard upper bounds; callers cannot configure an arbitrary source URL.

Required centrally managed package references:

- [PdfPig 0.1.16](https://www.nuget.org/packages/PdfPig/0.1.16), Apache-2.0; implementation APIs verified against the [v0.1.16 upstream tag](https://github.com/UglyToad/PdfPig/tree/v0.1.16).
- [DocumentFormat.OpenXml 3.5.1](https://www.nuget.org/packages/DocumentFormat.OpenXml/3.5.1), MIT.
- [QRCoder 1.8.1](https://www.nuget.org/packages/QRCoder/1.8.1), MIT.

The Infrastructure reference for the PDF package is `<PackageReference Include="PdfPig" />`; its central version is `<PackageVersion Include="PdfPig" Version="0.1.16" />`. Do not substitute the differently named legacy `UglyToad.PdfPig` package.

## Curated curriculum catalogue

Agent plans select an ID from `ICurriculumRetriever.Sources`; URL text from a model or presenter is not accepted by `RetrieveAsync`.

- `ocr-j277-specification`: [OCR GCSE Computer Science J277 specification](https://www.ocr.org.uk/Images/558027-specification-gcse-computer-science-j277.pdf). The exact same path on `ocr.org.uk` is the sole additional redirect target. Identity checks expect J277, algorithms, and sorting; the short evidence extract is anchored around sorting.
- `dfe-computing-ks3-ks4`: [DfE computing programmes of study for key stages 3 and 4](https://assets.publishing.service.gov.uk/media/5a7cb981ed915d682236228d/SECONDARY_national_curriculum_-_Computing.pdf). Only this exact resource is allowed. Identity checks expect computing, key stage 3, and algorithms. This supports an original robot sports day teaching context; that context is not attributed to the DfE publication.

These canonical resource links were checked against their publishers' public catalogue pages during implementation. No raw specification text or source PDF is committed. The automated tests generate original, explicitly synthetic PDF fixtures in memory. They do not demonstrate successful retrieval of the real public PDFs or current publisher availability. A publisher URL/content change fails closed and requires an intentional catalogue/profile review rather than broadening the allowlist at runtime.

## Retrieval trust boundary

Every initial URL and redirect must be an exact approved HTTPS resource, with port 443 and no user information, query, or fragment. Only standard 301/302/303/307/308 redirects are handled; redirect loops, missing locations, and cross-source redirects fail. The HTTP handler does not automatically follow redirects.

For each connection, DNS is resolved once. An empty result, more than 16 addresses, or any private/local/special address in the result is rejected. IPv4-mapped IPv6, scoped addresses, transition mechanisms, documentation blocks, multicast, and reserved space are rejected. Socket connections use only validated IP endpoints from that snapshot, including failover. No second hostname lookup occurs. Normal .NET TLS hostname and certificate validation remains enabled. HTTP/1.1 is forced, connections are closed rather than reused, and proxies, cookies, credentials, automatic decompression, and outbound activity-header propagation are disabled. Restricted egress environments must allow these publishers directly; do not disable TLS verification or route around the address policy.

Defaults and hard caps:

- Download: 8 MiB by default; configurable up to 16 MiB. Declared and actual streamed lengths are checked independently, including unknown-length responses.
- Response headers: 16 KiB. Only HTTP 200 with PDF media type and absent/identity content encoding is accepted.
- Total operation: 30 seconds by default, at most one minute. Connection and extraction budgets default to five seconds each and cannot exceed the total budget.
- Redirects: three by default, at most five.
- PDF pages: 100 by default, at most 128. Encrypted, malformed, unsupported-encoding, and incomplete PDFs fail closed.
- Extracted text: 180,000 characters by default, at most 250,000. The evidence extract defaults to 512 characters and cannot exceed 800.
- PDF streams: 8 MiB decoded per stream, 64 MiB aggregate decoded data, 128 MiB aggregate parser reads, 4,096 decoded streams, four filters per chain, and 30 million checked parser operations.

The maintained PDF parser is wrapped with bounded reads, strict parsing, limited stack depth, supported-filter checks, and bounded streaming preflight before its Flate decoder allocates output. Predictor parameters and row sizes are checked. Extraction time/cancellation checks are cooperative in process, not an OS sandbox or a guarantee that every internal parser loop can be forcibly interrupted. Deployments requiring hard CPU/memory isolation should execute PDF extraction in a separately resource-limited worker; the reference boundary does not claim to provide that isolation.

Text validation rejects malformed Unicode, replacement characters, unsupported controls, and invisible formatting characters; it normalizes whitespace and requires expected publication markers. Marker checks are a change/misdirection tripwire, not proof of authorship. Extracted content remains **untrusted external data**, even when downloaded over authenticated HTTPS. Agent prompts must delimit it as source data, never as instructions or permission to call tools.

Successful retrieval returns a `SourceReference` containing the catalogue ID/title, original canonical publisher URL (not a redirect alias), retrieval timestamp, lowercase SHA-256 of the actual downloaded bytes, and a short extract. The digest establishes payload identity, not a publisher signature. Failure messages are stable domain errors and do not echo response bodies, request secrets, or low-level socket details. Caller cancellation propagates as cancellation.

## Genuine lesson downloads

`OpenXmlLessonDocumentRenderer` renders only a current-schema, Ready lesson with an accepted review. It revalidates typed lesson data, duration totals, citations, and XML characters before rendering. Output is a genuine macro-free DOCX package, named `lesson-plan.docx`, with the Open XML document MIME type.

The document includes styled headings, objectives and source IDs, prerequisites, teaching notes, elapsed activity timings and total duration, assessments, materials, canonical source URLs, retrieval timestamps, payload hashes, and short evidence extracts. URLs and all other input are plain typed text. No HTML, raw XML, field codes, AltChunk, external resource relationships, embedded files, or macros are introduced. Owner IDs, internal record IDs, credentials, and marking keys are not written to the document.

The endpoint must return `LessonDocument.Content` with the supplied MIME type and safe download filename, and enforce the application's presenter authorization before calling the renderer.

## Local QR generation

`LocalQuizQrRenderer` encodes the exact `AbsoluteUri` of the supplied already canonical quiz URL into a black-and-white PNG with a quiet zone and Q error correction. Encoding is performed locally with QRCoder; there is no QR web API, DNS lookup, or network fetch.

The renderer rejects relative/noncanonical URLs, user information, query/fragment data, empty quiz paths, oversized input, non-HTTPS URLs, private/special IP literals, and common internal host forms. It intentionally does not prove DNS ownership or reachability. The publishing workflow must first persist/publish the quiz, construct its canonical public URL from trusted configuration (not a model-provided host or untrusted request Host header), and verify that the route is reachable before returning that same URL and its QR artifact. The renderer alone cannot satisfy public-reachability acceptance testing.

`new LocalQuizQrRenderer(allowHttpLocalhost: true)` is an explicit **local-verification-only** option. It permits HTTP/HTTPS only on exact `localhost`, `127.0.0.1`, or `::1`; it does not authorize private LAN addresses, other insecure public hosts, or localhost lookalikes. A QR pointing to localhost is not reachable from a separate participant device and must never be presented as a published workshop QR.

The public constructor contract is `LocalQuizQrRenderer(bool allowHttpLocalhost = false)`. For production, retain the default registration shown above. An isolated Testing/local host using a URL such as `http://127.0.0.1:49152/quiz/<id>` must instead register:

```csharp
services.AddSingleton<IQuizQrRenderer>(_ => new LocalQuizQrRenderer(allowHttpLocalhost: true));
```

Select that registration only in the explicitly isolated local test host, not from request input. The renderer does not infer the hosting environment or automatically relax policy after a successful reachability check: the default instance deliberately rejects loopback HTTP with `quiz_url_invalid`. With the explicit option enabled, the supplied canonical scheme, host, port, and quiz path are encoded unchanged; all other URL safeguards remain active.

## Focused verification

The new test classes are `CurriculumRetrievalSafetyTests`, `CurriculumPdfExtractionTests`, and `LessonArtifactRenderingTests`, with original synthetic fixtures in `RetrievalRenderingFixtures`.

```powershell
dotnet test tests\LessonPlanner.UnitTests\LessonPlanner.UnitTests.csproj --no-restore --filter "FullyQualifiedName~CurriculumRetrievalSafetyTests|FullyQualifiedName~CurriculumPdfExtractionTests|FullyQualifiedName~LessonArtifactRenderingTests"
```

Transport/DNS tests use deterministic local doubles; PDF tests parse generated documents and exercise malformed/resource-limit cases. DOCX tests reopen and structurally validate packages with the Open XML validator and inspect relationships. QR tests validate the PNG and compare every pixel against the library's module matrix for the exact canonical input; this is not a substitute for an independent phone scan against a reachable deployed quiz. None of these tests require Azure or transmit lesson data to third parties.
