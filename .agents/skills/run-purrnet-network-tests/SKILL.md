---
name: run-purrnet-network-tests
description: Build or reuse a standalone Unity test Player, run a dedicated server plus owner and observer clients, and interpret atomic PurrNet result artifacts. Use when an agent needs to validate PurrNet RPC authority, sender checks, replication, multi-client state agreement, or a project scenario built on com.amilverton.purrnet-network-tests.
---

# Run PurrNet network tests

Use the installed package's coordinator as the source of truth. A pass requires contract-valid JSON
from `Server`, `OwnerClient`, and `ObserverClient`, followed by three natural process exits with code
zero; a successful build or one client report is not a pass.

## Workflow

1. Find the Unity project root containing `Packages/manifest.json`.
2. Verify that the manifest directly installs both `com.amilverton.purrnet-network-tests` and
   `dev.purrnet.purrnet`. If either is absent, report the prerequisite unless the user asked you to
   install dependencies.
3. Resolve `Tools~/Invoke-PurrNetNetworkTests.ps1` from either an embedded package path or
   `Library/PackageCache/com.amilverton.purrnet-network-tests@*/`.
4. Run one scenario with the staging build default, or use
   `Tools~/Invoke-PurrNetNetworkTestSuite.ps1` for the full built-in matrix. Use `-BuildInPlace` only when the exact project is
   closed and a local `file:` package dependency would break in staging. Use `-ReusePlayer` only when
   a successful build exists; the coordinator rejects reuse if Player inputs, dependency locks, or
   Unity version changed. Add `-OpenViewer` only
   when a human wants the optional three-pane evidence and raw-log window; automated runs do not
   need it. The default evidence tabs show assertions, role-local evidence, milestones, and shared facts.
   When a human wants to watch the complete matrix, use
   `Tools~/Invoke-PurrNetNetworkTestSuiteInteractive.ps1`; it opens one viewer on raw logs and follows
   each new run through the suite.
5. Parse the command's final JSON. Report the run ID, scenario, revision, shared facts, and artifact path.
6. On failure, inspect the newest run's existing `*.result.json`, `*.ready.json`, and role logs before
   changing code. Preserve those artifacts.

```powershell
$package = Get-ChildItem .\Library\PackageCache -Directory |
    Where-Object Name -Like 'com.amilverton.purrnet-network-tests@*' |
    Select-Object -First 1

& (Join-Path $package.FullName 'Tools~\Invoke-PurrNetNetworkTests.ps1') `
    -ProjectPath $PWD `
    -Scenario 'Harness.InventoryTransfer'
```

For broad validation, run `Invoke-PurrNetNetworkTestSuite.ps1` and require every requested repetition
to pass. Never accept a majority of repeated runs.

## Integrity rules

- Do not use PurrNet Local Transport as multi-process replication evidence.
- Do not start batch Unity against a project already open in the Editor; keep the staging default.
- Do not replace readiness with fixed sleeps. A ready file must come from the scenario-owned milestone.
- Do not bypass a gameplay RPC or call its server implementation directly.
- Treat a timeout, early/non-zero process exit, malformed JSON, failed role, stale Player fingerprint,
  mismatched revision/facts, or built-in contract mismatch as a failure.
- Stop only the child processes launched by the coordinator. Never remove retained run artifacts while
  diagnosing a failure.

## Authoring a scenario

Read the installed package's `Documentation~/protocol.md`. Add a concrete `NetworkTestScenario` with
one stable `[NetworkTestScenario("Feature.Scenario")]` ID. Keep setup code-driven, send client intent
through the normal PurrNet request, derive the actor from `RPCInfo.sender`, wait for replicated facts,
record at least one `Session.RecordAssertion`, use `Session.SetEvidence` for asymmetric local
observations, and finish through `Session.Pass(revision)` or `Session.Fail(message)`. Add or update a
coordinator-owned contract when the scenario ships as a built-in.
