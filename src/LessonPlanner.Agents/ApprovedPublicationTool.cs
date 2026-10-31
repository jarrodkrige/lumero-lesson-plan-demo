using System.Text.Json;
using LessonPlanner.Core;
using Microsoft.Extensions.AI;

namespace LessonPlanner.Agents;

internal sealed class ApprovedPublicationTool : AIFunction
{
    internal const string ToolName = "publish_approved_quiz";
    private static readonly JsonElement EmptyArgumentsSchema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new Dictionary<string, object>(),
        required = Array.Empty<string>(),
        additionalProperties = false
    });
    private readonly object _gate = new();
    private readonly IMcpPublicationClient _client;
    private readonly ApprovedPublication _approval;
    private readonly PublicationCaller _caller;
    private readonly CancellationToken _lifetime;
    private readonly TimeProvider _clock;
    private Task<PublishedQuiz>? _attempt;

    internal ApprovedPublicationTool(IMcpPublicationClient client, ApprovedPublication approval,
        PublicationCaller caller, TimeProvider clock, CancellationToken lifetime)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(clock);
        DomainRules.RequirePresenter(caller.Presenter);
        DomainRules.RequireId(approval.ApprovalId);
        DomainRules.RequireId(approval.DraftId);
        if (approval.Version < 1 || !AgentContractGuard.Sha256(approval.PayloadHash) ||
            !Guid.TryParse(caller.ClientId, out var clientId) || clientId == Guid.Empty)
            throw new DomainException("publication_approval", "A valid bound approval and caller are required.");
        _client = client;
        _approval = approval;
        _caller = caller;
        _clock = clock;
        _lifetime = lifetime;
    }

    public override string Name => ToolName;
    public override string Description => "Publish only the immutable quiz already approved by the presenter. No arguments are accepted.";
    public override JsonElement JsonSchema => EmptyArgumentsSchema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _lifetime.ThrowIfCancellationRequested();
        if (arguments.Count != 0)
            throw new DomainException("publication_arguments", "The publication tool does not accept model-supplied arguments.");
        Task<PublishedQuiz> attempt;
        lock (_gate)
        {
            // Cache failures too. Retrying an ambiguous publication requires the same server-side approval.
            attempt = _attempt ??= PublishCoreAsync(cancellationToken);
        }
        await attempt.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new { published = true };
    }

    internal Task<PublishedQuiz> RequireReceiptAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return (_attempt ?? throw new DomainException("publication_not_invoked",
                "The publisher did not invoke the approved publication tool.")).WaitAsync(cancellationToken);
        }
    }

    private async Task<PublishedQuiz> PublishCoreAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime, cancellationToken);
        var receipt = await _client.PublishAsync(_approval, _caller, linked.Token).ConfigureAwait(false);
        if (receipt is null || !DomainRules.IsId(receipt.Id) || !AgentContractGuard.Text(receipt.Title, 3, 160) ||
            !AgentContractGuard.Https(receipt.PublicUrl) || receipt.PublishedAt == default ||
            receipt.PublishedAt > _clock.GetUtcNow().AddMinutes(5) || receipt.ExpiresAt <= receipt.PublishedAt ||
            receipt.ExpiresAt <= _clock.GetUtcNow())
            throw new DomainException("publication_receipt", "The publication service returned an invalid receipt.");
        return receipt;
    }
}
