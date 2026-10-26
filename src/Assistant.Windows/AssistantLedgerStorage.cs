using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;

internal static class AssistantLedgerStorage
{
    internal static async Task<DurableConversationLedger> OpenAsync(string directory, bool enableArchive,
        IOutputDependencyValidator? validator = null, CancellationToken ct = default, bool inspection = false)
    {
        string ledgerPath = Path.Combine(directory, "conversations.state"), archivePath = Path.Combine(directory, "history-archive");
        if (!File.Exists(ledgerPath) && Directory.Exists(archivePath))
            throw new LedgerException(LedgerErrors.CorruptStorage, "账本缺失，但历史归档仍存在。请保留完整数据，不会创建空白账本。");
        var storage = new FileJournalStorage(ledgerPath, readOnly: inspection);
        var protector = new WindowsStateProtector(); HistoryArchiveSet? archive = null;
        try
        {
            if (inspection && storage.Recovery.Required)
                throw new LedgerException(LedgerErrors.RecoveryRequired, "账本存在未完成或损坏数据，不能报告为完整可读。");
            bool required = false;
            if (await storage.ReadLatestAsync(ct) is { } snapshot)
            {
                byte[] plain = protector.Unprotect(snapshot.Payload,
                    Encoding.UTF8.GetBytes($"assistant-ledger/v1/{protector.Scheme}/{snapshot.Sequence}"));
                try
                {
                    using var json = JsonDocument.Parse(plain);
                    required = json.RootElement.GetProperty("schemaVersion").GetInt32() == 12;
                }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
            if (required || !inspection && enableArchive && !storage.Recovery.Required)
            {
                bool create = !Directory.Exists(archivePath);
                if (required && create)
                    throw new LedgerException(LedgerErrors.CorruptStorage, "这份账本需要原历史归档，不能用新目录替代。");
                if (create) Directory.CreateDirectory(archivePath);
                archive = await HistoryArchiveSet.OpenAsync(archivePath, protector, createNew: create, ct: ct, readOnly: inspection);
                if (inspection && archive.RequiresRecovery)
                    throw new LedgerException(LedgerErrors.RecoveryRequired, "历史归档存在未完成或损坏数据，不能报告为完整可读。");
            }
            if (inspection) return await DurableConversationLedger.OpenForInspectionAsync(storage, protector,
                options: new() { RequireOutputDependencies = true }, cancellationToken: ct, historyArchive: archive);
            return await DurableConversationLedger.OpenAsync(storage, protector,
                options: new() { RequireOutputDependencies = true }, cancellationToken: ct,
                dependencyValidator: validator, historyArchive: archive);
        }
        catch
        {
            try { await storage.DisposeAsync(); }
            finally { if (archive != null) await archive.DisposeAsync(); }
            throw;
        }
    }
}
