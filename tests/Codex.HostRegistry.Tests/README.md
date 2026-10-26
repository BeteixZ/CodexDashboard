# Host registry isolated checks

This .NET 10 console suite uses in-memory documents and synthetic identities only.
It opens no SSH connection, credential store, production settings or task session.
It writes a pass/fail JSON report into a new explicitly supplied output directory.

```powershell
dotnet run --project tests/Codex.HostRegistry.Tests/Codex.HostRegistry.Tests.csproj -c Release -- --output C:\absolute\new\test-output
```

## Product integration

Each product persists its own `HostRegistryDocument` in its own settings.
Use the shared `CodexDashboard` namespace:

```csharp
var registry = new HostRegistry(settings.HostRegistry);
var reconciliation = registry.ReconcileConfiguration(
    settings.LocalEnabled, settings.RemoteHosts, settings.CodexHome);
settings.HostRegistry = registry.Export();

var target = new CanonicalTaskIdentity(hostId, threadId, profileId);
registry.Observe(target.Endpoint, HostAvailability.Online, observedAt);
var addressing = registry.CheckControlTarget(target, DateTimeOffset.UtcNow);
```

`CanAddress` is only an identity and freshness check. The gateway must still verify
the live backend, task/turn identity, permissions, and operation-specific approval.
The registry never uses a display name, another online host, or an alias history
suggestion to replace a control target. It does not mutate old approvals or operations.
Existing `TaskRecord.Key` remains a legacy storage key; new control paths use
`CanonicalTaskIdentity.Key`, including optional profile identity.

Reconciliation imports configured aliases and retains removed hosts as disabled
historical records. Reintroduced hosts require an explicit `SetEnabled` call.
Do not discard the returned document or silently rebuild a malformed registry:
surface validation failures and retain the original settings for repair.

An alias rename starts with `ProposeAliasRename(oldHostId, newHostId, now)`.
Both endpoints remain unavailable for controls while the proposal is pending.
After explicit user confirmation, call `ConfirmAliasRename(mappingId,
new ExplicitHostConfirmation(currentUserMessageId, now))`. Import the new
configuration before confirming to enable the new endpoint, or explicitly enable
it after subsequent reconciliation. Confirmed and rejected records are retained.
`ResolveForHistory` provides a display/history suggestion only; it is not a
control-resolution API.

`ConfigureProfiles` requires explicit confirmation and at least two directory
references on first use. Directory references are opaque configuration values:
the library neither discovers directories nor proves that two spellings identify
different physical directories. The product/backend must validate their actual
binding before enabling execution. A profile ID cannot later be repointed.
The old profile-less identity remains ambiguous and disabled, and removed profiles
remain in history. Do not infer a profile for an old approval or operation.

The suite covers distinct host/profile task keys; display-name independence;
disabled, unknown, offline and stale targets; non-destructive configuration imports;
profile conversion; explicit alias confirmation; rename chains, case changes,
cycles and conflicts; JSON validation; and preservation of old control identities.
