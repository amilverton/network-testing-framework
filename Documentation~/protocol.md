# Runner protocol

The coordinator builds one Windows Player and launches it as three independent processes:
`Server`, `OwnerClient`, and `ObserverClient`. Every process receives the same run ID, scenario ID,
loopback endpoint, and timeout, but writes to its own ready, result, and log paths.

Client launch is checkpointed: `OwnerClient` must publish scenario-owned readiness before
`ObserverClient` starts. Scenarios which need both clients delay their action phase until both roles
have registered; late-join scenarios can commit authoritative state before the observer process exists.

## Readiness

Readiness is scenario-owned. The server publishes readiness only after its real UDP listener is
connected and its network fixture has spawned. A client publishes readiness only after it has
connected, spawned the same fixture, received its role assignment from the server, and observed the
scenario's initial replicated state.

Files are written to a temporary sibling and atomically renamed into place. A coordinator must ignore
files whose names contain `.tmp-`.

## Completion barrier and final results

`Session.Pass(revision)` first publishes a role-specific `*.result.json.complete.json` artifact. The
coordinator validates the run, scenario, role, and exact contract revision from all three completion
artifacts before atomically publishing three role-bound `*.result.json.stop.json` signals. This keeps
the dedicated server alive until both clients have completed their own real network assertions and
prevents one successful role from tearing down another role's unfinished test.

Each role publishes schema version `2`, the exact run/scenario/role identity, a lowercase status,
named milestones, a state revision, compact `sharedFacts`, asymmetric `roleEvidence`, ordered
role-owned `assertions`, an optional failure, and its log path. Process ID, role, provenance, and the
milestone-derived transition trace are supplied by the harness rather than the scenario.

`Session.Pass(revision)` remains provisional after the barrier. Once its valid stop signal arrives,
the role stops its active PurrNet client/server, waits for both connection states to become
disconnected, and invokes the optional project post-stop hook.
Only then does it atomically publish a passing result. A shutdown, disconnect, or hook failure revokes
the requested pass. The coordinator still treats that final result as provisional until the Player
exits naturally with code zero.

The coordinator compares revisions and shared facts across roles and requires three distinct process
IDs. Built-in scenarios use the package-owned contracts in `Tools~/BuiltInScenarioContracts.json`.
Project scenarios use contracts embedded from `ProjectSettings/PurrNetNetworkTests.json` into the
SHA-256-bound Player execution manifest. In both cases, missing, extra, reordered, or incorrect facts,
evidence, assertions, milestones, and revisions fail validation.

Any timeout, early or non-zero process exit, malformed JSON, failed role, revision mismatch, fact
mismatch, contract mismatch, or stale Player fingerprint is a failed run. The run directory and all
logs remain available under `Artifacts/NetworkTests/Runs`.
The optional `-OpenViewer` switch opens a non-locking three-pane live view of those role logs and
their ready/result artifacts. It is a human diagnostic surface and is not required by CI or agents.

## Scenario discovery

Create a concrete `NetworkTestScenario`, annotate it with one stable
`[NetworkTestScenario("Feature.Scenario")]` ID, and register it with an exact contract in
`ProjectSettings/PurrNetNetworkTests.json`. A `typeName` entry creates a temporary generated network
prefab; a `prefabPath` entry includes one authored prefab whose root owns the only scenario component.
Generated assets live only in the exact `Assets/PurrNetNetworkTestGenerated` folder and are removed
after the build. See [portable project integration](project-integration.md) for the manifest schema and
the constrained project bootstrap/provider/hook envelope.

Use the scenario's normal PurrNet request path. Client intent belongs in a `ServerRpc`; the server must
derive identity from `RPCInfo.sender`, validate before mutation, and expose state through real
replication. Call `Session.PublishReady()`, record compact shared facts and role-owned assertions,
optionally record asymmetric observations through `Session.SetEvidence`, and finish through
`Session.Pass(revision)` or `Session.Fail(message)`.
