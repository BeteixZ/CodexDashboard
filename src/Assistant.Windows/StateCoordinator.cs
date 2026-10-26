using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace CodexDashboard;

/// <summary>Serializes state transitions while allowing awaited I/O to yield.</summary>
internal interface IStateCoordinator : IDisposable, IAsyncDisposable
{
    bool IsOwnerThread { get; }
    void Invoke(Action action);
    T Invoke<T>(Func<T> action);
    Task InvokeAsync(Func<Task> action);
    Task<T> InvokeAsync<T>(Func<Task<T>> action);
}

internal sealed class SerialStateCoordinator : IStateCoordinator
{
    private readonly BlockingCollection<Action> callbacks = new();
    private readonly object lifecycle = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int ownerThreadId, operations;
    private bool closing;

    internal SerialStateCoordinator()
    {
        new Thread(Run) { IsBackground = true, Name = "Assistant state coordinator" }.Start();
        ready.Task.GetAwaiter().GetResult();
    }

    public bool IsOwnerThread => Environment.CurrentManagedThreadId == ownerThreadId;

    public void Invoke(Action action) => Invoke(() => { action(); return 0; });

    public T Invoke<T>(Func<T> action)
    {
        // Reentrant state access, including a transport's immediate callback,
        // must never wait for the owner thread that is already executing it.
        if (IsOwnerThread) return action();
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Admit(() =>
        {
            try { result.TrySetResult(action()); }
            catch (Exception ex) { result.TrySetException(ex); }
            finally { CompleteOperation(); }
        });
        return result.Task.GetAwaiter().GetResult();
    }

    public Task InvokeAsync(Func<Task> action) => InvokeAsync(async () => { await action(); return 0; });

    public Task<T> InvokeAsync<T>(Func<Task<T>> action)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Admit(async () =>
        {
            try { result.TrySetResult(await action()); }
            catch (OperationCanceledException ex) { result.TrySetCanceled(ex.CancellationToken); }
            catch (Exception ex) { result.TrySetException(ex); }
            finally { CompleteOperation(); }
        });
        return result.Task;
    }

    private void Admit(Action action)
    {
        lock (lifecycle)
        {
            if (closing) throw new ObjectDisposedException(nameof(SerialStateCoordinator));
            operations++;
            callbacks.Add(action);
        }
    }

    private void CompleteOperation()
    {
        lock (lifecycle)
        {
            operations--;
            if (closing && operations == 0) callbacks.CompleteAdding();
        }
    }

    private void Run()
    {
        ownerThreadId = Environment.CurrentManagedThreadId;
        SynchronizationContext.SetSynchronizationContext(new OwnerContext(this));
        ready.TrySetResult();
        Exception? callbackFailure = null;
        try
        {
            foreach (var callback in callbacks.GetConsumingEnumerable())
            {
                try { callback(); }
                catch (Exception ex) { callbackFailure ??= ex; }
            }
        }
        finally
        {
            callbacks.Dispose();
            if (callbackFailure == null) stopped.TrySetResult();
            else stopped.TrySetException(callbackFailure);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (lifecycle)
        {
            if (!closing)
            {
                closing = true;
                // Continue accepting SynchronizationContext continuations for
                // admitted operations until every operation actually finishes.
                if (operations == 0) callbacks.CompleteAdding();
            }
        }
        return new(stopped.Task);
    }

    public void Dispose()
    {
        var shutdown = DisposeAsync();
        if (!IsOwnerThread) shutdown.AsTask().GetAwaiter().GetResult();
    }

    private sealed class OwnerContext(SerialStateCoordinator owner) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state)
            => owner.callbacks.Add(() => callback(state));
        public override void Send(SendOrPostCallback callback, object? state)
            => owner.Invoke(() => callback(state));
        public override SynchronizationContext CreateCopy() => this;
    }
}
