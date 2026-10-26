using System;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace CodexDashboard;

/// <summary>
/// Excludes other upgraded receivers for this Windows user and bot, regardless of
/// profile directory or desktop session. Legacy executables need migration guards.
/// </summary>
internal sealed class ReceiverLease : IDisposable, IAsyncDisposable
{
    private readonly ManualResetEventSlim release = new(false);
    private readonly TaskCompletionSource<ReceiverLease?> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string name;
    private int releaseRequested;

    private ReceiverLease(string botId)
    {
        using var identity = WindowsIdentity.GetCurrent();
        string user = identity.User?.Value ?? throw new InvalidOperationException("无法确定当前 Windows 用户。");
        name = "Global\\CodexAssistant.WeChatReceiver.v1." + TextSafety.Hash(user) + "." + TextSafety.Hash(botId);
        // Mutex ownership is thread-affine. A dedicated thread acquires and releases
        // it so asynchronous receiver shutdown can safely resume on any thread.
        new Thread(Hold) { IsBackground = true, Name = "WeChat receiver lease" }.Start();
    }

    internal static ReceiverLease? TryAcquire(string botId, CancellationToken cancellation = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(botId);
        cancellation.ThrowIfCancellationRequested();
        var attempt = new ReceiverLease(botId);
        var acquired = attempt.ready.Task.GetAwaiter().GetResult();
        if (cancellation.IsCancellationRequested)
        {
            acquired?.Dispose();
            cancellation.ThrowIfCancellationRequested();
        }
        return acquired;
    }

    private void Hold()
    {
        bool owns = false;
        Mutex? mutex = null;
        try
        {
            // Use Windows' default object ACL, and scope the global name to the
            // current SID. CurrentUserOnly's extra managed ACL compatibility
            // check rejects even same-process reopen under some desktop sandboxes.
            mutex = new Mutex(false, name);
            try { owns = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { owns = true; }
            if (!owns) { ready.TrySetResult(null); return; }
            ready.TrySetResult(this);
            release.Wait();
        }
        catch (Exception ex) { ready.TrySetException(ex); }
        finally
        {
            if (owns) mutex!.ReleaseMutex();
            mutex?.Dispose();
            release.Dispose();
            stopped.TrySetResult();
        }
    }

    public void Dispose()
    {
        RequestRelease();
        stopped.Task.GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        RequestRelease();
        await stopped.Task.ConfigureAwait(false);
    }

    private void RequestRelease()
    {
        if (Interlocked.Exchange(ref releaseRequested, 1) == 0) release.Set();
    }
}
