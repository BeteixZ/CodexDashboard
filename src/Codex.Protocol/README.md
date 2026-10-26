# Codex local status protocol v1

This `net10.0` library contains no WPF, channel, model, storage or Codex execution dependencies. Its Windows transport exposes four read-only methods: `hello`, `health`, `capabilities`, and `assistant.status`. Task data, reports, events, control operations and approval responses are deliberately absent.

## Host integration

```csharp
var identity = new ProtocolPeerIdentity
{
    Product = ProtocolProduct.Assistant,
    ProductVersion = "1.0.0",
    InstanceId = Guid.NewGuid() // one value for this process lifetime
};
var pipeName = LocalProtocolEndpoint.ForCurrentUser(ProtocolProduct.Assistant);
var server = new LocalProtocolServer(pipeName, identity, cachedStatusProvider);
Task serving = server.RunAsync(applicationStoppingToken);
```

Implement `IProtocolStatusProvider` using cached state. Both methods must return promptly and honor cancellation. They must not contact a model, send a message, load credentials, or initiate a Codex operation. `health` describes readiness, not the success of business work. `assistant.status` reports only connection state, model availability, queue and attention counts, and a short safe reason.

Keep a reference to `serving`, observe startup failures, and cancel/await it when the application exits. `RunAsync` binds its pipe instances before its first asynchronous wait. A second server cannot claim an already owned endpoint. A server object can be run only once. Dashboard uses the same server with `Product = Dashboard`; its `assistant.status` capability is unavailable.

```csharp
await using var client = await LocalProtocolClient.ConnectAsync(
    pipeName, dashboardIdentity, ProtocolProduct.Assistant,
    cancellationToken: cancellationToken);
HealthStatus health = await client.GetHealthAsync(cancellationToken);
AssistantStatus status = await client.GetAssistantStatusAsync(cancellationToken);
```

The client verifies the expected product and negotiated version before returning. Calls on one client are serialized. After an error, timeout, cancellation during I/O, or the server's per-connection request limit, create a new client explicitly. The library does not reconnect or retry automatically. These are status reads, but keeping retries at the host boundary makes offline and degraded states visible.

Hosts sharing an installation profile must use the same `profile` argument to `ForCurrentUser`. Different profiles get separate endpoint names.

## Security and data boundaries

- Both pipe handles use `PipeOptions.CurrentUserOnly`. The Windows token is the identity boundary; the endpoint name contains a hash of the user SID and profile, not the SID.
- The client fixes the server name to `"."` and uses `TokenImpersonationLevel.Identification`. The API does not accept a remote machine address.
- `product`, product version and instance GUID are validated declarations. They are not executable signature checks and cannot distinguish a genuine product from another program running as the same Windows user. Do not authorize controls using them.
- DTOs contain no credentials, chat bodies, task contents, memory or user identifiers. Short reason strings must be prepared by the host for display; the protocol cannot determine whether arbitrary text contains a secret.
- Exceptions from status providers become a generic `internal_error`; raw exception text does not cross the pipe.
- OS access checks were exercised for same-user operation. Denial for a second Windows account needs a separate account test; this suite does not create accounts or alter ACLs.

## Wire contract

A frame is a four-byte signed little-endian byte length followed by one UTF-8 JSON object. Length must be positive and within the negotiated bound. Zero, negative, oversized, truncated and invalid JSON frames close the connection. There is no line delimiter, notification stream or request multiplexing.

Requests:

```json
{
  "id": "bounded-correlation-id",
  "method": "hello",
  "params": {
    "identity": {
      "product": "dashboard",
      "productVersion": "1.0.0",
      "instanceId": "72f3e861-0d3b-447e-9cff-c7443cad7dde"
    },
    "supportedVersions": {
      "major": 1,
      "minimumMinor": 0,
      "maximumMinor": 0
    },
    "maximumFrameBytes": 65536
  }
}
```

Responses contain the matching `id` and exactly one of `result` or `error`. Errors have `code` and a short `message`. All other v1 requests omit `params` or supply `{}`. Unknown envelope and DTO properties are ignored for future optional fields. Required fields, duplicate properties and unsupported enum values are rejected. Unknown methods return `method_not_found`; they never fall through to a command handler.

`hello` must be first and cannot be repeated on the same connection. Major versions must match. Minor ranges must overlap; the largest common minor is selected. The negotiated frame size is the smaller of the two advertised limits. Missing fields do not imply readiness, authorization, completion or delivery.

Default limits:

| Limit | Default | Configurable bound |
| --- | --- | --- |
| Frame | 64 KiB | 1 KiB–1 MiB |
| Concurrent connections | 4 | 1–32 |
| Requests after hello per connection | 256 | 1–10,000 |
| Connect / hello / status timeout | 3 seconds each | Positive, up to 5 minutes |
| Idle read timeout | 30 seconds | Positive, up to 5 minutes |
| JSON depth | 16 | Fixed |
| Correlation ID / method / product version | 64 characters each | Fixed |
| Status reason | 256 characters | Fixed |

Fixed pipe workers bound connection handling. Each connection has one active request. Slow status providers are cancelled at the request deadline. If a provider ignores cancellation, its slot stays occupied until it actually finishes; subsequent requests fail with `busy` instead of creating an unlimited backlog. Providers must still avoid synchronous blocking before returning their `ValueTask`.

## Verification

Run the standalone console suite; no test framework or external services are required:

```powershell
dotnet run --project tests/Codex.Protocol.Tests/Codex.Protocol.Tests.csproj
```

The suite uses unique test pipe names, fake cached status, and one hidden child process owned by the test. It covers all methods, version negotiation, unknown/missing/duplicate fields, malformed framing, output bounds, timeouts, cancellation, concurrency, provider backpressure, shutdown, endpoint ownership and real separate-process exchange. It does not read production configuration, contact WeChat, call a model, or operate Codex tasks.
