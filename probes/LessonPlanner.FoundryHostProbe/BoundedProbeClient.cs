using Microsoft.Extensions.AI;

namespace LessonPlanner.FoundryHostProbe;

internal sealed class BoundedProbeClient(IChatClient inner, string deployment) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(messages, Bounded(options), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(messages, Bounded(options), cancellationToken);

    private ChatOptions Bounded(ChatOptions? options)
    {
        var bounded = options?.Clone() ?? new ChatOptions();
        bounded.ModelId = deployment;
        bounded.MaxOutputTokens = 128;
        bounded.Tools = [];
        bounded.ToolMode = ChatToolMode.None;
        return bounded;
    }
}
