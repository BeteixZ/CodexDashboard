using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Codex.Migration;

namespace CodexDashboard;

// Recover into a fresh directory from the latest reviewed state. Never overwrite
// post-activation records with an older backup or clear their pending statuses.
internal static class ReviewedRecoveryHandoff
{
    internal static async Task<MigrationResult> ExecuteAsync(MigrationRequest request, string reviewedFingerprint,
        ILegacyReceiverGuard? receiverGuard = null, CancellationToken cancellation = default)
    {
        var result = await new MigrationEngine(new ReviewedGuard(receiverGuard ?? new DenyMigrationGuard(), reviewedFingerprint))
            .MigrateAsync(request with { PauseTargetServices = true }, cancellation);
        return result with { ActivationAllowed = false, Code = result.Succeeded ? "reviewed_data_prepared_services_paused" : result.Code };
    }

    private sealed class ReviewedGuard(ILegacyReceiverGuard receiverGuard, string expected) : ILegacyReceiverGuard
    {
        public async ValueTask<IReceiverExclusionLease?> TryAcquireAsync(MigrationRequest request,
            MigrationDirection direction, CancellationToken cancellation)
        {
            var lease = await receiverGuard.TryAcquireAsync(request, direction, cancellation);
            if (lease == null) return null;
            try
            {
                if (!lease.LegacyStoppedAndWritesBlocked || !lease.AssistantStoppedAndWritesBlocked
                    || !lease.CredentialScopeCompatible || !await lease.RevalidateAsync(cancellation))
                    throw new InvalidOperationException("Receiver exclusion is required.");
                var report = await AssistantLedgerInspection.ReadAsync(request.SourceDirectory, requireHold: true);
                if (report.GetProperty("snapshotFingerprint").GetProperty("Sha256").GetString() != expected)
                    throw new InvalidOperationException("Reviewed snapshot changed.");
                // Pending and unknown records are copied unchanged. This publishes data
                // with every channel/model/control switch off, never a replay grant.
                return lease;
            }
            catch { await lease.DisposeAsync(); throw; }
        }
    }
}
