using System.Text.Json;
using Azure.Core;
using LessonPlanner.Core;
using ModelContextProtocol.Client;

namespace LessonPlanner.Infrastructure;

public sealed record McpConnectionOptions(Uri Endpoint, string TokenScope)
{
    public void Validate(bool allowLoopback)
    {
        if (!Endpoint.IsAbsoluteUri || !string.IsNullOrEmpty(Endpoint.UserInfo) ||
            !string.IsNullOrEmpty(Endpoint.Query) || !string.IsNullOrEmpty(Endpoint.Fragment) ||
            (Endpoint.Scheme != "https" && !(allowLoopback && Endpoint.IsLoopback && Endpoint.Scheme == "http")) ||
            string.IsNullOrWhiteSpace(TokenScope) || !TokenScope.EndsWith("/.default", StringComparison.Ordinal))
            throw new InvalidOperationException("MCP requires a configured HTTPS endpoint and its own API token scope.");
    }
}

public sealed class OfficialMcpPublicationClient(McpConnectionOptions options, TokenCredential credential, QuizPolicy policy)
    : IMcpPublicationClient
{
    public const string PresenterTenantHeader = "X-Lumero-Presenter-Tenant";
    public const string PresenterObjectHeader = "X-Lumero-Presenter-Object";

    public async Task<PublishedQuiz> PublishAsync(ApprovedPublication approval, PublicationCaller caller,
        CancellationToken cancellationToken)
    {
        DomainRules.RequirePresenter(caller.Presenter);
        DomainRules.RequireId(approval.DraftId);
        DomainRules.RequireId(approval.ApprovalId);
        var token = await credential.GetTokenAsync(new TokenRequestContext([options.TokenScope]), cancellationToken);
        using var http = new HttpClient(new BoundedResponseHandler(65536)
        {
            InnerHandler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                UseProxy = false,
                MaxResponseHeadersLength = 16,
                ConnectTimeout = TimeSpan.FromSeconds(10)
            }
        }) { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 65536 };
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = options.Endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            ConnectionTimeout = TimeSpan.FromSeconds(15),
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {token.Token}",
                [PresenterTenantHeader] = caller.Presenter.TenantId.ToString("D"),
                [PresenterObjectHeader] = caller.Presenter.ObjectId.ToString("D")
            }
        }, http, ownsHttpClient: false);
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
        var result = await client.CallToolAsync(ContractVersions.PublishAction,
            new Dictionary<string, object?> { ["request"] = approval }, cancellationToken: cancellationToken);
        var structured = result.StructuredContent?.ToString();
        if (result.IsError == true || string.IsNullOrWhiteSpace(structured) || structured.Length > 4096)
            throw new DomainException("publication_failed", "The publication service did not return a valid receipt. No success has been assumed.");
        PublishedQuiz receipt;
        try { receipt = JsonSerializer.Deserialize<PublishedQuiz>(structured, DomainRules.Json) ?? throw InvalidReceipt(); }
        catch (JsonException) { throw InvalidReceipt(); }
        if (receipt.Id != approval.DraftId || receipt.PublicUrl != policy.PublicUrl(approval.DraftId) ||
            receipt.Title is null || receipt.Title.Length is < 3 or > 160 || receipt.ExpiresAt <= receipt.PublishedAt)
            throw InvalidReceipt();
        return receipt;
    }

    private static DomainException InvalidReceipt() => new("publication_receipt", "The publication service returned an inconsistent receipt.");
}
