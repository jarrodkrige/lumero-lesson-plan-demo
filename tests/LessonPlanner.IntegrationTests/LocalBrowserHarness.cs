namespace LessonPlanner.IntegrationTests;

public sealed class LocalBrowserHarness : IAsyncDisposable
{
    private readonly LocalTableFixture storage;
    private readonly WebTestHost web;
    public Uri Origin => web.Origin;

    private LocalBrowserHarness(LocalTableFixture storage, WebTestHost web)
    {
        this.storage = storage;
        this.web = web;
    }

    public static async Task<LocalBrowserHarness> StartAsync(int port)
    {
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var storage = new LocalTableFixture(useRealTime: true);
        await storage.InitializeAsync();
        try { return new(storage, await WebTestHost.StartAsync(storage, port)); }
        catch { await storage.DisposeAsync(); throw; }
    }

    public Task WaitForShutdownAsync(CancellationToken cancellationToken) => web.WaitForShutdownAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await web.DisposeAsync();
        await storage.DisposeAsync();
    }
}
