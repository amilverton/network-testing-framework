# PurrNet Network Test Harness

Build one test-only Unity Player, launch it as a dedicated server plus two independent clients, and
receive compact machine-readable proof of PurrNet authority and replication. The normal Unity Editor
can remain open because the default build happens in an isolated staging copy.

The package includes eight real-network scenarios:

- `Harness.SustainedPacketStream` — five seconds of ordered interval traffic, per-packet delivery,
  cadence validation, and final replicated-value convergence;
- `Harness.RpcRouting` — exact TargetRpc and ObserversRpc inclusion/exclusion;
- `Harness.CrossPlayerDamage` — sender-derived damage, health replication, and replay rejection;
- `Harness.LateJoinState` — committed late-join snapshot followed by a live delta;
- `Harness.OwnerNetworkTransform` — owner movement, convergence, and observer spoof isolation;
- `Harness.OwnershipTransfer` — old-owner mutation, handoff, new-owner mutation, and revocation;
- `Harness.SyncListOrder` — exact add/add/set/remove callback order and final collection state;
- `Harness.InventoryTransfer` — accepted owner intent and rejected observer mutation.

This is a focused standalone integration harness, not a replacement for Unity Test Framework unit or
PlayMode tests.

## Requirements

- Windows and PowerShell 7
- Unity 6 with Windows build support
- Git available to Unity Package Manager
- PurrNet `1.19.1` or a deliberately tested compatible version

The repository's `TestProject~` pins Unity `6000.4.10f1` and PurrNet `v1.19.1`, which are the versions
used by the package's integration proof.

## Install

Add both Git dependencies to the consuming Unity project's `Packages/manifest.json`. PurrNet is listed
directly because Unity cannot resolve a Git package from a transitive semantic-version dependency by
itself.

```json
{
  "dependencies": {
    "com.amilverton.purrnet-network-tests": "https://github.com/amilverton/network-testing-framework.git",
    "dev.purrnet.purrnet": "https://github.com/PurrNet/PurrNet.git?path=/Assets/PurrNet#v1.19.1"
  }
}
```

## Run the built-in scenario

Resolve the coordinator from the package cache, then give it the consuming project. Omit
`-BuildInPlace` for the safe default staging build.

```powershell
$package = Get-ChildItem .\Library\PackageCache -Directory |
    Where-Object Name -Like 'com.amilverton.purrnet-network-tests@*' |
    Select-Object -First 1

& (Join-Path $package.FullName 'Tools~\Invoke-PurrNetNetworkTests.ps1') `
    -ProjectPath $PWD `
    -Scenario 'Harness.InventoryTransfer'
```

The command returns JSON only after all roles match the scenario contract and all three Player
processes exit naturally with code zero. Failed runs retain ready files, result files, and one log per
process under `Artifacts/NetworkTests/Runs/<run-id>`. Built-in contracts independently fix the exact
revision, facts, evidence, assertions, and milestone order expected from each role.

Add `-OpenViewer` to the coordinator for a live Windows view with side-by-side Server, OwnerClient,
and ObserverClient evidence. Each pane opens on readiness, revision, assertions, role-local evidence,
shared facts, and failure details; a `Raw Unity log` tab retains the original Player output. To inspect an existing
run directly:

```powershell
pwsh -File .\Tools~\Show-PurrNetNetworkTestLogs.ps1 `
    -RunPath .\Artifacts\NetworkTests\Runs\<run-id>
```

During local package development, run against the included project without staging because its local
`file:../..` dependency intentionally points outside the project folder:

```powershell
pwsh -File .\Tools~\Invoke-PurrNetNetworkTests.ps1 `
    -ProjectPath .\TestProject~ `
    -BuildInPlace `
    -OpenViewer
```

Use `-ReusePlayer` after a successful build to skip Unity import and build time. Reuse is fail-closed:
the coordinator fingerprints Player inputs, the dependency lock, and Unity version and refuses a
stale Player before launching any role.

## Run the complete matrix

Build once, then run every built-in scenario against that exact Player:

```powershell
pwsh -File .\Tools~\Invoke-PurrNetNetworkTestSuite.ps1 `
    -ProjectPath .\TestProject~ `
    -BuildInPlace `
    -Repeat 2
```

Every repetition must pass; the suite never converts an intermittent failure into success by majority
vote. Add `-ReusePlayer` to reuse a fingerprint-matching build from an earlier command.

For a human-observable run, use the interactive launcher. From this repository it automatically
selects `TestProject~`, uses its required in-place build mode, and opens one live three-role viewer
that follows each scenario as the suite advances. It opens on the raw log tabs so ongoing packet
activity is visible; select `Harness evidence` in any role pane to inspect its structured result:

```powershell
pwsh -File .\Tools~\Invoke-PurrNetNetworkTestSuiteInteractive.ps1
```

The window remains on the last run after the suite finishes, so its final evidence and raw logs stay
available for inspection. Close the included `TestProject~` Editor before using the no-argument
launcher because its local package reference requires guarded in-place compilation. When invoking
the launcher from an installed package, run it from the consuming Unity project directory or pass
`-ProjectPath`; staged builds remain the default there.

## Add a feature scenario

Create a concrete `NetworkTestScenario` in a runtime assembly that references
`Amilverton.PurrNetTesting.Runtime`, then give it one stable identifier:

```csharp
[NetworkTestScenario("Inventory.ContainerGiveItem")]
public sealed class ContainerGiveItemScenario : NetworkTestScenario
{
    protected override void OnScenarioSpawned(bool asServer)
    {
        // Arrange and run the normal PurrNet request/replication path for this role.
    }
}
```

The builder discovers attributed types and generates their network prefabs only inside the staging
project. A scenario owns fixture setup and expected facts; the package owns process roles, endpoint
configuration, staggered client launch, timeouts, atomic files, logs, lifecycle, and cross-role
validation. A passing scenario must publish shared facts and at least one role-owned assertion; use
`Session.SetEvidence` for observations that intentionally differ by role. See
[the protocol](Documentation~/protocol.md) for the exact readiness and result contract.

Keep scenarios narrow. Use named network milestones rather than fixed sleeps, validate all client
intent on the server, and report only stable IDs, amounts, indices, revisions, and booleans.

## Agent use

The repo-local skill at `.agents/skills/run-purrnet-network-tests` teaches an agent to find the installed
package, run the coordinator, and interpret failures. It delegates execution to the versioned
PowerShell tool so agent instructions cannot drift away from the actual protocol.

## Build safety

- The default runner copies the project without `Library`, `Temp`, `Logs`, build outputs, artifacts, or
  `.git`, then gives the staging copy its own `Library`.
- `-BuildInPlace` refuses to run if a Unity process already has that exact project open.
- Generated Unity assets are confined to `Assets/PurrNetNetworkTestGenerated` and only that exact folder
  is removed after a build.
- The generated scene does not serialize PurrNet's `NetworkManager`. PurrNet `1.19.1` produced a corrupt
  standalone `level0` when that component was serialized in the tested Unity 6 editors, so the harness
  creates and configures the real manager and UDP transport on an inactive root at runtime.
- Passing child processes must exit naturally. Forced termination is reserved for failure cleanup,
  while diagnostic run artifacts are retained.
- PurrNet Local Transport is never used as replication evidence; the included scenario uses UDP over
  loopback and separate operating-system processes.
