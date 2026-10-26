using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexDashboard;

namespace Codex.Control;

/// <summary>
/// Trusted host encryption boundary. Must authenticate ciphertext and purpose, fail closed, and
/// never fall back to plaintext. The host owns key custody; no built-in plaintext protector exists.
/// </summary>
public interface IOwnedProvenanceProtector
{
    string Scheme { get; }
    byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> purpose);
    byte[] Unprotect(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> purpose);
}

public sealed record ProtectedOwnedProvenance(long Sequence, byte[] Payload);

/// <summary>
/// Append returns only after a complete record is durable. An uncertain append must poison further
/// writes until reopen; read must expose recovery status and never silently discard damaged data.
/// These are immutable creation proofs, not operation, conversation, or retry records.
/// </summary>
public interface IOwnedProvenanceStorage : IAsyncDisposable
{
    bool IsReadOnly { get; }
    bool RequiresRecovery { get; }
    ValueTask<IReadOnlyList<ProtectedOwnedProvenance>> ReadAllAsync(CancellationToken cancellationToken = default);
    ValueTask AppendAsync(long expectedSequence, ReadOnlyMemory<byte> protectedRecord, CancellationToken cancellationToken = default);
}

public sealed class ProvenanceException(string code) : IOException(code)
{
    public string Code { get; } = code;
}

public sealed record OwnedCreationEvidence(Guid CreateOperationId, DateTimeOffset CreatedAt,
    string ServerVersion, string ReceiptDigest, Guid ConnectionEpoch);

/// <summary>
/// Authenticated historical creation facts only: not a handle, current observation, or grant of
/// control. Reading this catalogue does not require initialization or a JSON-RPC connection.
/// </summary>
public sealed record OwnedCreationFact(CanonicalTaskIdentity Task, string ProjectId, string Cwd,
    string Model, string ApprovalPolicy, string Sandbox, OwnedCreationEvidence Creation);

internal sealed record OwnedProvenanceEntry(int Schema, long Sequence, string ProtectionScheme,
    ConnectionIdentity Connection, string ConfigurationDigest, CanonicalTaskIdentity Task,
    string ProjectId, string Cwd, string Model, string ApprovalPolicy, string Sandbox,
    OwnedCreationEvidence Creation);

/// <summary>
/// Only the control client's verified thread/start path can append. Restoration never accepts a
/// caller-supplied record or owner declaration. The host must keep this storage out of model tools.
/// </summary>
public sealed class OwnedProvenanceStore : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        MaxDepth = 16, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false
    };
    private readonly OwnedServerConfiguration configuration;
    private readonly IOwnedProvenanceStorage storage;
    private readonly IOwnedProvenanceProtector protector;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly List<OwnedProvenanceEntry> entries = [];
    private bool loaded, faulted, disposed;
    private int attached;
    internal bool Faulted => faulted || storage.RequiresRecovery;
    internal bool IsReadOnly => storage.IsReadOnly;

    public OwnedProvenanceStore(OwnedServerConfiguration configuration, IOwnedProvenanceStorage storage,
        IOwnedProvenanceProtector protector)
    {
        OwnedVerification.Validate(configuration);
        if (string.IsNullOrWhiteSpace(protector.Scheme) || protector.Scheme.Length > 128)
            throw new ArgumentException("An authenticated protection scheme is required.", nameof(protector));
        this.configuration = configuration; this.storage = storage; this.protector = protector;
    }

    internal bool Matches(OwnedServerConfiguration candidate) => candidate.Connection == configuration.Connection
        && OwnedVerification.ConfigurationDigest(candidate) == OwnedVerification.ConfigurationDigest(configuration);
    internal void Attach()
    {
        if (Interlocked.Exchange(ref attached, 1) != 0) throw new ProvenanceException("provenance_store_already_attached");
    }

    internal async Task EnsureCanCreateAsync(Guid operationId, DateTimeOffset now, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await LoadCoreAsync(now, ct).ConfigureAwait(false);
            RequireWritable();
            if (entries.Any(x => x.Creation.CreateOperationId == operationId))
                throw new ProvenanceException("create_operation_already_recorded_restore_read_only");
            if (entries.Count >= 1024) throw new ProvenanceException("provenance_capacity");
        }
        finally { gate.Release(); }
    }

    internal async Task RecordCreatedAsync(CanonicalTaskIdentity identity, CreateOwnedTask request,
        OwnedCreationEvidence evidence, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await LoadCoreAsync(evidence.CreatedAt, ct).ConfigureAwait(false);
            RequireWritable();
            var entry = new OwnedProvenanceEntry(1, entries.Count + 1L, protector.Scheme,
                configuration.Connection, OwnedVerification.ConfigurationDigest(configuration), identity,
                request.ProjectId, request.Cwd, request.Model, request.ApprovalPolicy, request.Sandbox, evidence);
            Validate(entry, entry.Sequence, evidence.CreatedAt);
            if (entries.Any(x => x.Task == identity || x.Creation.CreateOperationId == evidence.CreateOperationId))
                throw new ProvenanceException("creation_provenance_identity_reused");
            if (entries.Count >= 1024) throw new ProvenanceException("provenance_capacity");
            byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(entry, Json);
            byte[] encrypted;
            try { encrypted = protector.Protect(plaintext, Purpose(entry.Sequence)); }
            catch { faulted = true; throw new ProvenanceException("provenance_protection_failed"); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
            if (encrypted.Length is <= 0 or > 128 * 1024)
            { faulted = true; throw new ProvenanceException("invalid_protected_provenance"); }
            try
            {
                await storage.AppendAsync(entries.Count, encrypted, ct).ConfigureAwait(false);
                entries.Add(entry); // Never expose an in-memory proof before durable append acknowledgement.
            }
            catch
            {
                faulted = true;
                throw new ProvenanceException("provenance_commit_unconfirmed");
            }
        }
        finally { gate.Release(); }
    }

    internal async Task<IReadOnlyList<OwnedProvenanceEntry>> ReadVerifiedAsync(DateTimeOffset now, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { await LoadCoreAsync(now, ct).ConfigureAwait(false); return entries.ToArray(); }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<OwnedCreationFact>> ReadCreationFactsAsync(DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var verified = await ReadVerifiedAsync(now, cancellationToken).ConfigureAwait(false);
        return verified.Select(entry => new OwnedCreationFact(entry.Task, entry.ProjectId, entry.Cwd,
            entry.Model, entry.ApprovalPolicy, entry.Sandbox, entry.Creation)).ToArray();
    }

    private async Task LoadCoreAsync(DateTimeOffset now, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Faulted) throw new ProvenanceException("provenance_recovery_required");
        if (loaded) return;
        var candidate = new List<OwnedProvenanceEntry>();
        try
        {
            var records = await storage.ReadAllAsync(ct).ConfigureAwait(false);
            if (storage.RequiresRecovery || records.Count > 1024) throw new ProvenanceException("provenance_recovery_required");
            long expected = 1;
            var identities = new HashSet<CanonicalTaskIdentity>();
            var operations = new HashSet<Guid>();
            foreach (var record in records)
            {
                ct.ThrowIfCancellationRequested();
                if (record.Sequence != expected || record.Payload.Length is <= 0 or > 128 * 1024)
                    throw new ProvenanceException("provenance_sequence_or_size_invalid");
                byte[] plaintext;
                try { plaintext = protector.Unprotect(record.Payload, Purpose(expected)); }
                catch { throw new ProvenanceException("provenance_authentication_failed"); }
                try
                {
                    if (plaintext.Length is <= 0 or > 64 * 1024) throw new ProvenanceException("provenance_plaintext_size_invalid");
                    var entry = JsonSerializer.Deserialize<OwnedProvenanceEntry>(plaintext, Json)
                        ?? throw new ProvenanceException("provenance_invalid_record");
                    Validate(entry, expected, now);
                    if (!identities.Add(entry.Task) || !operations.Add(entry.Creation.CreateOperationId))
                        throw new ProvenanceException("provenance_duplicate_identity");
                    candidate.Add(entry);
                }
                finally { CryptographicOperations.ZeroMemory(plaintext); }
                expected++;
            }
            entries.AddRange(candidate); loaded = true; // All-or-nothing.
        }
        catch (OperationCanceledException) { throw; }
        catch (ProvenanceException) { faulted = true; throw; }
        catch { faulted = true; throw new ProvenanceException("provenance_invalid_or_unreadable"); }
    }

    private void Validate(OwnedProvenanceEntry entry, long sequence, DateTimeOffset now)
    {
        if (entry.Schema != 1 || entry.Sequence != sequence || entry.ProtectionScheme != protector.Scheme
            || entry.Connection != configuration.Connection || entry.ConfigurationDigest != OwnedVerification.ConfigurationDigest(configuration)
            || entry.Task?.Endpoint != configuration.Connection.Endpoint
            || !configuration.Projects.TryGetValue(entry.ProjectId, out string? cwd) || cwd != entry.Cwd
            || !configuration.Models.Contains(entry.Model)
            || entry.ApprovalPolicy is not ("untrusted" or "on-request") || entry.Sandbox is not ("read-only" or "workspace-write")
            || entry.Creation == null || entry.Creation.CreateOperationId == Guid.Empty || entry.Creation.ConnectionEpoch == Guid.Empty
            || entry.Creation.ServerVersion != configuration.ExpectedVersion || entry.Creation.CreatedAt == default
            || entry.Creation.CreatedAt > now.AddMinutes(1) || !Digest(entry.Creation.ReceiptDigest))
            throw new ProvenanceException("provenance_scope_or_creation_evidence_invalid");
    }
    private static bool Digest(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    internal byte[] Purpose(long sequence) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        purpose = "Codex.Control.OwnedCreation/v1", sequence,
        endpoint = configuration.Connection.Endpoint, backend = configuration.Connection.Backend,
        synthetic = configuration.Connection.Synthetic, digest = OwnedVerification.ConfigurationDigest(configuration)
    }));
    private void RequireWritable()
    {
        if (Faulted || storage.IsReadOnly) throw new ProvenanceException("provenance_storage_not_writable");
    }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { if (disposed) return; disposed = true; await storage.DisposeAsync().ConfigureAwait(false); }
        finally { gate.Release(); }
    }
}
