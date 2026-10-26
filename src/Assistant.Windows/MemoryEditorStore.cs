using System;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexDashboard;

// An in-memory editing precondition, never a new authority or durable schema.
// Include the actual rules bytes: AGENTS.md may also be edited outside this app.
internal sealed record AssistantMemoryEditorSnapshot(string Profile, string Stamp, string Rules,
    ImmutableArray<AssistantMemoryEntry> Entries);

internal sealed class AssistantMemoryEditorConflictException()
    : InvalidOperationException("内容已在别处变化，尚未保存。请先重新读取并核对；当前编辑内容仍保留。");

internal sealed partial class AssistantMemoryStore
{
    internal AssistantMemoryEditorSnapshot CaptureEditorSnapshot()
    {
        lock (gate)
        {
            var document = ReadDocument(); string rules = ReadRulesCore(document.Profile);
            if (document.Profile != Profile) throw new AssistantMemoryEditorConflictException();
            return new(document.Profile, EditorStamp(document, rules), rules, document.Entries.ToImmutableArray());
        }
    }

    internal bool IsEditorSnapshotCurrent(AssistantMemoryEditorSnapshot snapshot)
    {
        lock (gate)
        {
            if (snapshot.Profile != Profile) return false;
            var document = ReadDocument(); string rules = ReadRulesCore(document.Profile);
            return document.Profile == snapshot.Profile && document.Profile == Profile
                && EditorStamp(document, rules) == snapshot.Stamp;
        }
    }

    internal AssistantMemoryEntry AddFromEditor(AssistantMemoryEditorSnapshot expected, string text)
    {
        lock (gate) { RequireEditorSnapshot(expected); return AddFromEditor(text); }
    }

    internal AssistantMemoryEntry UpdateFromEditor(AssistantMemoryEditorSnapshot expected, string id, string text)
    {
        lock (gate)
        {
            RequireEditorSnapshot(expected);
            if (!expected.Entries.Any(entry => entry.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                throw new AssistantMemoryEditorConflictException();
            return UpdateFromEditor(id, text);
        }
    }

    internal void DeleteFromEditor(AssistantMemoryEditorSnapshot expected, string id)
    {
        lock (gate)
        {
            RequireEditorSnapshot(expected);
            if (!expected.Entries.Any(entry => entry.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                throw new AssistantMemoryEditorConflictException();
            Delete(id);
        }
    }

    internal void SaveRulesFromEditor(AssistantMemoryEditorSnapshot expected, string rules)
    {
        lock (gate) { RequireEditorSnapshot(expected); SaveRules(rules); }
    }

    private void RequireEditorSnapshot(AssistantMemoryEditorSnapshot expected)
    {
        if (!IsEditorSnapshotCurrent(expected)) throw new AssistantMemoryEditorConflictException();
    }

    private static string EditorStamp(AssistantMemoryDocument document, string rules)
    {
        // No current time or model-specific selection participates: drafts do not
        // expire merely because generation context has a shorter lease.
        string serialized = JsonSerializer.Serialize(new { document, rules }, Settings.Json);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serialized)));
    }
}
