using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CodexDashboard;

[JsonConverter(typeof(JsonStringEnumConverter<HostMappingState>))]
public enum HostMappingState { Resolved, Unconfirmed, ProfileRequired, Retired, Conflict }
[JsonConverter(typeof(JsonStringEnumConverter<HostAvailability>))]
public enum HostAvailability { Unknown, Online, Offline }
[JsonConverter(typeof(JsonStringEnumConverter<AliasMappingState>))]
public enum AliasMappingState { Pending, Resolved, Rejected }

/// <summary>Exact Codex identity. Display names, SSH aliases without the prefix, and directory guesses are rejected.</summary>
public sealed record HostEndpointIdentity
{
    public string HostId { get; }
    public string? ProfileId { get; }
    [JsonIgnore] public string Key => "codex-host/v1/" + Uri.EscapeDataString(HostId) + "/" + Uri.EscapeDataString(ProfileId ?? "");
    [JsonConstructor]
    public HostEndpointIdentity(string hostId, string? profileId = null)
    {
        HostRegistryRules.RequireHostId(hostId);
        HostRegistryRules.RequireProfileId(profileId);
        HostId = hostId;
        ProfileId = profileId;
    }
}

/// <summary>
/// New gateways use this key. TaskRecord.Key remains the legacy display-name-based persistence key.
/// URI escaping keeps delimiters inside thread IDs distinct from structural separators.
/// </summary>
public sealed record CanonicalTaskIdentity
{
    public string HostId { get; }
    public string ThreadId { get; }
    public string? ProfileId { get; }
    [JsonIgnore] public HostEndpointIdentity Endpoint => new(HostId, ProfileId);
    [JsonIgnore] public string Key => "codex-task/v1/" + Uri.EscapeDataString(HostId) + "/"
        + Uri.EscapeDataString(ProfileId ?? "") + "/" + Uri.EscapeDataString(ThreadId);
    [JsonConstructor]
    public CanonicalTaskIdentity(string hostId, string threadId, string? profileId = null)
    {
        HostRegistryRules.RequireHostId(hostId);
        HostRegistryRules.RequireProfileId(profileId);
        HostRegistryRules.RequireText(threadId, "thread_id", 512);
        HostId = hostId;
        ThreadId = threadId;
        ProfileId = profileId;
    }
}

public sealed record HostRegistration
{
    public string HostId { get; init; } = "";
    public string? ProfileId { get; init; }
    public string DisplayName { get; init; } = "";
    public string ConnectionReference { get; init; } = "";
    /// <summary>Explicit configuration directory/reference, never discovered or normalized by this data-only library.</summary>
    public string? CodexDirectory { get; init; }
    public bool Configured { get; init; }
    public bool Enabled { get; init; }
    public HostMappingState MappingState { get; init; } = HostMappingState.Unconfirmed;
    public HostAvailability Availability { get; init; } = HostAvailability.Unknown;
    public DateTimeOffset? ObservedAt { get; init; }
    public string? ConfirmationUserMessageId { get; init; }
    public DateTimeOffset? ConfirmedAt { get; init; }
    [JsonIgnore] public HostEndpointIdentity Identity => new(HostId, ProfileId);
}

public sealed record HostAliasMapping
{
    public string Id { get; init; } = "";
    public string FromHostId { get; init; } = "";
    public string ToHostId { get; init; } = "";
    public AliasMappingState State { get; init; } = AliasMappingState.Pending;
    public DateTimeOffset ProposedAt { get; init; }
    public DateTimeOffset? ConfirmedAt { get; init; }
    public string? ConfirmationUserMessageId { get; init; }
}

public sealed record HostRegistryDocument
{
    public int Version { get; init; } = 1;
    public long Revision { get; init; }
    public List<HostRegistration> Hosts { get; init; } = [];
    public List<HostAliasMapping> AliasMappings { get; init; } = [];
}

/// <summary>Caller must obtain this from a current explicit user action, never from task output or memory.</summary>
public sealed record ExplicitHostConfirmation
{
    public string UserMessageId { get; }
    public DateTimeOffset At { get; }
    public ExplicitHostConfirmation(string userMessageId, DateTimeOffset at)
    {
        HostRegistryRules.RequireText(userMessageId, "confirmation_user_message_id", 256);
        if (at == default) throw new ArgumentException("confirmation_time_required");
        UserMessageId = userMessageId;
        At = at;
    }
}

public sealed record HostProfileConfiguration(string ProfileId, string CodexDirectory, string? DisplayName = null);
public sealed record HostReconciliation(IReadOnlyList<string> Added, IReadOnlyList<string> Disabled, IReadOnlyList<string> Conflicts);
public sealed record HistoricalHostResolution(CanonicalTaskIdentity Original, CanonicalTaskIdentity? Suggested,
    bool Resolved, string Code, IReadOnlyList<string> MappingIds);
public sealed record HostControlCheck(CanonicalTaskIdentity Original, bool CanAddress, string Code)
{
    /// <summary>Registry success only proves addressability; the gateway must still verify live thread/turn and permissions.</summary>
    public bool RequiresLiveBackendVerification => true;
}

/// <summary>
/// Each product owns and serializes its own Export() in its own settings. No filesystem, network,
/// credentials, approval records or operations are touched here.
/// </summary>
public sealed class HostRegistry
{
    private HostRegistryDocument document;
    public HostRegistry(HostRegistryDocument? source = null)
    {
        document = Clone(source ?? new());
        Validate(document);
    }

    public HostRegistryDocument Export() => Clone(document);
    public IReadOnlyList<HostRegistration> Registrations => document.Hosts.ToArray();
    public IReadOnlyList<HostAliasMapping> AliasMappings => document.AliasMappings.ToArray();

    /// <summary>
    /// Imports current configured aliases without guessing a rename. Missing aliases are retained,
    /// marked not configured and disabled. Reappearing aliases are not silently re-enabled.
    /// Direct configuration identities are resolved, but their availability starts unknown.
    /// </summary>
    public HostReconciliation ReconcileConfiguration(bool localEnabled, IEnumerable<string> remoteAliases,
        string? localCodexDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(remoteAliases);
        var aliases = remoteAliases.ToArray();
        if (aliases.Any(a => !HostIdentity.ValidAlias(a))
            || aliases.Distinct(StringComparer.OrdinalIgnoreCase).Count() != aliases.Length)
            throw new ArgumentException("invalid_or_ambiguous_remote_aliases");
        if (localCodexDirectory != null) HostRegistryRules.RequireText(localCodexDirectory, "codex_directory", 4096);
        var configured = aliases.Select(a => HostIdentity.RemotePrefix + a).ToHashSet(StringComparer.Ordinal);
        if (localEnabled) configured.Add(HostIdentity.LocalHostId);
        var next = Clone(document);
        var added = new List<string>();
        var disabled = new List<string>();
        var conflicts = new List<string>();
        // A case-only alias change is a different Codex hostId. Require an explicit rename instead of merging.
        foreach (var hostId in configured)
            if (next.Hosts.Any(h => h.HostId.Equals(hostId, StringComparison.OrdinalIgnoreCase) && h.HostId != hostId
                && !HasRecordedRenameLink(next.AliasMappings, h.HostId, hostId)))
                throw new ArgumentException("case_only_alias_change_requires_explicit_rename");
        foreach (var hostId in configured)
        {
            if (next.Hosts.Any(h => h.HostId == hostId)) continue;
            var registration = NewRegistration(hostId) with
            {
                Configured = true, Enabled = true, MappingState = HostMappingState.Resolved,
                CodexDirectory = hostId == HostIdentity.LocalHostId ? localCodexDirectory : null
            };
            next.Hosts.Add(registration);
            added.Add(registration.Identity.Key);
        }
        for (int i = 0; i < next.Hosts.Count; i++)
        {
            var host = next.Hosts[i];
            bool present = configured.Contains(host.HostId);
            bool historicDefault = host.MappingState == HostMappingState.ProfileRequired;
            if (!present || historicDefault || host.MappingState == HostMappingState.Retired)
            {
                if (host.Configured || host.Enabled) disabled.Add(host.Identity.Key);
                next.Hosts[i] = host with
                {
                    Configured = false, Enabled = false, Availability = HostAvailability.Unknown, ObservedAt = null
                };
                continue;
            }
            if (host.HostId == HostIdentity.LocalHostId && host.ProfileId == null && localCodexDirectory != null
                && host.CodexDirectory != null && host.CodexDirectory != localCodexDirectory)
            {
                // Changing which .codex directory an existing identity means is not a harmless label edit.
                next.Hosts[i] = host with
                {
                    Configured = true, Enabled = false, MappingState = HostMappingState.Conflict,
                    Availability = HostAvailability.Unknown, ObservedAt = null
                };
                conflicts.Add(host.Identity.Key);
                continue;
            }
            next.Hosts[i] = host with
            {
                Configured = true,
                CodexDirectory = host.CodexDirectory ?? (host.HostId == HostIdentity.LocalHostId && host.ProfileId == null ? localCodexDirectory : null)
            };
        }
        Commit(next);
        return new(added, disabled, conflicts);
    }

    public void SetDisplayName(HostEndpointIdentity identity, string displayName)
    {
        HostRegistryRules.RequireText(displayName, "display_name", 256);
        Replace(identity, h => h with { DisplayName = displayName });
    }

    public void SetEnabled(HostEndpointIdentity identity, bool enabled)
        => Replace(identity, h =>
        {
            if (enabled && (!h.Configured || h.MappingState != HostMappingState.Resolved || HasPendingMapping(h.HostId)))
                throw new InvalidOperationException("host_identity_not_ready");
            return h with { Enabled = enabled, Availability = HostAvailability.Unknown, ObservedAt = null };
        });

    /// <summary>Pure observation import. It does not connect. Stale, future, unknown and offline observations cannot address controls.</summary>
    public void Observe(HostEndpointIdentity identity, HostAvailability availability, DateTimeOffset observedAt)
    {
        if (!Enum.IsDefined(availability) || observedAt == default) throw new ArgumentException("invalid_observation");
        Replace(identity, h =>
        {
            if (h.ObservedAt != null && observedAt < h.ObservedAt) return h;
            return h with { Availability = availability, ObservedAt = observedAt };
        });
    }

    /// <summary>
    /// First conversion requires at least two explicit profiles. The old profile-less identity is
    /// retained as ProfileRequired, so old approvals/operations cannot accidentally choose a profile.
    /// Removed profiles remain historical and disabled. Once assigned, profile IDs are never collapsed.
    /// </summary>
    public void ConfigureProfiles(string hostId, IReadOnlyList<HostProfileConfiguration> profiles,
        ExplicitHostConfirmation confirmation)
    {
        HostRegistryRules.RequireHostId(hostId);
        ArgumentNullException.ThrowIfNull(confirmation);
        ArgumentNullException.ThrowIfNull(profiles);
        var originals = document.Hosts.Where(h => h.HostId == hostId).ToArray();
        if (originals.Length == 0) throw new InvalidOperationException("unknown_host");
        if (HasPendingMapping(hostId) || document.AliasMappings.Any(m => m.FromHostId == hostId && m.State == AliasMappingState.Resolved))
            throw new InvalidOperationException("rename_must_be_resolved_first");
        bool previouslyProfiled = originals.Any(h => h.ProfileId != null);
        if (profiles.Count == 0 || (!previouslyProfiled && profiles.Count < 2))
            throw new ArgumentException("profile_ids_require_multiple_directories");
        foreach (var profile in profiles)
        {
            if (profile == null) throw new ArgumentException("invalid_profile");
            HostRegistryRules.RequireProfileId(profile.ProfileId);
            if (profile.ProfileId == null) throw new ArgumentException("profile_id_required");
            HostRegistryRules.RequireText(profile.CodexDirectory, "codex_directory", 4096);
            if (profile.DisplayName != null) HostRegistryRules.RequireText(profile.DisplayName, "display_name", 256);
        }
        if (profiles.Select(p => p.ProfileId).Distinct(StringComparer.Ordinal).Count() != profiles.Count
            || profiles.Select(p => p.CodexDirectory).Distinct(StringComparer.Ordinal).Count() != profiles.Count)
            throw new ArgumentException("duplicate_profile_or_directory");
        foreach (var profile in profiles)
        {
            var existing = originals.SingleOrDefault(h => h.ProfileId == profile.ProfileId);
            if (existing?.CodexDirectory != null && existing.CodexDirectory != profile.CodexDirectory)
                throw new InvalidOperationException("profile_id_cannot_be_retargeted");
        }
        var next = Clone(document);
        bool configured = originals.Any(h => h.Configured);
        var selected = profiles.Select(p => p.ProfileId).ToHashSet(StringComparer.Ordinal);
        for (int i = 0; i < next.Hosts.Count; i++)
        {
            var old = next.Hosts[i];
            if (old.HostId != hostId) continue;
            if (old.ProfileId == null)
                next.Hosts[i] = old with
                {
                    MappingState = HostMappingState.ProfileRequired, Configured = false,
                    Enabled = false, Availability = HostAvailability.Unknown, ObservedAt = null
                };
            else if (!selected.Contains(old.ProfileId))
                next.Hosts[i] = old with
                {
                    Configured = false, Enabled = false, Availability = HostAvailability.Unknown, ObservedAt = null
                };
        }
        foreach (var profile in profiles)
        {
            int index = next.Hosts.FindIndex(h => h.HostId == hostId && h.ProfileId == profile.ProfileId);
            var old = index >= 0 ? next.Hosts[index] : NewRegistration(hostId) with { ProfileId = profile.ProfileId };
            var replacement = old with
            {
                CodexDirectory = profile.CodexDirectory,
                DisplayName = profile.DisplayName ?? (index >= 0 ? old.DisplayName : HostIdentity.DisplayName(hostId) + " / " + profile.ProfileId),
                Configured = configured, Enabled = configured, MappingState = HostMappingState.Resolved,
                Availability = HostAvailability.Unknown, ObservedAt = null,
                ConfirmationUserMessageId = confirmation.UserMessageId, ConfirmedAt = confirmation.At
            };
            if (index >= 0) next.Hosts[index] = replacement; else next.Hosts.Add(replacement);
        }
        Commit(next);
    }

    public string ProposeAliasRename(string fromHostId, string toHostId, DateTimeOffset proposedAt)
    {
        HostRegistryRules.RequireRemoteHostId(fromHostId);
        HostRegistryRules.RequireRemoteHostId(toHostId);
        if (fromHostId == toHostId || proposedAt == default) throw new ArgumentException("invalid_alias_rename");
        if (!document.Hosts.Any(h => h.HostId == fromHostId)) throw new InvalidOperationException("unknown_source_host");
        if (HasPendingMapping(fromHostId)
            || !document.Hosts.Any(h => h.HostId == fromHostId && h.MappingState == HostMappingState.Resolved))
            throw new InvalidOperationException("source_identity_not_resolved");
        if (document.AliasMappings.Any(m => m.FromHostId == fromHostId && m.State is AliasMappingState.Pending or AliasMappingState.Resolved))
            throw new InvalidOperationException("source_already_has_mapping");
        if (WouldCycle(document.AliasMappings, fromHostId, toHostId)) throw new InvalidOperationException("alias_cycle");
        if (document.Hosts.Any(h => h.HostId == toHostId))
            throw new InvalidOperationException("target_identity_already_exists");
        if (document.Hosts.Any(h => h.HostId != fromHostId && h.HostId.Equals(toHostId, StringComparison.OrdinalIgnoreCase)
            && !HasRecordedRenameLink(document.AliasMappings, h.HostId, fromHostId)))
            throw new InvalidOperationException("target_alias_case_conflict");
        var next = Clone(document);
        string id = Guid.NewGuid().ToString("N");
        next.AliasMappings.Add(new()
        {
            Id = id, FromHostId = fromHostId, ToHostId = toHostId,
            ProposedAt = proposedAt, State = AliasMappingState.Pending
        });
        foreach (var source in document.Hosts.Where(h => h.HostId == fromHostId))
            next.Hosts.Add(source with
            {
                HostId = toHostId, DisplayName = HostIdentity.DisplayName(toHostId) + (source.ProfileId == null ? "" : " / " + source.ProfileId),
                ConnectionReference = HostRegistryRules.ConnectionReference(toHostId),
                Configured = false, Enabled = false,
                MappingState = source.ProfileId == null && source.MappingState == HostMappingState.ProfileRequired
                    ? HostMappingState.ProfileRequired : HostMappingState.Unconfirmed,
                Availability = HostAvailability.Unknown, ObservedAt = null,
                ConfirmationUserMessageId = null, ConfirmedAt = null
            });
        Commit(next);
        return id;
    }

    public void ConfirmAliasRename(string mappingId, ExplicitHostConfirmation confirmation)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        var next = Clone(document);
        int index = next.AliasMappings.FindIndex(m => m.Id == mappingId);
        if (index < 0 || next.AliasMappings[index].State != AliasMappingState.Pending)
            throw new InvalidOperationException("pending_mapping_required");
        var mapping = next.AliasMappings[index];
        if (confirmation.At < mapping.ProposedAt) throw new ArgumentException("confirmation_predates_proposal");
        next.AliasMappings[index] = mapping with
        {
            State = AliasMappingState.Resolved, ConfirmedAt = confirmation.At,
            ConfirmationUserMessageId = confirmation.UserMessageId
        };
        for (int i = 0; i < next.Hosts.Count; i++)
        {
            var host = next.Hosts[i];
            if (host.HostId == mapping.FromHostId)
                next.Hosts[i] = host with
                {
                    MappingState = HostMappingState.Retired, Configured = false, Enabled = false,
                    Availability = HostAvailability.Unknown, ObservedAt = null
                };
            else if (host.HostId == mapping.ToHostId && host.MappingState != HostMappingState.ProfileRequired)
                next.Hosts[i] = host with
                {
                    MappingState = HostMappingState.Resolved, Enabled = host.Configured,
                    Availability = HostAvailability.Unknown, ObservedAt = null,
                    ConfirmationUserMessageId = confirmation.UserMessageId, ConfirmedAt = confirmation.At
                };
        }
        Commit(next);
    }

    public void RejectAliasRename(string mappingId)
    {
        var next = Clone(document);
        int index = next.AliasMappings.FindIndex(m => m.Id == mappingId);
        if (index < 0 || next.AliasMappings[index].State != AliasMappingState.Pending)
            throw new InvalidOperationException("pending_mapping_required");
        var mapping = next.AliasMappings[index];
        next.AliasMappings[index] = mapping with { State = AliasMappingState.Rejected };
        for (int i = 0; i < next.Hosts.Count; i++)
            if (next.Hosts[i].HostId == mapping.ToHostId)
                next.Hosts[i] = next.Hosts[i] with { MappingState = HostMappingState.Retired, Enabled = false, Configured = false };
        Commit(next);
    }

    /// <summary>History-only suggestion. Never pass this result into an old approval or operation automatically.</summary>
    public HistoricalHostResolution ResolveForHistory(CanonicalTaskIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!document.Hosts.Any(h => h.HostId == identity.HostId && h.ProfileId == identity.ProfileId))
            return new(identity, null, false, "unknown_host_or_profile", []);
        string current = identity.HostId;
        var used = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (seen.Add(current))
        {
            var mapping = document.AliasMappings.SingleOrDefault(m => m.FromHostId == current && m.State != AliasMappingState.Rejected);
            if (mapping == null)
            {
                var target = document.Hosts.SingleOrDefault(h => h.HostId == current && h.ProfileId == identity.ProfileId);
                if (target == null) return new(identity, null, false, "unknown_host_or_profile", used);
                if (target.MappingState != HostMappingState.Resolved)
                    return new(identity, null, false, "identity_not_resolved", used);
                return new(identity, new(current, identity.ThreadId, identity.ProfileId), true, used.Count == 0 ? "exact" : "confirmed_history_mapping", used);
            }
            if (mapping.State != AliasMappingState.Resolved) return new(identity, null, false, "mapping_unconfirmed", used);
            used.Add(mapping.Id);
            current = mapping.ToHostId;
        }
        return new(identity, null, false, "alias_cycle", used);
    }

    /// <summary>Checks the supplied identity only. Even a confirmed rename NEVER redirects a control.</summary>
    public HostControlCheck CheckControlTarget(CanonicalTaskIdentity identity, DateTimeOffset now,
        TimeSpan? maxObservationAge = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (now == default) throw new ArgumentException("current_time_required");
        var maxAge = maxObservationAge ?? TimeSpan.FromMinutes(2);
        if (maxAge <= TimeSpan.Zero || maxAge > TimeSpan.FromMinutes(10)) throw new ArgumentOutOfRangeException(nameof(maxObservationAge));
        if (document.AliasMappings.Any(m => m.FromHostId == identity.HostId && m.State == AliasMappingState.Resolved))
            return new(identity, false, "renamed_identity_requires_new_explicit_target");
        if (HasPendingMapping(identity.HostId)) return new(identity, false, "mapping_unconfirmed");
        var host = document.Hosts.SingleOrDefault(h => h.HostId == identity.HostId && h.ProfileId == identity.ProfileId);
        if (host == null) return new(identity, false, "unknown_host_or_profile");
        if (host.MappingState != HostMappingState.Resolved) return new(identity, false, "identity_not_resolved");
        if (!host.Configured) return new(identity, false, "host_not_configured");
        if (!host.Enabled) return new(identity, false, "host_disabled");
        if (host.Availability != HostAvailability.Online) return new(identity, false, host.Availability == HostAvailability.Offline ? "host_offline" : "host_observation_unknown");
        if (host.ObservedAt == null || now - host.ObservedAt > maxAge || host.ObservedAt - now > TimeSpan.FromSeconds(30))
            return new(identity, false, "host_observation_stale_or_invalid");
        return new(identity, true, "addressable_live_backend_verification_required");
    }

    private bool HasPendingMapping(string hostId)
        => document.AliasMappings.Any(m => m.State == AliasMappingState.Pending && (m.FromHostId == hostId || m.ToHostId == hostId));
    private void Replace(HostEndpointIdentity identity, Func<HostRegistration, HostRegistration> update)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var next = Clone(document);
        int index = next.Hosts.FindIndex(h => h.HostId == identity.HostId && h.ProfileId == identity.ProfileId);
        if (index < 0) throw new InvalidOperationException("unknown_host_or_profile");
        next.Hosts[index] = update(next.Hosts[index]);
        Commit(next);
    }
    private static HostRegistration NewRegistration(string hostId) => new()
    {
        HostId = hostId, DisplayName = HostIdentity.DisplayName(hostId),
        ConnectionReference = HostRegistryRules.ConnectionReference(hostId)
    };
    private void Commit(HostRegistryDocument next)
    {
        next = next with { Revision = checked(document.Revision + 1) };
        Validate(next);
        document = next;
    }
    private static HostRegistryDocument Clone(HostRegistryDocument source) => source with
    {
        Hosts = source.Hosts?.ToList() ?? throw new ArgumentException("missing_hosts"),
        AliasMappings = source.AliasMappings?.ToList() ?? throw new ArgumentException("missing_alias_mappings")
    };

    public static void Validate(HostRegistryDocument candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Version != 1 || candidate.Revision < 0) throw new ArgumentException("unsupported_host_registry_version");
        if (candidate.Hosts == null || candidate.AliasMappings == null || candidate.Hosts.Count > 2048 || candidate.AliasMappings.Count > 4096)
            throw new ArgumentException("invalid_host_registry_size");
        if (candidate.AliasMappings.Any(m => m == null)) throw new ArgumentException("null_alias_mapping");
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var host in candidate.Hosts)
        {
            if (host == null) throw new ArgumentException("null_host");
            HostRegistryRules.RequireHostId(host.HostId);
            HostRegistryRules.RequireProfileId(host.ProfileId);
            HostRegistryRules.RequireText(host.DisplayName, "display_name", 256);
            if (host.ConnectionReference != HostRegistryRules.ConnectionReference(host.HostId))
                throw new ArgumentException("connection_reference_identity_mismatch");
            if (host.CodexDirectory != null) HostRegistryRules.RequireText(host.CodexDirectory, "codex_directory", 4096);
            if (!Enum.IsDefined(host.MappingState) || !Enum.IsDefined(host.Availability) || !identities.Add(host.Identity.Key))
                throw new ArgumentException("invalid_or_duplicate_host_identity");
            if (host.ProfileId != null && host.CodexDirectory == null) throw new ArgumentException("profile_directory_required");
            if ((host.ConfirmationUserMessageId == null) != (host.ConfirmedAt == null))
                throw new ArgumentException("incomplete_host_confirmation");
            if (host.ConfirmationUserMessageId != null)
            {
                HostRegistryRules.RequireText(host.ConfirmationUserMessageId, "confirmation_user_message_id", 256);
                if (host.ConfirmedAt == default(DateTimeOffset)) throw new ArgumentException("confirmation_time_required");
            }
        }
        foreach (var group in candidate.Hosts.GroupBy(h => h.HostId, StringComparer.Ordinal))
        {
            var profiles = group.Where(h => h.ProfileId != null).ToArray();
            if (profiles.Length > 0)
            {
                if (profiles.Length < 2) throw new ArgumentException("single_profile_has_no_multiple_directory_history");
                if (group.Any(h => h.ProfileId == null && h.MappingState is not (HostMappingState.ProfileRequired or HostMappingState.Retired)))
                    throw new ArgumentException("profileless_identity_is_ambiguous");
                if (profiles.Select(h => h.CodexDirectory).Distinct(StringComparer.Ordinal).Count() != profiles.Length)
                    throw new ArgumentException("duplicate_profile_directory");
            }
        }
        var mappingIds = new HashSet<string>(StringComparer.Ordinal);
        var outgoing = new HashSet<string>(StringComparer.Ordinal);
        var incoming = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mapping in candidate.AliasMappings)
        {
            if (mapping == null || !Guid.TryParseExact(mapping.Id, "N", out _) || !mappingIds.Add(mapping.Id)
                || !Enum.IsDefined(mapping.State) || mapping.ProposedAt == default)
                throw new ArgumentException("invalid_alias_mapping");
            HostRegistryRules.RequireRemoteHostId(mapping.FromHostId);
            HostRegistryRules.RequireRemoteHostId(mapping.ToHostId);
            if (mapping.FromHostId == mapping.ToHostId
                || !candidate.Hosts.Any(h => h.HostId == mapping.FromHostId)
                || !candidate.Hosts.Any(h => h.HostId == mapping.ToHostId))
                throw new ArgumentException("missing_alias_mapping_endpoint");
            if (mapping.State == AliasMappingState.Resolved)
            {
                HostRegistryRules.RequireText(mapping.ConfirmationUserMessageId, "mapping_confirmation_required", 256);
                if (mapping.ConfirmedAt == null || mapping.ConfirmedAt < mapping.ProposedAt)
                    throw new ArgumentException("mapping_confirmation_required");
            }
            else if (mapping.ConfirmationUserMessageId != null || mapping.ConfirmedAt != null)
                throw new ArgumentException("unresolved_mapping_has_confirmation");
            if (mapping.State == AliasMappingState.Rejected) continue;
            if (!outgoing.Add(mapping.FromHostId) || !incoming.Add(mapping.ToHostId))
                throw new ArgumentException("conflicting_alias_mappings");
            if (WouldCycle(candidate.AliasMappings.Where(m => m.Id != mapping.Id), mapping.FromHostId, mapping.ToHostId))
                throw new ArgumentException("alias_cycle");
        }
        foreach (var group in candidate.Hosts.Select(h => h.HostId).Distinct(StringComparer.Ordinal)
            .GroupBy(h => h, StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First();
            if (group.Any(h => !HasRecordedRenameLink(candidate.AliasMappings, first, h)))
                throw new ArgumentException("case_only_alias_change_requires_explicit_rename");
        }
    }

    // This includes rejected historical proposals only to preserve their distinct identities.
    // It does not resolve a target and is never consulted by control addressability checks.
    private static bool HasRecordedRenameLink(IEnumerable<HostAliasMapping> mappings, string first, string second)
    {
        var edges = mappings.ToArray();
        var pending = new Stack<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        pending.Push(first);
        while (pending.TryPop(out string? current))
        {
            if (current == second) return true;
            if (!seen.Add(current)) continue;
            foreach (var edge in edges)
            {
                if (edge.FromHostId == current) pending.Push(edge.ToHostId);
                if (edge.ToHostId == current) pending.Push(edge.FromHostId);
            }
        }
        return false;
    }

    private static bool WouldCycle(IEnumerable<HostAliasMapping> mappings, string from, string to)
    {
        var edges = mappings.Where(m => m.State != AliasMappingState.Rejected).ToArray();
        var pending = new Stack<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        pending.Push(to);
        while (pending.TryPop(out string? current))
        {
            if (current == from) return true;
            if (!seen.Add(current)) continue;
            foreach (var mapping in edges.Where(m => m.FromHostId == current)) pending.Push(mapping.ToHostId);
        }
        return false;
    }
}

internal static class HostRegistryRules
{
    internal static void RequireHostId(string? hostId)
    {
        if (hostId == HostIdentity.LocalHostId) return;
        if (hostId == null || !hostId.StartsWith(HostIdentity.RemotePrefix, StringComparison.Ordinal)
            || !HostIdentity.ValidAlias(hostId[HostIdentity.RemotePrefix.Length..]))
            throw new ArgumentException("invalid_codex_host_id");
    }
    internal static void RequireRemoteHostId(string? hostId)
    {
        RequireHostId(hostId);
        if (hostId == HostIdentity.LocalHostId) throw new ArgumentException("local_identity_cannot_be_alias_renamed");
    }
    internal static void RequireProfileId(string? profileId)
    {
        if (profileId != null && !Regex.IsMatch(profileId, @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,99}\z"))
            throw new ArgumentException("invalid_profile_id");
    }
    internal static void RequireText(string? value, string name, int max)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Any(char.IsControl)
            || value.Any(char.IsSurrogate) && !WellFormedUnicode(value))
            throw new ArgumentException(name);
    }
    private static bool WellFormedUnicode(string value)
    {
        for (int i = 0; i < value.Length; i++)
            if (char.IsSurrogate(value[i]))
            {
                if (!char.IsHighSurrogate(value[i]) || i + 1 == value.Length || !char.IsLowSurrogate(value[++i])) return false;
            }
        return true;
    }
    internal static string ConnectionReference(string hostId)
        => hostId == HostIdentity.LocalHostId ? "local" : "ssh:" + hostId[HostIdentity.RemotePrefix.Length..];
}
