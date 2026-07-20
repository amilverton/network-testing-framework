# Portable project integration contract

Portable v1 lets a consuming Unity project place its own PurrNet scenario, exact result contract, and
optionally its own constrained network bootstrap into the harness's generated three-process Player.
The package owns process orchestration and validation; the project owns gameplay setup and expected
facts.

## Fixed execution envelope

The builder accepts exactly Unity `6000.4.10f1`, PurrNet `1.19.1`, Windows
`StandaloneWindows64`, the Mono Standalone scripting backend, `UDPTransport` over loopback, and the
three roles `Server`, `OwnerClient`, and `ObserverClient`. These are executable constraints, not
recommendations. The builder stops before producing a Player when any of them differs.

Install both Git dependencies directly in `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.caffeinated.network-testing": "https://github.com/amilverton/caffeinated-network-testing.git#v0.5.0",
    "dev.purrnet.purrnet": "https://github.com/PurrNet/PurrNet.git?path=/Assets/PurrNet#v1.19.1"
  }
}
```

## Project manifest schema

The conventional path is `ProjectSettings/PurrNetNetworkTests.json`. A missing manifest is valid and
means that only built-in scenarios are available. Once the file exists, parsing and validation are
strict: duplicate or unknown JSON members, missing required members, unsupported value types, or
invalid references fail the build.

The root object contains:

- `schemaVersion`: required integer `1`;
- `bootstrapPrefabPath`: optional forward-slash asset path under `Assets/`; omit it to use the
  generated fallback network root;
- `scenarios`: required array of project scenario entries;
- `suites`: required array of named, ordered project scenario groups.

Every scenario entry contains:

- `id`: required, nonblank, case-sensitive, unique ID; `Harness.` is reserved;
- `enabled`: required Boolean;
- exactly one nonblank `typeName` or `prefabPath`;
- `contract`: required exact contract even when the entry is disabled.

Every contract contains schema version `1`, a nonnegative `stateRevision`, an exact `sharedFacts`
object, and exact role contracts named `Server`, `OwnerClient`, and `ObserverClient`. Fact and evidence
values may only be Boolean, String, or signed 32-bit Integer. Each role declares `evidence`,
`readyMilestones`, `assertions`, and `milestones`. The three arrays are nonempty, ordered, contain
unique nonblank strings, and `readyMilestones` must be an exact prefix of `milestones`.
A runnable scenario must declare and publish at least one shared fact because the coordinator rejects
an empty final `sharedFacts` object.

A suite name is unique. Its `scenarios` array is ordered, has no duplicates, and may reference only
enabled scenario IDs from the same manifest. Suites are emitted into the Player execution manifest;
portable v1 runs their members by passing those IDs to the suite script's `-Scenarios` parameter.

### Exact-contract example

This complete example uses a code-only scenario and an authored network bootstrap:

```json
{
  "schemaVersion": 1,
  "bootstrapPrefabPath": "Assets/NetworkTests/ProjectNetworkRoot.prefab",
  "scenarios": [
    {
      "id": "Game.CounterIncrement",
      "enabled": true,
      "typeName": "Game.NetworkTests.CounterIncrementScenario, Game.NetworkTests",
      "contract": {
        "schemaVersion": 1,
        "stateRevision": 1,
        "sharedFacts": {
          "counter.final": 15,
          "requester": "OwnerClient"
        },
        "roles": {
          "Server": {
            "evidence": {
              "acceptedAmount": 5
            },
            "readyMilestones": [
              "server-listening",
              "fixture-spawned"
            ],
            "assertions": [
              "server-derived-requester-from-rpc-sender"
            ],
            "milestones": [
              "server-listening",
              "fixture-spawned",
              "increment-accepted",
              "clients-converged"
            ]
          },
          "OwnerClient": {
            "evidence": {
              "requestedIncrement": true
            },
            "readyMilestones": [
              "client-connected",
              "fixture-spawned"
            ],
            "assertions": [
              "owner-observed-replicated-counter"
            ],
            "milestones": [
              "client-connected",
              "fixture-spawned",
              "increment-requested",
              "counter-observed"
            ]
          },
          "ObserverClient": {
            "evidence": {
              "requestedIncrement": false
            },
            "readyMilestones": [
              "client-connected",
              "fixture-spawned"
            ],
            "assertions": [
              "observer-observed-replicated-counter"
            ],
            "milestones": [
              "client-connected",
              "fixture-spawned",
              "counter-observed"
            ]
          }
        }
      }
    }
  ],
  "suites": [
    {
      "name": "project-smoke",
      "scenarios": [
        "Game.CounterIncrement"
      ]
    }
  ]
}
```

The contract is coordinator-owned truth. A pass requires the exact revision, exact shared-fact and
project evidence keys and values, exact assertion order, exact final milestone order, and the exact
ready prefix for each role. The harness also adds and verifies process-derived `role`, `processId`,
`provenance`, and `transitionTrace` evidence. All three reports must agree on the shared facts and
revision, and their process IDs must be distinct.

## `typeName` versus `prefabPath`

Use `typeName` when the fixture can be generated from a component type. The value must exactly identify
the attributed concrete type using either its simple assembly-qualified name
(`Namespace.Type, Assembly`) or its full assembly-qualified name. The `[NetworkTestScenario]` ID must
exactly match the manifest ID. During the isolated build the package creates the temporary network
prefab and adds supported features requested by the attribute.

Use `prefabPath` when the scenario needs authored serialized components or references. The path must
be a forward-slash project path under `Assets/`. The prefab root must contain exactly one
`NetworkTestScenario`, descendants must contain none, and the root scenario type's attribute ID must
match the manifest ID. The builder includes the authored prefab directly and fingerprints its asset
dependency closure.

Every concrete attributed project scenario found in the compilation must have a manifest entry. An
enabled entry must resolve unambiguously at build time. The builder never silently falls back from one
source kind to the other.

## Authored bootstrap, provider, and hook rules

Omit `bootstrapPrefabPath` when no project-owned PurrNet setup is needed. The harness creates an
inactive fallback root and adds one `NetworkManager`, one `UDPTransport`, and runtime `NetworkRules`
before activation.

When `bootstrapPrefabPath` is present, save that prefab inactive and use exactly one of two root
forms.

A directly serialized root's complete hierarchy must contain:

- exactly one `NetworkManager`;
- exactly one `UDPTransport` and no other `GenericTransport`;
- the manager's `transport` field referencing that UDP transport;
- serialized, non-null `NetworkRules`;
- at most one optional `NetworkTestBootstrapHook`.

Some Git-installed PurrNet layouts expose runtime types to C# but do not expose their source files as
resolvable Unity `MonoScript` assets. In that case, derive one project-owned hook from
`NetworkTestBootstrapHook`, put only that hook on the inactive prefab, and call
`ConfigureUdpNetworkRootForBuild(NetworkPrefabs, NetworkRules)` while authoring it. The hook-owned
project assets remain serialized, while the harness creates the sealed PurrNet `NetworkManager` and
its `UDPTransport` in memory before activation. A provisioned root must contain no serialized
`NetworkManager` or transport; mixing both forms is refused.

The catalog and rules assets must also have project-owned Unity script identities in that layout.
Define sealed project classes deriving from `NetworkPrefabs` and `NetworkRules`, create assets of
those derived types, disable catalog `autoGenerate`, populate stable project-prefab entries, call
`Refresh()`, and pass the derived assets to the hook. Import the package's **Project Integration
Bootstrap** sample for an executable authoring command and exact source pattern.

The harness refuses an active prefab so PurrNet cannot initialize before the role-specific loopback
address and port are set. It first provisions the root when requested, validates the resulting exact
component envelope, clears auto-start client/server flags, activates the root, verifies that the same
rules and manager survive activation, and then composes prefab providers.

A synchronous project `IPrefabProvider` is supported. The harness refreshes and validates every
project and scenario entry: prefab IDs must be nonnegative and unique within their provider, prefabs
must be non-null and unique, project and scenario prefabs must be distinct, and the combined ID span
must not overflow. It composes the project provider first and the generated scenario provider second,
then verifies that every project prefab ID and its pooling/warmup data were preserved. Project
Addressables, `IAsyncPrefabProvider`, and a pre-existing nested `CompositePrefabProvider` are refused
in v1.

Derive one optional project hook from `NetworkTestBootstrapHook`. The harness calls
`OnPreNetworkStart(NetworkTestBootstrap, NetworkManager)` once after activation/provider composition
and before starting the role. After a requested pass, the role publishes its completion artifact and
waits until the coordinator has validated all three roles at the same exact revision. The coordinator
then issues role-bound stop signals; each role stops its active client/server, waits for both PurrNet
states to reach `Disconnected`, and calls
`OnPostNetworkStop(NetworkTestBootstrap, NetworkManager)` once before publishing the final pass.
Hooks are synchronous. A thrown exception fails the role; a post-stop failure revokes a provisional
pass.

The selected project bootstrap is used for built-in scenarios as well as project scenarios. Its hook
may validate shared services and provider preservation for every run, but it must gate
scenario-specific milestones, evidence, assertions, and facts on `bootstrap.ScenarioId`. Emitting
project-only protocol data during a built-in scenario intentionally fails that built-in's exact
contract.

## Staged build behavior

`Invoke-PurrNetNetworkTests.ps1` stages by default. It copies the project while excluding Unity caches,
temporary data, logs, build outputs, artifacts, and source-control metadata, then lets the staged copy
create its own `Library`. Generated test assets are confined to
`Assets/PurrNetNetworkTestGenerated` and removed after the build.

Top-level `file:` dependencies from the project manifest are vendored into
`Packages/LocalPackages/<name>-<digest>` inside the stage. Distributable package content—including
`Tools~`, `Skills~`, `Documentation~`, and `Samples~` when present—is copied while repository fixture
projects, caches, artifacts, IDE files, and VCS metadata are excluded;
the manifest and a compatible depth-zero lock entry are rewritten only at the exact dependency value.
The staging helper verifies copied content by digest and refuses missing/misnamed packages, malformed or
ambiguous JSON, UTF-16 JSON, unsafe filesystem roots, reparse points, destination collisions, and
nested local `file:` dependency chains. Publish/embed the nested package or declare it as another
top-level project dependency.

Project, settings, harness, and local-package fingerprints are streamed through bounded buffers, so
large Unity assets do not have to fit in memory. The coordinator invalidates the reusable fingerprint
before a build, recomputes it after Unity exits, and refuses the new Player if source inputs drifted
during staging or compilation.

Use `-KeepStaging` to retain the isolated copy for diagnosis. Use `-BuildInPlace` only when staging is
inapplicable and the exact project is closed; the coordinator refuses an in-place build when Unity has
that project open. `-ReusePlayer` is also fail-closed and accepts only a fingerprint-matching build with
an intact schema-2 build receipt and SHA-256-bound execution manifest.
`-BuildTimeoutSeconds` bounds Unity compilation separately from the three-role `-TimeoutSeconds` run
deadline. Either timeout terminates the coordinator-owned process tree and preserves diagnostics.

## Editor, CLI, and viewer surfaces

The Unity menu `Tools > Caffeinated Network Testing > Package Control` provides four guarded actions:

- create a missing empty project manifest without overwriting;
- install/update the packaged AI skill;
- stage an incoming skill copy for review when the active copy must remain untouched;
- launch the complete built-in plus enabled-project interactive suite and live viewer.

Buttons are disabled when their prerequisites are blocked. Refresh the status list after correcting a
problem. The final build remains the authoritative validation of the exact support envelope and
project assets.

For automation, run `Tools~/Invoke-PurrNetNetworkTests.ps1 -ProjectPath <project> -Scenario <id>`.
Use `Invoke-PurrNetNetworkTestSuite.ps1 -Scenarios <ids>` for a matrix and
`Invoke-PurrNetNetworkTestSuiteInteractive.ps1` for a watched matrix. `-OpenViewer` adds the live
three-role view to a single run. `Show-PurrNetNetworkTestLogs.ps1` opens a retained run by `-RunPath`
or follows the artifacts root by `-ArtifactsPath -FollowNewestRun`. The viewer's Evidence tab shows
readiness, result, assertions, role-local evidence, milestones, shared facts, and failures; Raw Log
shows the original Unity output.

## Failure and refusal boundaries

The following conditions are failures, never warnings to work around:

- unsupported Unity, PurrNet, target, backend, transport, provider, bootstrap, or process topology;
- malformed project/build/execution/ready/result JSON or stale identities; `.tmp-` siblings are
  incomplete atomic writes and are ignored rather than accepted;
- missing exact contracts, ambiguous scenario discovery, invalid prefab/type sources, or disabled or
  unknown suite members;
- a readiness or final artifact that differs in membership, value, order, role, or provenance;
- timeout, premature or nonzero process exit, failed role, revision/fact disagreement, or a stale
  Player fingerprint;
- network shutdown or pre/post hook failure after `Session.Pass` was requested.

On failure the coordinator stops only the child processes it launched and retains the run directory.
Inspect `build.log`, the Player build receipt and execution manifest, `*.ready.json`, `*.result.json`,
and the three role logs before changing code.

## Clean-context agent workflow

Resolve the installed package root, then install its package-owned skill:

```powershell
& (Join-Path $packageRoot 'Tools~\Install-PurrNetNetworkTestSkill.ps1') `
    -ProjectPath $projectRoot
```

The installer writes `.agents/skills/run-caffeinated-network-tests` and an ownership record containing the
package version, canonical source hash, and installed-content hash. It atomically replaces only a
previously owned, hash-matching copy. It refuses an unowned directory or a locally modified installed
copy. Use `-StageIncoming` to create a versioned sibling for explicit review instead of overwriting
local work.

Start the agent in the consuming repository after installation and invoke
`$run-caffeinated-network-tests`. A clean-context agent should resolve the package and ownership record,
inspect the exact manifest/source contract, use the versioned coordinator rather than recreating its
logic, parse the final JSON, and retain the artifact path in its report. On failure it should inspect
existing evidence first and preserve the run directory.
