using System.Collections.Concurrent;
using LessonPlanner.Core;

namespace LessonPlanner.Web.Services;

public sealed class ModelWorkGate
{
    private readonly SemaphoreSlim total = new(2, 2);
    private readonly ConcurrentDictionary<PresenterIdentity, SemaphoreSlim> presenters = new();

    public IDisposable? TryEnter(PresenterIdentity presenter)
    {
        var personal = presenters.GetOrAdd(presenter, _ => new SemaphoreSlim(1, 1));
        if (!personal.Wait(0)) return null;
        if (total.Wait(0)) return new Permit(total, personal);
        personal.Release();
        return null;
    }

    private sealed class Permit(SemaphoreSlim total, SemaphoreSlim personal) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            total.Release();
            personal.Release();
        }
    }
}
