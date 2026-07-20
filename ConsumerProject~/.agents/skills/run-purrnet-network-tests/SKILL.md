---
name: run-purrnet-network-tests
description: Inspect, author, build, run, and interpret exact three-role PurrNet network tests in a Unity project using com.amilverton.purrnet-network-tests.
---

# Run PurrNet network tests

Use the installed package's manifests, builder, and PowerShell tools as the source of truth. A pass
requires exact contract-valid atomic JSON from `Server`, `OwnerClient`, and `ObserverClient`, followed
by three natural process exits with code zero. A successful build, matching self-reports, or one client
result is not a pass.

## Establish the package and envelope

1. Find the Unity project root containing `Packages/manifest.json`.
2. Require direct dependencies on both `com.amilverton.purrnet-network-tests` and
   `dev.purrnet.purrnet`. For portable v1, PurrNet must resolve from
   `https://github.com/PurrNet/PurrNet.git?path=/Assets/PurrNet#v1.19.1`.
3. Resolve the harness from an embedded package or
   `Library/PackageCache/com.amilverton.purrnet-network-tests@*/`.
4. Require Windows, Unity `6000.4.10f1`, Windows Build Support, StandaloneWindows64, Mono,
   PurrNet `1.19.1`, UDP loopback, PowerShell 7, and exactly one dedicated server, owner client, and
   observer client. Treat the builder as authoritative even if an Editor prerequisite status is only
   advisory.
5. Read `Documentation~/project-integration.md`, `Documentation~/protocol.md`, and the project's
   optional `ProjectSettings/PurrNetNetworkTests.json` before changing or running a project scenario.

Open `Tools > PurrNet Network Tests > Package Control` when a human wants the Editor surface. Refresh
its status list before using its guarded manifest, skill, or interactive-suite actions.

## Install for a clean-context agent

Before handing the repository to a clean-context agent, run:

```powershell
& (Join-Path $packageRoot 'Tools~\Install-PurrNetNetworkTestSkill.ps1') `
    -ProjectPath $projectRoot
```

The installer creates `.agents/skills/run-purrnet-network-tests` and a
`.purrnet-network-tests-skill.json` ownership record. For an installer-produced copy, require its
`packageVersion` to match the resolved package. Never overwrite a missing-ownership or hash-mismatched
skill. Use `-StageIncoming` to create a versioned sibling for explicit review, then leave the active
copy untouched. Start the clean agent in the project repository after installation and invoke
`$run-purrnet-network-tests`.

## Inspect a project scenario

Keep the scenario in a runtime-compatible asmdef referencing:

- `Amilverton.PurrNetTesting.Runtime`;
- `PurrNet.Runtime`;
- the game feature assemblies it exercises.

Reject Editor-only or Unity Test Framework-only Player scenarios. Require one concrete attributed
type and one stable non-`Harness.` ID. Every discovered project scenario needs a manifest entry.

Validate manifest schema `1` fail-closed. Each scenario needs `id`, `enabled`, exactly one `typeName`
or `prefabPath`, and an exact contract even when disabled. A `typeName` must exactly identify the
attributed type as `Namespace.Type, Assembly` or its full assembly-qualified name; the builder creates
its temporary fixture prefab. A `prefabPath` must point under `Assets/` to a prefab with exactly one
root `NetworkTestScenario`, none below it, and a matching attribute ID.

Require contract schema `1`, a nonnegative revision, Boolean/String/Int32 shared facts, and exactly
`Server`, `OwnerClient`, and `ObserverClient`. Each role owns exact evidence and ordered nonempty unique
ready milestones, assertions, and final milestones; ready milestones are an exact prefix of final
milestones. Suites contain ordered, unique enabled scenario IDs. Run project suite membership by
passing its IDs through `-Scenarios`; portable v1 has no `-Suite` switch.

If `bootstrapPrefabPath` is present, require the prefab to be saved inactive and use exactly one
supported root form. A serialized root has exactly one `NetworkManager`, exactly one
`UDPTransport` and no other transport, the manager referencing that transport, serialized non-null
`NetworkRules`, and at most one `NetworkTestBootstrapHook`. A hook-provisioned root has no
serialized manager or transport and exactly one project-owned hook configured through
`ConfigureUdpNetworkRootForBuild(NetworkPrefabs, NetworkRules)`; the harness creates the sealed
PurrNet components before activation. Reject mixed forms, Addressables/async providers, and nested
composites. For the Git-package workaround, define project-owned sealed subclasses of
`NetworkPrefabs` and `NetworkRules`, create assets of those subclasses, disable catalog
`autoGenerate`, populate stable project prefab entries, call `Refresh()`, and serialize those
assets through the project-owned hook. The package's Project Integration Bootstrap sample contains
the exact pattern. The runtime validates provider entries, composes the project provider before the
scenario provider, preserves project IDs, clears auto-start flags, and invokes the optional
synchronous hook before network start and after full disconnect. Hooks execute for built-in scenarios too:
validate shared project setup for every run, but gate scenario-specific shared facts, milestones,
evidence, and assertions on `bootstrap.ScenarioId` so project hooks do not change built-in exact
contracts.

## Author the scenario

Exercise real game code. Send client intent through the real PurrNet RPC, derive authority from
`RPCInfo.sender`, validate before mutation, and expose state through replication. Do not call a server
implementation directly to simulate an RPC.

Publish readiness only after role-specific network and fixture conditions are true. Record compact
shared facts with `Session.SetFact`, asymmetric observations with `Session.SetEvidence`, unique
role-owned assertions, and named milestones. Finish through `Session.Pass(revision)` or
`Session.Fail(message)`. A requested pass remains provisional until PurrNet stops, both states reach
`Disconnected`, the post-stop hook succeeds, the result is atomically published, and the process exits
naturally. `Session.Pass` first emits a completion artifact; the coordinator must validate all three
role identities and revisions before it emits their role-bound stop signals.

After compilation, run once without Player reuse. Add a negative control that would expose a bypassed
RPC, missing sender check, unauthorized mutation, wrong fact, or stale prefab, and confirm failure at
the intended boundary.

## Run with package tools

Use the default staging build. It copies the project without volatile caches/artifacts and vendors
safe top-level local `file:` packages into the staged project. It refuses nested local dependency
chains, reparse points, unsafe roots, ambiguous JSON, and content drift. Use `-BuildInPlace` only when
the exact project is closed and in-place compilation is explicitly acceptable. Use `-KeepStaging`
only for diagnosis.

Run one scenario:

```powershell
& (Join-Path $packageRoot 'Tools~\Invoke-PurrNetNetworkTests.ps1') `
    -ProjectPath $projectRoot `
    -Scenario 'Game.InventoryTransfer'
```

Use `-OpenViewer` for the optional three-role Evidence/Raw Log window. Use `-ReusePlayer` only when the
coordinator accepts the source/dependency/project-settings fingerprint, schema-2 build receipt, and
SHA-256-bound execution manifest.

Run `Tools~/Invoke-PurrNetNetworkTestSuite.ps1 -Scenarios <ids>` for a matrix and require every
repetition to pass. Use `Invoke-PurrNetNetworkTestSuiteInteractive.ps1` for one viewer that follows a
watched matrix. Use `Show-PurrNetNetworkTestLogs.ps1` with `-RunPath`, or with
`-ArtifactsPath -FollowNewestRun`, to inspect retained runs.

Parse the single-run command's final JSON and report `runId`, `scenarioId`, `stateRevision`,
`sharedFacts`, `playerPath`, and `artifactsPath`. Read role-local evidence from the retained
`*.result.json` files; it is not duplicated in the final coordinator summary.

## Diagnose and refuse safely

On failure, inspect the newest run's `build.log`, Player build receipt, execution manifest,
`*.ready.json`, `*.result.json`, and three role logs before changing code. Preserve the run directory.

Treat unsupported versions/backend/transport/topology, malformed or stale JSON, missing exact
contracts, ambiguous sources, unsupported bootstrap/providers, timeout, early/nonzero exit, wrong
role/revision/fact/evidence/assertion/milestone order, duplicate process IDs, cross-role disagreement,
shutdown/hook failure, or stale Player inputs as failure. Do not replace readiness or disconnect
polling with fixed sleeps. Stop only coordinator-owned child processes and do not remove retained
artifacts while diagnosing.
