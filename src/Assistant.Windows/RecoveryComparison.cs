using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Assistant.Core;

namespace CodexDashboard;

internal sealed record RecoveryRecord(string Kind, string Key, string Digest);
internal sealed record RecoveryDifference(string Kind, int MissingFromCandidate, int Changed, int CandidateOnly);

// Compare committed records, not ordinal positions or counts. Reports expose digests only.
internal static class RecoveryComparison
{
    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    internal static RecoveryRecord Record(string kind, object owner, object id, object value)
        => new(kind, Hash(new { kind, owner, id }), Hash(value));

    internal static IEnumerable<RecoveryRecord> Records(ConversationIdentity identity, ConversationSnapshot state)
    {
        yield return Record("Conversation", identity, "snapshot", state);
        foreach (var item in state.Inbox) yield return Record("Inbox", identity, item.SourceMessageId, item);
        foreach (var item in state.Events) yield return Record("Event", identity, item.Id, item);
        foreach (var item in state.Operations) yield return Record("Operation", identity, item.Id, item);
        foreach (var item in state.Outbox) yield return Record("Outbox", identity, item.Id, item);
        foreach (var item in state.WorkItems) yield return Record("WorkItem", identity, item.Id, item);
        foreach (var item in state.Schedules) yield return Record("Schedule", identity, item.WorkItemId, item);
        foreach (var item in state.CommitmentRuns) yield return Record("CommitmentRun", identity, item.Id, item);
        if (state.DependentWork is { } dependent) yield return Record("DependentWork", identity, "snapshot", dependent);
    }

    internal static JsonElement Compare(JsonElement current, JsonElement candidate)
    {
        var latest = Read(current); var copy = Read(candidate);
        var differences = latest.Values.Select(x => x.Kind).Concat(copy.Values.Select(x => x.Kind)).Distinct().Order()
            .Select(kind => new RecoveryDifference(kind,
                latest.Values.Count(x => x.Kind == kind && !copy.ContainsKey(x.Key)),
                latest.Values.Count(x => x.Kind == kind && copy.TryGetValue(x.Key, out var other) && x.Digest != other.Digest),
                copy.Values.Count(x => x.Kind == kind && !latest.ContainsKey(x.Key)))).ToArray();
        return JsonSerializer.SerializeToElement(new { version = 1, restoreAuthorized = false,
            currentFingerprint = current.GetProperty("snapshotFingerprint"), candidateFingerprint = candidate.GetProperty("snapshotFingerprint"),
            recordsIdentical = differences.All(x => x.MissingFromCandidate == 0 && x.Changed == 0 && x.CandidateOnly == 0),
            differences, nextStep = "Preserve current data. Reconcile differences and external effects before any activation; this comparison never grants startup or replay." });
    }

    private static Dictionary<string, RecoveryRecord> Read(JsonElement report)
    {
        if (report.GetProperty("version").GetInt32() != 6 || report.GetProperty("restoreAuthorized").GetBoolean())
            throw new InvalidOperationException("Current offline inspection reports required.");
        return report.GetProperty("reconciliationRecords").Deserialize<RecoveryRecord[]>()!.ToDictionary(x => x.Key);
    }
}
