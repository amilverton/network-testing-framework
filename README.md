# Caffeinated Network Testing

Build one test-only Unity Player, launch it as a dedicated server plus two independent clients, and
receive machine-readable proof of PurrNet authority, sender validation, and replication. The default
build runs in an isolated staging copy, so the normal Unity Editor can remain open.

This is a focused standalone integration harness, not a replacement for Unity Test Framework unit or
PlayMode tests.

## Portable v1 support envelope

Portable project scenarios are executable proof only inside this exact envelope:

| Surface | Portable v1 contract |
| --- | --- |
| Unity | `6000.4.10f1` |
| PurrNet | `1.19.1` |
| Host and Player | Windows, `StandaloneWindows64` |
| Scripting backend | Mono |
| Transport | PurrNet `UDPTransport`, loopback |
| Processes | one `Server`, one `OwnerClient`, one `ObserverClient` |

PowerShell 7 and Windows Build Support for the exact Unity editor are required. Git must be available
when Unity Package Manager first resolves the dependencies. The Editor package-control inspection is
an early prerequisite check; the Player builder is authoritative and refuses any Unity version,
PurrNet version, or Standalone scripting backend outside the table above.

## Install from Git

Add both dependencies directly to the consuming project's `Packages/manifest.json`. Unity Package
Manager cannot reliably resolve PurrNet's Git package from the harness's transitive semantic-version
dependency, so the direct PurrNet entry is mandatory.

```json
{
  "dependencies": {
    "com.caffeinated.network-testing": "https://github.com/amilverton/caffeinated-network-testing.git#v0.5.0",
    "dev.purrnet.purrnet": "https://github.com/PurrNet/PurrNet.git?path=/Assets/PurrNet#v1.19.1"
  }
}
```

The harness is pinned to its tested `v0.5.0` release. Do not change either package pin without
re-establishing the executable compatibility proof.

## Integrate a project scenario

Open `Tools > Caffeinated Network Testing > Package Control` in Unity. The window reports prerequisites and
the project manifest status, can create a missing `ProjectSettings/PurrNetNetworkTests.json` without
overwriting an existing file, installs or stages the packaged AI skill, and can launch the complete
built-in plus enabled-project interactive suite with the live viewer. Closing the window or recompiling scripts cancels an
external operation started by that window.

For a project-authored PurrNet root, import the package's **Project Integration Bootstrap** sample.
It supplies project-owned catalog/rules wrappers and an idempotent authoring command for the
hook-provisioned UDP form used when Git-package PurrNet scripts cannot be serialized directly.

Project scenario code must live in a runtime-compatible assembly definition that references
`Caffeinated.NetworkTesting.Runtime`, `PurrNet.Runtime`, and the game assemblies it exercises. Do not
put a Player scenario in an Editor-only or Unity Test Framework-only assembly.

Create a concrete scenario with one stable ID:

```csharp
[NetworkTestScenario("Game.InventoryTransfer")]
public sealed class InventoryTransferScenario : NetworkTestScenario
{
    protected override void OnScenarioSpawned(bool asServer)
    {
        // Exercise the project's real PurrNet request and replication path.
    }
}
```

Then register the scenario and its exact three-role contract in
`ProjectSettings/PurrNetNetworkTests.json`. Use exactly one of `typeName` or `prefabPath` for every
entry. See [portable project integration](Documentation~/project-integration.md) for the complete
schema, an exact-contract example, authored bootstrap/provider rules, staging behavior, and refusal
semantics. See [the runner protocol](Documentation~/protocol.md) for atomic readiness and result files.

## Run from PowerShell

Resolve the package from `Library/PackageCache` (or use its embedded path), then run one scenario. The
staging build is the default:

```powershell
$packageRoot = (Get-ChildItem .\Library\PackageCache -Directory |
    Where-Object Name -Like 'com.caffeinated.network-testing@*' |
    Select-Object -First 1).FullName

& (Join-Path $packageRoot 'Tools~\Invoke-PurrNetNetworkTests.ps1') `
    -ProjectPath $PWD `
    -Scenario 'Game.InventoryTransfer'
```

The command emits final JSON only after exact contract validation and natural zero-code exit by all
three roles. `-OpenViewer` opens the three-pane evidence/raw-log viewer. `-ReusePlayer` skips the build
only when the input fingerprint, dependency locks, authored asset dependencies, Unity version, build
receipt, and execution-manifest hash still match.
Unity builds have a separate bounded deadline (`-BuildTimeoutSeconds`, default 600); scenario
readiness, completion, and natural exit share `-TimeoutSeconds`. A deadline failure terminates only
the coordinator-owned process tree and retains its run diagnostics.

Run an explicit matrix with the suite coordinator:

```powershell
& (Join-Path $packageRoot 'Tools~\Invoke-PurrNetNetworkTestSuite.ps1') `
    -ProjectPath $PWD `
    -Scenarios @('Game.InventoryTransfer', 'Game.LateJoin') `
    -Repeat 2
```

Every repetition must pass; there is no majority-vote success. Manifest `suites` preserve named,
ordered scenario membership in the generated execution manifest, but portable v1 selects project
scenarios at the CLI with `-Scenarios`; it does not expose a `-Suite` selector.

For a watched matrix, use `Invoke-PurrNetNetworkTestSuiteInteractive.ps1`. It opens one viewer and
follows each run. Inspect an existing run directly with either a fixed run directory or the artifacts
root:

```powershell
& (Join-Path $packageRoot 'Tools~\Show-PurrNetNetworkTestLogs.ps1') `
    -RunPath .\Artifacts\NetworkTests\Runs\<run-id>
```

The viewer is a Windows/WPF diagnostic surface; CI and agents consume the JSON artifacts directly.

## Install the packaged AI workflow

Install the package-owned skill into the consuming repository before handing the work to a
clean-context agent:

```powershell
& (Join-Path $packageRoot 'Tools~\Install-PurrNetNetworkTestSkill.ps1') `
    -ProjectPath $PWD
```

This creates `.agents/skills/run-caffeinated-network-tests` plus an ownership record binding the installed
content hash to the harness package version. The installer refuses to overwrite an unowned or locally
modified skill. When review is intentional, rerun with `-StageIncoming`; it writes a versioned sibling
such as `run-caffeinated-network-tests.incoming-0.5.0` and leaves the active skill untouched. Start a clean
agent context after installation and invoke `$run-caffeinated-network-tests`; the skill resolves the
installed package tools, checks the strict inputs, runs the coordinator, and interprets retained
evidence without reimplementing the protocol.

The same install/update and incoming-review operations are available from the Package Control window.

## Built-in proof matrix

The package also includes eight real-network scenarios:

- `Harness.SustainedPacketStream` — ordered interval traffic, delivery, cadence, and convergence;
- `Harness.RpcRouting` — exact TargetRpc and ObserversRpc inclusion/exclusion;
- `Harness.CrossPlayerDamage` — sender-derived damage, replication, and replay rejection;
- `Harness.LateJoinState` — committed late-join snapshot followed by a live delta;
- `Harness.OwnerNetworkTransform` — owner movement, convergence, and spoof isolation;
- `Harness.OwnershipTransfer` — old-owner mutation, handoff, new-owner mutation, and revocation;
- `Harness.SyncListOrder` — exact callback order and final collection state;
- `Harness.InventoryTransfer` — accepted owner intent and rejected observer mutation.

Run the default complete matrix with `Invoke-PurrNetNetworkTestSuite.ps1` and no `-Scenarios`
argument. It includes all eight built-ins plus every enabled project-manifest scenario. The Package
Control window's interactive-suite action runs the same complete matrix.

## Fail-closed behavior

- The default runner copies the project without caches, logs, builds, artifacts, or VCS metadata. It
  vendors supported top-level local `file:` packages into the staging project and refuses nested local
  dependency chains, reparse points, unsafe roots, ambiguous JSON, or copy-digest drift.
- `-BuildInPlace` refuses to batch-build when Unity already has that exact project open. Use it only
  when the project is closed and in-place compilation is explicitly acceptable.
- Generated assets exist only under `Assets/PurrNetNetworkTestGenerated` during a build and that exact
  folder is cleaned afterward. Authored project assets are never generated or overwritten.
- Missing or malformed exact contracts, ambiguous scenario sources, unsupported authored network
  roots/providers, stale Player inputs, malformed/stale JSON, timeout, early/nonzero exit, wrong
  milestones/assertions/evidence/facts/revision, or cross-role disagreement fail the run.
- A scenario's `Session.Pass` is provisional. Each role publishes a completion-barrier artifact; only
  after the coordinator validates all three identities and revisions does it issue role-bound stop
  signals. The harness then stops PurrNet, waits for both client and server states to become
  disconnected, runs the post-stop hook, publishes the final result, and requires natural process
  exit. Diagnostic artifacts remain under
  `Artifacts/NetworkTests/Runs/<run-id>` on failure.
