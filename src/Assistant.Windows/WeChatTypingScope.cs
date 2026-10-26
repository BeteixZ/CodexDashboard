using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;

// Optional, transient presentation. No ticket persistence or message retries.
internal sealed class WeChatTypingScope : IAsyncDisposable
{
    private readonly CancellationTokenSource stop;
    private readonly Task completion;
    internal WeChatTypingScope(WeChatApi api, ClawBinding binding, SemaphoreSlim gate, CancellationToken generation,
        CancellationToken session)
    {
        stop = CancellationTokenSource.CreateLinkedTokenSource(generation, session);
        completion = RunAsync(api, binding, gate, stop.Token);
    }
    private static async Task RunAsync(WeChatApi api, ClawBinding binding, SemaphoreSlim gate, CancellationToken ct)
    {
        bool acquired = false, startAttempted = false;
        string ticket = "";
        try
        {
            await gate.WaitAsync(ct).ConfigureAwait(false); acquired = true;
            ticket = await api.GetTypingTicketAsync(binding, ct).ConfigureAwait(false);
            if (ticket.Length == 0) return;
            ct.ThrowIfCancellationRequested();
            startAttempted = true;
            await api.SendTypingAsync(binding, ticket, true, ct).ConfigureAwait(false);
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (OptionalFailure(error)) { }
        finally
        {
            if (startAttempted)
            {
                try { await api.SendTypingAsync(binding, ticket, false, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception error) when (OptionalFailure(error)) { }
            }
            if (acquired) gate.Release();
            api.Dispose();
        }
    }
    private static bool OptionalFailure(Exception error) => error is OperationCanceledException or HttpRequestException
        or WeChatApiException or JsonException;
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        try { await completion.ConfigureAwait(false); }
        finally { stop.Dispose(); }
    }
}

internal sealed class TypingToolModel(IToolModel model, Func<CancellationToken, IAsyncDisposable?> start) : IToolModel
{
    public async Task<ToolModelResponse> GenerateAsync(ToolModelRequest request, CancellationToken ct)
    {
        await using var scope = start(ct);
        return await model.GenerateAsync(request, ct);
    }
}
