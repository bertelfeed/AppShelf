using System.Collections.Concurrent;

namespace AppShelf.Windows;

/// <summary>One long-lived STA thread for frequent short COM calls, instead of a new thread per call.</summary>
public sealed class StaWorker : IDisposable
{
    private readonly BlockingCollection<Action> jobs = new(64);
    public StaWorker(string name)
    {
        var thread = new Thread(() => { foreach (var job in jobs.GetConsumingEnumerable()) job(); }) { IsBackground = true, Name = name };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }
    public Task<T> Run<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        // A full queue means the shell is not answering; dropping the request beats piling up work.
        if (!jobs.TryAdd(() => { try { completion.SetResult(action()); } catch (Exception ex) { completion.SetException(ex); } }))
            completion.SetException(new TimeoutException("Shell request queue is full."));
        return completion.Task;
    }
    public void Dispose() => jobs.CompleteAdding();
}
