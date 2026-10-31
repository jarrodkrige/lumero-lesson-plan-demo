using Azure.Data.Tables;
using LessonPlanner.Core;
using LessonPlanner.Infrastructure;

namespace LessonPlanner.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LocalStorageCollection : ICollectionFixture<LocalTableFixture>
{
    public const string Name = "Real loopback storage and MCP";
}

public sealed class LocalTableFixture : IAsyncLifetime
{
    // This is Azurite's published development key, never an Azure account credential.
    public const string Connection = "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;TableEndpoint=http://127.0.0.1:11002/devstoreaccount1;";
    public string TableName { get; } = $"lumerotest{Guid.NewGuid():N}";
    public TableClient Table { get; private set; } = null!;
    public QuizPolicy Policy { get; private set; } = new(new Uri("http://127.0.0.1:5180"), TimeSpan.FromHours(1), TimeSpan.FromMinutes(5), 200);
    public void UsePublicOrigin(Uri origin)
    {
        var policy = Policy with { PublicBaseUrl = origin };
        policy.Validate(true);
        Policy = policy;
    }
    public MutableClock Clock { get; } = new();

    public LocalTableFixture() { }
    internal LocalTableFixture(bool useRealTime) => Clock = new MutableClock(useRealTime);

    public async Task InitializeAsync()
    {
        Table = NewClient();
        try { await Table.CreateIfNotExistsAsync(); }
        catch (Azure.RequestFailedException error)
        {
            throw new InvalidOperationException("Real Azurite is required on 127.0.0.1:11002. Run npm run emulator before integration tests.", error);
        }
    }

    public TableClient NewClient() => new(Connection, TableName, new TableClientOptions
    { Retry = { MaxRetries = 0, NetworkTimeout = TimeSpan.FromSeconds(3) }, Diagnostics = { IsLoggingContentEnabled = false } });

    public AzureTableStore Store(int maximumAttempts = 200) => new(NewClient(), Policy with { MaximumAttempts = maximumAttempts },
        new ExactMatchGradingPolicy(), Clock);

    public async Task<QuizDraft> SaveDraftAsync(AzureTableStore? store = null)
    {
        store ??= Store();
        var lesson = TestData.Lesson();
        var draft = TestData.Draft(lesson);
        await store.SaveAsync(lesson, CancellationToken.None);
        await store.SaveDraftAsync(draft, CancellationToken.None);
        return draft;
    }

    public async Task DisposeAsync()
    {
        await Table.DeleteAsync();
        var keys = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".local", "test-keys", TableName));
        if (Directory.Exists(keys)) Directory.Delete(keys, true);
    }
}

public sealed class MutableClock(bool useRealTime = false) : TimeProvider
{
    private readonly DateTimeOffset start = DateTimeOffset.UtcNow;
    private TimeSpan offset;
    public override DateTimeOffset GetUtcNow() => (useRealTime ? DateTimeOffset.UtcNow : start) + offset;
    public void Advance(TimeSpan by) => offset += by;
}
