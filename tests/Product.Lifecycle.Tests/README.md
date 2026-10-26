# Independent distribution isolated checks

This suite supports **independent distribution and installed-package isolated checks**, not acceptance
of the two production application lifecycles. Both real Windows distribution
executables enter an early test branch before production settings, credentials,
singletons, UI, channels, models, or task transports are constructed.

The test branch uses the production `MonitorService` with an explicitly injected
in-memory collector, production notification-transition logic, and the real
`ProductStatusBridge` / current-user named-pipe protocol. It checks these parts
inside each independently copied distribution without the other product.

## Running

Build both distributions from reviewed source after the early hook is integrated.
Do not point this runner at an installed or user-data directory. The runner copies
only clean build output and rejects data selectors, credential files, settings,
queue/memory state, redirected paths, or the other product's executable.

```powershell
dotnet build tests/Product.Lifecycle.Tests/Product.Lifecycle.Tests.csproj -c Release -m:1 -nr:false
dotnet tests/Product.Lifecycle.Tests/bin/Release/net10.0/Product.Lifecycle.Tests.dll --self-test --output C:\absolute\new\safety-output
dotnet tests/Product.Lifecycle.Tests/bin/Release/net10.0/Product.Lifecycle.Tests.dll --dashboard-package C:\absolute\dashboard-publish --assistant-package C:\absolute\assistant-publish --output C:\absolute\new\run-output
```

Append `--preflight-only` to inspect packages without starting either executable.
Builds in a constrained shell may need `TEMP`/`TMP` redirected to an existing
workspace-owned directory. If the default NuGet metadata cache is unwritable,
set `NUGET_HTTP_CACHE_PATH` and `NUGET_SCRATCH` to fresh writable directories too;
keep vulnerability auditing enabled. When referenced products have already been built, use
`-p:BuildProjectReferences=false` after their referenced libraries already exist.

## Exact hook contract

Both entry points call `ProductLifecycleTest.IsRequested(args)` immediately after
`base.OnStartup`, before data-directory selection or any normal startup work.
When true, await `RunAsync(args, ProtocolProduct.Dashboard/Assistant)`, pass its
exit code to `Shutdown`, and return. The Assistant project links the root source.

Only these four ordered arguments are accepted:

```text
--isolated-lifecycle-test <evidence> --data-dir <isolateddata>
```

The runner creates each synthetic manifest, fixture, and evidence directory.
All channel/model/control/real-collector flags must explicitly be false; remote
hosts must be empty. No normal product configuration is read or saved.
The synthetic collector's local-enabled flag selects an in-memory fixture only.

The singleton is `Local\CodexLifecycleTest.<product>.<data-directory-SHA256-prefix>`;
the protocol uses a separate `lifecycle-<data-directory-SHA256-prefix>` profile.
There is no production mutex, port, or default status endpoint. A second process
using the same test data exits with code 10. Each child checks its own
`stop.request` and has a 90-second maximum lifetime.

The startup preflight inspects PE metadata and the startup state machine's IL call
order to reject packages missing the early hook. It also checks assembly
references and rejects Dashboard packages containing assistant service types.
This is supporting static evidence, not a formal control-flow/security proof.

## Six disruption scenarios

For each product as the disrupted process, the runner verifies:

1. A stop-file request produces a clean exit while the other process continues.
2. Ending only the exact child `Process` created by this runner leaves the other
   process collecting and serving fresh protocol status.
3. After stopping a child, moving its copied package into another directory
   leaves the other process collecting and serving fresh protocol status.

Both products start hidden. The runner never looks up arbitrary process names or
kills existing applications. It moves only checked paths below the newly created
run directory. The default six scenarios retain copied files and perform no deletion.
It observes multiple new heartbeat sequences, valid synthetic snapshots, and
advancing protocol timestamps after each disruption. Cleanup requests graceful
stop first and can terminate only a child it started.

### Installed-package uninstall mode

Append `--installer-only` instead of `--preflight-only` to run two installed-package scenarios, one for each product. The runner invokes the selected package's `Install-Product.ps1` into fresh directories beneath its new output root, then uses the existing early synthetic lifecycle hook in the installed executables. After stopping one child, it invokes that installation's `Uninstall-Product.ps1` and verifies its program directory is removed, every isolated data file is unchanged, and the other child continues collecting and serving advancing protocol status. Only the stopped test installation is deleted. PowerShell children are hidden, bounded, and belong to this runner.

This mode exercises the actual installer and uninstaller with the real distribution executables, but still bypasses production accounts, channels and normal UI startup. It does not claim actual-user installation acceptance or legacy receiver handoff.

Moving a stopped copied package in the default mode is **not** an installer uninstall; `--installer-only` is the separate deletion scenario.
This suite does **not** test real WeChat receivers, model calls, remote tasks,
production singleton handoff, tray/UI shutdown, migration of existing accounts,
old executable exclusion, or two real pollers. P1-11 remains incomplete until
those separately authorized acceptance checks are performed.

## Evidence and fixture measurements

`preflight.json` records inspected assembly hashes, dependencies, and early-hook
call order. Per-product evidence includes startup configuration, heartbeats,
snapshot validity, samples, duplicate-instance rejection, and graceful stops.
Each scenario saves its own result; `results.json` summarizes the complete run.

A 120-task in-memory fixture refreshes every 250 ms. Measurements compare a
survivor's CPU, memory, and collection timings during two-fixture and survivor-only
phases. They include test evidence serialization and protocol overhead; they are
neither production-load benchmarks nor a measured real SSH baseline. P0-04's
production baseline remains pending.

## Package files and launcher parameters

`Test-Packaging.ps1` runs a separate read-only package inspection and synthetic
parameter suite. It extracts reviewed function definitions and the native
Windows command-line parser from the launcher source. It never dot-sources
the launcher body, enumerates product processes, invokes Explorer, launches a
product, or builds a Windows project. Negative tests copy only a validated
distribution into a fresh evidence directory; all fixtures are retained.

```powershell
.\tests\Product.Lifecycle.Tests\Test-Packaging.ps1 -ProductRoot C:\absolute\products -OutputDirectory C:\absolute\new\packaging-output
```

Run on PowerShell 7 or Windows PowerShell 5.1. `Build-Products.ps1` itself
requires PowerShell 7, the .NET 10 SDK, and normal package restore access.
Its two framework-dependent outputs require .NET 10 Desktop and ASP.NET Core
runtimes, Python 3.10+ for local collection, and the configured OpenSSH/remote
Python/Codex environment for remote tasks. These tests validate declarations
and packaged dependencies; they do not prove those prerequisites are installed.

The 44 checks cover both launchers' Windows quoting, absolute data paths,
selectors, default launch compatibility, exact profile matching, unavailable or
ambiguous command lines, and legacy derived protocol profiles. They also verify
the existing packages' dependency files, collector and remote proxy copies,
manifest hashes and inventory, and rejection of missing dependencies, wrong
launcher defaults, missing frameworks, a foreign product, manifest traversal,
duplicate canonical paths, or changed content.

Launchers accept `-DataDirectory C:\absolute\profile` and
`-ProtocolProfile pair-name`. Both default to protocol profile `default`, so
separate product data directories can communicate using the same named profile.
An executable-local `data-directory.txt`, when present, is validated and passed
explicitly. Without either selector or explicit directory, no `--data-dir` is
added: the application retains its default selection behavior, and the
launcher reports that its actual default directory is unresolved.

An existing process is accepted only for the same executable, Windows session,
and provable command-line identity. A default process is not inferred equivalent
to an explicit data directory. Unreadable or unsupported command lines fail
confirmation. Dashboard instances with explicit data directories use profile-scoped singleton identity; the launcher must still verify the executable and effective data/profile arguments before accepting an existing process. Existing Explorer-parent and tool-job separation
checks remain in place, but this parameter suite does not exercise them.

Historical package-parameter checks passed 44 cases on both PowerShell 7 and Windows PowerShell 5.1. Their local evidence directories are not distributed with the source. These results inspected an earlier framework-dependent distribution and do not establish current self-contained installer acceptance. Run the suite against the newly built package and record its actual source hashes and results; production lifecycle and normal desktop installation require separate acceptance.
