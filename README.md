# PurrNet Network Test Harness

Build one test-only Unity Player, launch it as a dedicated server plus two independent clients, and
receive compact machine-readable proof of PurrNet authority and replication. The normal Unity Editor
can remain open because the default build happens in an isolated staging copy.

The first included scenario, `Harness.InventoryTransfer`, proves a complete vertical slice:

- the owner client sends the normal request through a PurrNet `ServerRpc`;
- the server derives the caller from `RPCInfo.sender`, validates it, and commits one state revision;
- both the owner and a non-owner observer receive the same `SyncVar` state;
- the observer attempts the protected request through the same RPC;
- the server rejects it, and all three roles prove that the state and revision remained unchanged.

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

The command returns JSON only after `Server`, `OwnerClient`, and `ObserverClient` all publish passing
results whose revision and shared facts match. Failed runs retain ready files, result files, and one
log per process under `Artifacts/NetworkTests/Runs/<run-id>`.

Add `-OpenViewer` to the coordinator for a live Windows view with side-by-side Server, OwnerClient,
and ObserverClient evidence. Each pane opens on the role's readiness, revision, milestones, facts,
and failure details; a `Raw Unity log` tab retains the original Player output. To inspect an existing
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

Use `-ReusePlayer` after a successful build to skip Unity import and build time. Rebuild whenever
scenario or package code changes.

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
configuration, timeouts, atomic files, logs, lifecycle, and cross-role validation. See
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
- Every child process is stopped in a `finally` block, while diagnostic run artifacts are retained.
- PurrNet Local Transport is never used as replication evidence; the included scenario uses UDP over
  loopback and separate operating-system processes.
