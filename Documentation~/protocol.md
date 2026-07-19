# Runner protocol

The coordinator builds one Windows Player and launches it as three independent processes:
`Server`, `OwnerClient`, and `ObserverClient`. Every process receives the same run ID, scenario ID,
loopback endpoint, and timeout, but writes to its own ready, result, and log paths.

## Readiness

Readiness is scenario-owned. The server publishes readiness only after its real UDP listener is
connected and its network fixture has spawned. A client publishes readiness only after it has
connected, spawned the same fixture, received its role assignment from the server, and observed the
scenario's initial replicated state.

Files are written to a temporary sibling and atomically renamed into place. A coordinator must ignore
files whose names contain `.tmp-`.

## Final results

Each role publishes schema version `1`, the exact run/scenario/role identity, a lowercase status,
named milestones, a state revision, compact shared facts, an optional failure, and its log path.
The runner requires all three roles to pass, then compares both client revisions and shared facts to
the authoritative server report.

Any timeout, early process exit, malformed JSON, failed role, revision mismatch, or fact mismatch is
a failed run. The run directory and all logs remain available under `Artifacts/NetworkTests/Runs`.
The optional `-OpenViewer` switch opens a non-locking three-pane live view of those role logs and
their ready/result artifacts. It is a human diagnostic surface and is not required by CI or agents.

## Scenario discovery

Create a concrete `NetworkTestScenario`, annotate it with one stable
`[NetworkTestScenario("Feature.Scenario")]` ID, and keep setup code-driven. The build method discovers
the type, creates a temporary network prefab in the staging project, registers it with PurrNet, and
includes it in the test Player. Generated assets live only in the exact
`Assets/PurrNetNetworkTestGenerated` folder and are removed after the build.

Use the scenario's normal PurrNet request path. Client intent belongs in a `ServerRpc`; the server must
derive identity from `RPCInfo.sender`, validate before mutation, and expose state through real
replication. Call `Session.PublishReady()`, record compact shared facts, and finish through
`Session.Pass(revision)` or `Session.Fail(message)`.
