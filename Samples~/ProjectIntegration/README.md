# Project integration bootstrap sample

This sample demonstrates the portable authored-root form for PurrNet's Git package. Its project-owned
`ExampleProjectNetworkPrefabs` and `ExampleProjectNetworkRules` subclasses give Unity resolvable
script identities; the project-owned hook serializes those assets and asks the harness to create the
sealed PurrNet `NetworkManager` and `UDPTransport` immediately before activation.

After importing the sample, run:

`Tools > PurrNet Network Tests > Samples > Create Hook-Provisioned Root`

The command creates `Assets/NetworkTests/Generated/ProjectNetworkTestBootstrap.prefab` plus its
catalog and rules assets without overwriting valid existing assets. Put that prefab's path in
`ProjectSettings/PurrNetNetworkTests.json` as `bootstrapPrefabPath`.

The example catalog starts empty. Before testing game prefabs, add them to
`ExampleProjectNetworkPrefabs.prefabs`, keep `autoGenerate` disabled, call `Refresh()`, and save the
asset. The harness preserves project prefab IDs and appends generated scenario prefabs after the
project provider. Do not add a serialized `NetworkManager` or transport to the hook-provisioned
prefab; mixed root forms are refused.
