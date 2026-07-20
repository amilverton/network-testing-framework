using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Caffeinated.NetworkTesting;
using Caffeinated.NetworkTesting.Editor.ProjectConfiguration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PurrNet;
using PurrNet.Transports;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Caffeinated.NetworkTesting.Editor
{
    /// <summary>
    /// Generates a test-only bootstrap scene and builds one Windows Player for all roles.
    /// </summary>
    public static class NetworkTestPlayerBuilder
    {
        private const string BuildPathArgument = "-networkTestBuildPath";
        private const string BuiltInScenarioPrefix = "Harness.";
        private const string GeneratedRoot = "Assets/PurrNetNetworkTestGenerated";
        private const string GeneratedScenarioRoot = GeneratedRoot + "/Scenarios";
        private const string BootstrapScenePath = GeneratedRoot + "/NetworkTestBootstrap.unity";
        private const string SupportedUnityVersion = "6000.4.10f1";
        private const string SupportedPurrNetVersion = "1.19.1";

        /// <summary>
        /// Command-line entry point used by Tools~/Invoke-PurrNetNetworkTests.ps1.
        /// </summary>
        public static void BuildFromCommandLine()
        {
            string buildPath = ReadRequiredArgument(Environment.GetCommandLineArgs(), BuildPathArgument);
            Build(buildPath);
        }

        /// <summary>
        /// Build a Player containing built-in scenarios and enabled project-manifest scenarios.
        /// </summary>
        public static void Build(string buildPath)
        {
            if (string.IsNullOrWhiteSpace(buildPath))
                throw new ArgumentException("Build path cannot be empty.", nameof(buildPath));

            string fullBuildPath = Path.GetFullPath(buildPath);
            string buildDirectory = Path.GetDirectoryName(fullBuildPath);
            if (string.IsNullOrEmpty(buildDirectory))
                throw new InvalidOperationException($"Build path '{fullBuildPath}' has no parent directory.");

            string projectPath = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrWhiteSpace(projectPath))
                throw new InvalidOperationException("Unity project root could not be resolved from Application.dataPath.");

            Directory.CreateDirectory(buildDirectory);

            BuildMetadata metadata = ReadAndValidateBuildMetadata();
            ProjectNetworkTestManifest manifest = LoadProjectManifest(projectPath);
            JObject builtInContracts = LoadBuiltInContracts(metadata.HarnessPackagePath);
            List<DiscoveredScenario> scenarios = DiscoverScenarios(manifest, builtInContracts);
            AssetDependencyReceipt[] inputAssets = CollectInputAssetDependencies(projectPath, manifest, scenarios);

            string previousProductName = PlayerSettings.productName;
            bool previousRunInBackground = PlayerSettings.runInBackground;

            try
            {
                DeleteGeneratedAssets();
                CreateGeneratedFolders();

                NetworkTestScenarioRegistration[] registrations = CreateScenarioPrefabs(scenarios);
                CreateBootstrapScene(registrations, manifest.BootstrapPrefabPath);

                PlayerSettings.productName = "PurrNetNetworkTestPlayer";
                PlayerSettings.runInBackground = true;

                BuildPlayerOptions options = new BuildPlayerOptions
                {
                    scenes = new[] { BootstrapScenePath },
                    locationPathName = fullBuildPath,
                    target = BuildTarget.StandaloneWindows64,
                    targetGroup = BuildTargetGroup.Standalone,
                    options = BuildOptions.None
                };

                BuildReport report = BuildPipeline.BuildPlayer(options);
                if (report.summary.result != BuildResult.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"Network test Player build failed with result '{report.summary.result}' and " +
                        $"{report.summary.totalErrors} errors. See the Unity build log.");
                }

                ExecutionManifestReceipt executionManifest = WriteExecutionManifest(
                    fullBuildPath,
                    metadata,
                    manifest,
                    scenarios);
                WriteBuildReceipt(
                    fullBuildPath,
                    scenarios,
                    report,
                    metadata,
                    executionManifest,
                    inputAssets,
                    GetProjectManifestHash(projectPath));

                Debug.Log(
                    $"[Build] Built network test Player '{fullBuildPath}' with {scenarios.Count} scenario(s). " +
                    $"Size: {report.summary.totalSize} bytes.");
            }
            finally
            {
                PlayerSettings.productName = previousProductName;
                PlayerSettings.runInBackground = previousRunInBackground;
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                DeleteGeneratedAssets();
            }
        }

        private static BuildMetadata ReadAndValidateBuildMetadata()
        {
            UnityEditor.PackageManager.PackageInfo harnessPackage =
                UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(NetworkTestPlayerBuilder).Assembly);
            UnityEditor.PackageManager.PackageInfo purrNetPackage =
                UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(NetworkManager).Assembly);

            if (harnessPackage == null || string.IsNullOrWhiteSpace(harnessPackage.resolvedPath))
                throw new InvalidOperationException("The installed network-test package could not be resolved.");

            if (purrNetPackage == null)
                throw new InvalidOperationException("The installed PurrNet package could not be resolved.");

            if (!string.Equals(Application.unityVersion, SupportedUnityVersion, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Package v1 supports Unity {SupportedUnityVersion} exactly. " +
                    $"This project uses {Application.unityVersion}.");
            }

            if (!string.Equals(purrNetPackage.version, SupportedPurrNetVersion, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Package v1 supports PurrNet {SupportedPurrNetVersion} exactly. " +
                    $"This project resolved {purrNetPackage.version}.");
            }

            ScriptingImplementation scriptingBackend =
                PlayerSettings.GetScriptingBackend(NamedBuildTarget.Standalone);
            if (scriptingBackend != ScriptingImplementation.Mono2x)
            {
                throw new InvalidOperationException(
                    $"Package v1 supports the Mono Standalone scripting backend. " +
                    $"This project uses {scriptingBackend}.");
            }

            return new BuildMetadata(
                harnessPackage.version ?? "unknown",
                harnessPackage.resolvedPath,
                purrNetPackage.version,
                scriptingBackend.ToString());
        }

        private static ProjectNetworkTestManifest LoadProjectManifest(string projectPath)
        {
            ProjectNetworkTestManifestLoadResult result =
                new ProjectNetworkTestManifestLoader().LoadProject(projectPath);
            if (!result.Succeeded)
                throw new InvalidOperationException(result.Failure);

            return result.Manifest;
        }

        private static JObject LoadBuiltInContracts(string harnessPackagePath)
        {
            string contractPath = Path.Combine(
                harnessPackagePath,
                "Tools~",
                "BuiltInScenarioContracts.json");
            if (!File.Exists(contractPath))
                throw new InvalidOperationException($"Built-in scenario contract file '{contractPath}' does not exist.");

            try
            {
                using (StringReader stringReader = new StringReader(File.ReadAllText(contractPath)))
                using (JsonTextReader jsonReader = new JsonTextReader(stringReader))
                {
                    JObject root = JObject.Load(
                        jsonReader,
                        new JsonLoadSettings
                        {
                            DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
                        });
                    if (jsonReader.Read())
                        throw new InvalidOperationException("Built-in contract JSON contains content after its root object.");

                    return root;
                }
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException(
                    $"Built-in scenario contract file '{contractPath}' is invalid: {exception.Message}",
                    exception);
            }
        }

        private static List<DiscoveredScenario> DiscoverScenarios(
            ProjectNetworkTestManifest manifest,
            JObject builtInContracts)
        {
            TypeCache.TypeCollection discoveredTypes = TypeCache.GetTypesDerivedFrom<NetworkTestScenario>();
            Dictionary<string, AttributedScenarioType> attributedById =
                new Dictionary<string, AttributedScenarioType>(StringComparer.Ordinal);

            foreach (Type scenarioType in discoveredTypes)
            {
                if (scenarioType.IsAbstract || scenarioType.IsGenericTypeDefinition)
                    continue;

                NetworkTestScenarioAttribute attribute =
                    scenarioType.GetCustomAttribute<NetworkTestScenarioAttribute>(false);
                if (attribute == null)
                    continue;

                if (string.IsNullOrWhiteSpace(attribute.ScenarioId))
                    throw new InvalidOperationException($"Scenario type '{scenarioType.FullName}' has an empty scenario ID.");

                if (attributedById.ContainsKey(attribute.ScenarioId))
                    throw new InvalidOperationException($"Scenario ID '{attribute.ScenarioId}' is declared more than once.");

                attributedById.Add(
                    attribute.ScenarioId,
                    new AttributedScenarioType(scenarioType, attribute.PrefabFeatures));
            }

            Dictionary<string, ProjectNetworkTestScenario> projectScenarios =
                new Dictionary<string, ProjectNetworkTestScenario>(StringComparer.Ordinal);
            for (int i = 0; i < manifest.Scenarios.Count; i++)
                projectScenarios.Add(manifest.Scenarios[i].Id, manifest.Scenarios[i]);

            List<DiscoveredScenario> scenarios = new List<DiscoveredScenario>();
            HashSet<string> builtInIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, AttributedScenarioType> pair in attributedById)
            {
                if (pair.Key.StartsWith(BuiltInScenarioPrefix, StringComparison.Ordinal))
                {
                    if (pair.Value.ScenarioType.Assembly != typeof(NetworkTestScenario).Assembly)
                    {
                        throw new InvalidOperationException(
                            $"Scenario type '{pair.Value.ScenarioType.FullName}' uses reserved built-in ID '{pair.Key}'.");
                    }

                    JToken contractToken = builtInContracts[pair.Key];
                    if (!(contractToken is JObject contract))
                        throw new InvalidOperationException($"Built-in scenario '{pair.Key}' has no exact object contract.");

                    builtInIds.Add(pair.Key);
                    scenarios.Add(DiscoveredScenario.FromType(
                        pair.Key,
                        pair.Value.ScenarioType,
                        pair.Value.PrefabFeatures,
                        (JObject)contract.DeepClone(),
                        "BuiltInType"));
                    continue;
                }

                if (!projectScenarios.ContainsKey(pair.Key))
                {
                    throw new InvalidOperationException(
                        $"Project scenario type '{pair.Value.ScenarioType.FullName}' declares ID '{pair.Key}' but " +
                        $"is not registered in {ProjectNetworkTestManifestLoader.ManifestRelativePath}.");
                }
            }

            foreach (JProperty contractProperty in builtInContracts.Properties())
            {
                if (!builtInIds.Contains(contractProperty.Name))
                    throw new InvalidOperationException($"Built-in contract '{contractProperty.Name}' has no discovered scenario type.");
            }

            for (int i = 0; i < manifest.Scenarios.Count; i++)
            {
                ProjectNetworkTestScenario projectScenario = manifest.Scenarios[i];
                if (!projectScenario.Enabled)
                    continue;

                JObject contract = CreateContractObject(projectScenario.Contract);
                if (!string.IsNullOrWhiteSpace(projectScenario.TypeName))
                {
                    AttributedScenarioType attributed = ResolveProjectScenarioType(
                        projectScenario,
                        attributedById);
                    scenarios.Add(DiscoveredScenario.FromType(
                        projectScenario.Id,
                        attributed.ScenarioType,
                        attributed.PrefabFeatures,
                        contract,
                        "ProjectType"));
                    continue;
                }

                GameObject prefab = LoadProjectScenarioPrefab(projectScenario);
                scenarios.Add(DiscoveredScenario.FromPrefab(
                    projectScenario.Id,
                    prefab,
                    projectScenario.PrefabPath,
                    contract));
            }

            if (scenarios.Count == 0)
                throw new InvalidOperationException("No enabled network-test scenarios were discovered.");

            scenarios.Sort((left, right) => string.CompareOrdinal(left.ScenarioId, right.ScenarioId));
            return scenarios;
        }

        private static AttributedScenarioType ResolveProjectScenarioType(
            ProjectNetworkTestScenario scenario,
            IReadOnlyDictionary<string, AttributedScenarioType> attributedById)
        {
            if (!attributedById.TryGetValue(scenario.Id, out AttributedScenarioType attributed))
            {
                throw new InvalidOperationException(
                    $"Enabled project scenario '{scenario.Id}' does not resolve to a concrete " +
                    "[NetworkTestScenario] type in the current compilation.");
            }

            string simpleAssemblyQualifiedName =
                $"{attributed.ScenarioType.FullName}, {attributed.ScenarioType.Assembly.GetName().Name}";
            if (!string.Equals(scenario.TypeName, simpleAssemblyQualifiedName, StringComparison.Ordinal) &&
                !string.Equals(scenario.TypeName, attributed.ScenarioType.AssemblyQualifiedName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Project scenario '{scenario.Id}' typeName '{scenario.TypeName}' does not exactly identify " +
                    $"'{simpleAssemblyQualifiedName}'.");
            }

            return attributed;
        }

        private static GameObject LoadProjectScenarioPrefab(ProjectNetworkTestScenario scenario)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(scenario.PrefabPath);
            if (prefab == null || PrefabUtility.GetPrefabAssetType(prefab) == PrefabAssetType.NotAPrefab)
                throw new InvalidOperationException($"Project scenario '{scenario.Id}' prefab '{scenario.PrefabPath}' does not exist.");

            NetworkTestScenario[] rootScenarios = prefab.GetComponents<NetworkTestScenario>();
            NetworkTestScenario[] allScenarios = prefab.GetComponentsInChildren<NetworkTestScenario>(true);
            if (rootScenarios.Length != 1 || allScenarios.Length != 1)
            {
                throw new InvalidOperationException(
                    $"Project scenario prefab '{scenario.PrefabPath}' must contain exactly one " +
                    "NetworkTestScenario on its root and none on descendants.");
            }

            NetworkTestScenarioAttribute attribute =
                rootScenarios[0].GetType().GetCustomAttribute<NetworkTestScenarioAttribute>(false);
            if (attribute == null || !string.Equals(attribute.ScenarioId, scenario.Id, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Project scenario prefab '{scenario.PrefabPath}' root type must declare " +
                    $"[NetworkTestScenario(\"{scenario.Id}\")].");
            }

            return prefab;
        }

        private static NetworkTestScenarioRegistration[] CreateScenarioPrefabs(
            IReadOnlyList<DiscoveredScenario> scenarios)
        {
            NetworkTestScenarioRegistration[] registrations =
                new NetworkTestScenarioRegistration[scenarios.Count];

            for (int i = 0; i < scenarios.Count; i++)
            {
                DiscoveredScenario scenario = scenarios[i];
                if (scenario.AuthoredPrefab != null)
                {
                    registrations[i] = new NetworkTestScenarioRegistration(
                        scenario.ScenarioId,
                        scenario.AuthoredPrefab);
                    continue;
                }

                string safeName = GetSafeAssetName(scenario.ScenarioId);
                string prefabPath = $"{GeneratedScenarioRoot}/{safeName}.prefab";

                GameObject source = new GameObject(scenario.ScenarioType.Name);
                try
                {
                    Component component = source.AddComponent(scenario.ScenarioType);
                    if (component == null)
                        throw new InvalidOperationException($"Failed to add scenario component '{scenario.ScenarioType.FullName}'.");

                    AddRequestedPrefabFeatures(source, scenario);

                    GameObject prefab = PrefabUtility.SaveAsPrefabAsset(source, prefabPath);
                    if (prefab == null)
                        throw new InvalidOperationException($"Failed to create generated scenario prefab '{prefabPath}'.");

                    registrations[i] = new NetworkTestScenarioRegistration(scenario.ScenarioId, prefab);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(source);
                }
            }

            AssetDatabase.SaveAssets();
            return registrations;
        }

        private static void AddRequestedPrefabFeatures(GameObject source, DiscoveredScenario scenario)
        {
            NetworkTestPrefabFeatures unsupported = scenario.PrefabFeatures &
                ~NetworkTestPrefabFeatures.NetworkTransform;
            if (unsupported != NetworkTestPrefabFeatures.None)
            {
                throw new InvalidOperationException(
                    $"Scenario '{scenario.ScenarioId}' requests unsupported prefab features '{unsupported}'.");
            }

            if ((scenario.PrefabFeatures & NetworkTestPrefabFeatures.NetworkTransform) != 0)
            {
                GameObject transformTarget = new GameObject("NetworkTransform Target");
                transformTarget.transform.SetParent(source.transform, false);
                if (transformTarget.AddComponent<NetworkTransformFixtureInstaller>() == null)
                {
                    throw new InvalidOperationException(
                        $"Failed to add NetworkTransform installer for scenario '{scenario.ScenarioId}'.");
                }
            }
        }

        private static void CreateBootstrapScene(
            NetworkTestScenarioRegistration[] registrations,
            string bootstrapPrefabPath)
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObject bootstrapRoot = new GameObject("Caffeinated Network Testing");
            NetworkTestBootstrap bootstrap = bootstrapRoot.AddComponent<NetworkTestBootstrap>();

            GameObject networkRoot;
            NetworkTestNetworkRootMode networkRootMode;
            if (string.IsNullOrWhiteSpace(bootstrapPrefabPath))
            {
                networkRoot = new GameObject("PurrNet Runtime Network Root");
                networkRoot.SetActive(false);
                networkRootMode = NetworkTestNetworkRootMode.GeneratedFallback;
            }
            else
            {
                GameObject authoredPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(bootstrapPrefabPath);
                if (authoredPrefab == null || PrefabUtility.GetPrefabAssetType(authoredPrefab) == PrefabAssetType.NotAPrefab)
                    throw new InvalidOperationException($"Project bootstrap prefab '{bootstrapPrefabPath}' does not exist.");

                if (authoredPrefab.activeSelf)
                {
                    throw new InvalidOperationException(
                        $"Project bootstrap prefab '{bootstrapPrefabPath}' must be saved inactive so PurrNet cannot initialize before harness configuration.");
                }

                ValidateAuthoredBootstrapPrefab(authoredPrefab, bootstrapPrefabPath);

                networkRoot = PrefabUtility.InstantiatePrefab(authoredPrefab, scene) as GameObject;
                if (networkRoot == null)
                    throw new InvalidOperationException($"Failed to instantiate project bootstrap prefab '{bootstrapPrefabPath}'.");

                networkRootMode = NetworkTestNetworkRootMode.ProjectAuthored;
            }

            bootstrap.ConfigureForBuild(networkRoot, registrations, networkRootMode);
            EditorSceneManager.MarkSceneDirty(scene);

            if (!EditorSceneManager.SaveScene(scene, BootstrapScenePath))
                throw new InvalidOperationException($"Failed to save generated bootstrap scene '{BootstrapScenePath}'.");

            AssetDatabase.SaveAssets();

            string[] sceneDependencies = AssetDatabase.GetDependencies(BootstrapScenePath, true);
            for (int i = 0; i < registrations.Length; i++)
            {
                string prefabPath = AssetDatabase.GetAssetPath(registrations[i].Prefab);
                if (Array.IndexOf(sceneDependencies, prefabPath) < 0)
                    throw new InvalidOperationException($"Saved bootstrap scene does not depend on scenario prefab '{prefabPath}'.");
            }

            if (!string.IsNullOrWhiteSpace(bootstrapPrefabPath) &&
                Array.IndexOf(sceneDependencies, bootstrapPrefabPath) < 0)
            {
                throw new InvalidOperationException(
                    $"Saved bootstrap scene does not depend on project bootstrap prefab '{bootstrapPrefabPath}'.");
            }
        }

        internal static void ValidateAuthoredBootstrapPrefab(
            GameObject authoredPrefab,
            string bootstrapPrefabPath)
        {
            NetworkManager[] networkManagers =
                authoredPrefab.GetComponentsInChildren<NetworkManager>(true);
            UDPTransport[] udpTransports =
                authoredPrefab.GetComponentsInChildren<UDPTransport>(true);
            GenericTransport[] allTransports =
                authoredPrefab.GetComponentsInChildren<GenericTransport>(true);
            NetworkTestBootstrapHook[] hooks =
                authoredPrefab.GetComponentsInChildren<NetworkTestBootstrapHook>(true);

            if (hooks.Length > 1)
            {
                throw new InvalidOperationException(
                    $"Project bootstrap prefab '{bootstrapPrefabPath}' contains {hooks.Length} " +
                    "NetworkTestBootstrapHook components; at most one is supported.");
            }

            NetworkTestBootstrapHook hook = hooks.Length == 1 ? hooks[0] : null;
            bool containsSerializedPurrNetRoot =
                networkManagers.Length != 0 || allTransports.Length != 0;
            if (!containsSerializedPurrNetRoot)
            {
                if (hook == null || !hook.ProvisionsUdpNetworkRoot)
                {
                    throw new InvalidOperationException(
                        $"Project bootstrap prefab '{bootstrapPrefabPath}' contains no serialized PurrNet " +
                        "root and no hook-configured UDP root provisioner.");
                }

                if (hook.ConfiguredNetworkPrefabs == null || hook.ConfiguredNetworkRules == null)
                {
                    throw new InvalidOperationException(
                        $"Project bootstrap prefab '{bootstrapPrefabPath}' must serialize project " +
                        "NetworkPrefabs and NetworkRules on its provisioning hook.");
                }

                return;
            }

            if (hook != null && hook.ProvisionsUdpNetworkRoot)
            {
                throw new InvalidOperationException(
                    $"Project bootstrap prefab '{bootstrapPrefabPath}' mixes serialized PurrNet " +
                    "components with hook provisioning.");
            }

            if (networkManagers.Length != 1 ||
                udpTransports.Length != 1 ||
                allTransports.Length != 1)
            {
                throw new InvalidOperationException(
                    $"Project bootstrap prefab '{bootstrapPrefabPath}' must contain exactly one " +
                    $"NetworkManager and one UDPTransport with no other transport. Found " +
                    $"{networkManagers.Length} manager(s), {udpTransports.Length} UDP transport(s), " +
                    $"and {allTransports.Length} total transport(s).");
            }

            NetworkManager networkManager = networkManagers[0];
            if (networkManager.transport != udpTransports[0])
            {
                throw new InvalidOperationException(
                    $"Project bootstrap prefab '{bootstrapPrefabPath}' NetworkManager does not " +
                    "reference its single UDPTransport.");
            }

            if (networkManager.networkRules == null)
            {
                throw new InvalidOperationException(
                    $"Project bootstrap prefab '{bootstrapPrefabPath}' has no serialized NetworkRules.");
            }

            if (!PurrNetNetworkManagerConfigurator.TryValidateAuthoredProviderEnvelopeBeforeActivation(
                    networkManager,
                    out string providerFailure))
            {
                throw new InvalidOperationException(
                    $"Project bootstrap prefab '{bootstrapPrefabPath}' is unsupported: {providerFailure}");
            }
        }

        private static ExecutionManifestReceipt WriteExecutionManifest(
            string buildPath,
            BuildMetadata metadata,
            ProjectNetworkTestManifest projectManifest,
            IReadOnlyList<DiscoveredScenario> scenarios)
        {
            JObject scenarioContracts = new JObject();
            JObject scenarioSources = new JObject();
            List<string> builtInIds = new List<string>();

            for (int i = 0; i < scenarios.Count; i++)
            {
                DiscoveredScenario scenario = scenarios[i];
                scenarioContracts.Add(scenario.ScenarioId, scenario.Contract.DeepClone());
                scenarioSources.Add(
                    scenario.ScenarioId,
                    new JObject
                    {
                        ["kind"] = scenario.SourceKind,
                        ["source"] = scenario.SourceDescription
                    });

                if (scenario.ScenarioId.StartsWith(BuiltInScenarioPrefix, StringComparison.Ordinal))
                    builtInIds.Add(scenario.ScenarioId);
            }

            JArray suites = new JArray
            {
                new JObject
                {
                    ["name"] = "Harness.BuiltIn",
                    ["scenarios"] = new JArray(builtInIds)
                }
            };
            for (int i = 0; i < projectManifest.Suites.Count; i++)
            {
                ProjectNetworkTestSuite suite = projectManifest.Suites[i];
                suites.Add(
                    new JObject
                    {
                        ["name"] = suite.Name,
                        ["scenarios"] = new JArray(suite.ScenarioIds)
                    });
            }

            JObject executionManifest = new JObject
            {
                ["schemaVersion"] = 1,
                ["unityVersion"] = Application.unityVersion,
                ["harnessVersion"] = metadata.HarnessVersion,
                ["purrNetVersion"] = metadata.PurrNetVersion,
                ["supportedEnvelope"] = new JObject
                {
                    ["unityVersion"] = SupportedUnityVersion,
                    ["purrNetVersion"] = SupportedPurrNetVersion,
                    ["buildTarget"] = "StandaloneWindows64",
                    ["scriptingBackend"] = "Mono",
                    ["transport"] = "UDP",
                    ["roles"] = new JArray("Server", "OwnerClient", "ObserverClient")
                },
                ["scenarioContracts"] = scenarioContracts,
                ["scenarioSources"] = scenarioSources,
                ["suites"] = suites
            };

            string manifestPath = buildPath + ".manifest.json";
            NetworkTestWriteResult result =
                new NetworkTestResultWriter().Write(manifestPath, executionManifest);
            if (!result.Succeeded)
                throw new InvalidOperationException(result.Failure);

            return new ExecutionManifestReceipt(
                Path.GetFileName(manifestPath),
                ComputeSha256(manifestPath));
        }

        private static JObject CreateContractObject(ProjectNetworkTestContract contract)
        {
            return new JObject
            {
                ["schemaVersion"] = contract.SchemaVersion,
                ["stateRevision"] = contract.StateRevision,
                ["sharedFacts"] = CreatePrimitiveObject(contract.SharedFacts),
                ["roles"] = new JObject
                {
                    ["Server"] = CreateRoleContractObject(contract.Server),
                    ["OwnerClient"] = CreateRoleContractObject(contract.OwnerClient),
                    ["ObserverClient"] = CreateRoleContractObject(contract.ObserverClient)
                }
            };
        }

        private static JObject CreateRoleContractObject(ProjectNetworkTestRoleContract contract)
        {
            return new JObject
            {
                ["evidence"] = CreatePrimitiveObject(contract.Evidence),
                ["readyMilestones"] = new JArray(contract.ReadyMilestones),
                ["assertions"] = new JArray(contract.Assertions),
                ["milestones"] = new JArray(contract.Milestones)
            };
        }

        private static JObject CreatePrimitiveObject(IReadOnlyDictionary<string, object> values)
        {
            List<string> keys = new List<string>(values.Keys);
            keys.Sort(StringComparer.Ordinal);

            JObject result = new JObject();
            for (int i = 0; i < keys.Count; i++)
                result.Add(keys[i], JToken.FromObject(values[keys[i]]));

            return result;
        }

        private static AssetDependencyReceipt[] CollectInputAssetDependencies(
            string projectPath,
            ProjectNetworkTestManifest manifest,
            IReadOnlyList<DiscoveredScenario> scenarios)
        {
            List<string> roots = new List<string>();
            if (!string.IsNullOrWhiteSpace(manifest.BootstrapPrefabPath))
                roots.Add(manifest.BootstrapPrefabPath);

            for (int i = 0; i < scenarios.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(scenarios[i].AuthoredAssetPath))
                    roots.Add(scenarios[i].AuthoredAssetPath);
            }

            if (roots.Count == 0)
                return Array.Empty<AssetDependencyReceipt>();

            string[] dependencies = AssetDatabase.GetDependencies(roots.ToArray(), true);
            Array.Sort(dependencies, StringComparer.Ordinal);
            List<AssetDependencyReceipt> receipts = new List<AssetDependencyReceipt>();
            for (int i = 0; i < dependencies.Length; i++)
            {
                string assetPath = dependencies[i];
                if (!assetPath.StartsWith("Assets/", StringComparison.Ordinal) ||
                    assetPath.StartsWith(GeneratedRoot + "/", StringComparison.Ordinal))
                {
                    continue;
                }

                string physicalPath = Path.Combine(
                    projectPath,
                    assetPath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(physicalPath))
                    throw new InvalidOperationException($"Authored asset dependency '{assetPath}' has no project file.");

                string metaPath = physicalPath + ".meta";
                receipts.Add(
                    new AssetDependencyReceipt
                    {
                        AssetPath = assetPath,
                        Sha256 = ComputeSha256(physicalPath),
                        MetaSha256 = File.Exists(metaPath) ? ComputeSha256(metaPath) : null
                    });
            }

            return receipts.ToArray();
        }

        private static void WriteBuildReceipt(
            string buildPath,
            IReadOnlyList<DiscoveredScenario> scenarios,
            BuildReport report,
            BuildMetadata metadata,
            ExecutionManifestReceipt executionManifest,
            AssetDependencyReceipt[] inputAssets,
            string projectManifestSha256)
        {
            string[] scenarioIds = new string[scenarios.Count];
            for (int i = 0; i < scenarios.Count; i++)
                scenarioIds[i] = scenarios[i].ScenarioId;

            NetworkTestBuildReceipt receipt = new NetworkTestBuildReceipt
            {
                SchemaVersion = 2,
                UnityVersion = Application.unityVersion,
                HarnessVersion = metadata.HarnessVersion,
                PurrNetVersion = metadata.PurrNetVersion,
                ScenarioCount = scenarios.Count,
                ScenarioIds = scenarioIds,
                BuildSizeBytes = report.summary.totalSize,
                BuiltAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ExecutionManifestFileName = executionManifest.FileName,
                ExecutionManifestSha256 = executionManifest.Sha256,
                ProjectManifestSha256 = projectManifestSha256,
                InputAssetDependencies = inputAssets
            };

            NetworkTestWriteResult result =
                new NetworkTestResultWriter().Write(buildPath + ".build.json", receipt);
            if (!result.Succeeded)
                throw new InvalidOperationException(result.Failure);
        }

        private static string GetProjectManifestHash(string projectPath)
        {
            string manifestPath = Path.Combine(
                projectPath,
                ProjectNetworkTestManifestLoader.ManifestRelativePath.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(manifestPath) ? ComputeSha256(manifestPath) : null;
        }

        private static string ComputeSha256(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(stream);
                StringBuilder builder = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                    builder.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));

                return builder.ToString();
            }
        }

        private static string ReadRequiredArgument(IReadOnlyList<string> arguments, string key)
        {
            for (int i = 0; i < arguments.Count - 1; i++)
            {
                if (!string.Equals(arguments[i], key, StringComparison.OrdinalIgnoreCase))
                    continue;

                string value = arguments[i + 1];
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            throw new InvalidOperationException($"Required command-line argument '{key}' is missing.");
        }

        private static string GetSafeAssetName(string scenarioId)
        {
            char[] invalidCharacters = Path.GetInvalidFileNameChars();
            char[] characters = scenarioId.ToCharArray();

            for (int i = 0; i < characters.Length; i++)
            {
                if (Array.IndexOf(invalidCharacters, characters[i]) >= 0 || characters[i] == '.')
                    characters[i] = '_';
            }

            return new string(characters);
        }

        private static void CreateGeneratedFolders()
        {
            AssetDatabase.CreateFolder("Assets", "PurrNetNetworkTestGenerated");
            AssetDatabase.CreateFolder(GeneratedRoot, "Scenarios");
        }

        private static void DeleteGeneratedAssets()
        {
            if (!AssetDatabase.IsValidFolder(GeneratedRoot))
                return;

            if (!AssetDatabase.DeleteAsset(GeneratedRoot))
                throw new InvalidOperationException($"Failed to delete exact generated folder '{GeneratedRoot}'.");
        }

        private readonly struct AttributedScenarioType
        {
            public AttributedScenarioType(
                Type scenarioType,
                NetworkTestPrefabFeatures prefabFeatures)
            {
                ScenarioType = scenarioType;
                PrefabFeatures = prefabFeatures;
            }

            public Type ScenarioType { get; }
            public NetworkTestPrefabFeatures PrefabFeatures { get; }
        }

        private readonly struct DiscoveredScenario
        {
            private DiscoveredScenario(
                string scenarioId,
                Type scenarioType,
                NetworkTestPrefabFeatures prefabFeatures,
                GameObject authoredPrefab,
                string authoredAssetPath,
                JObject contract,
                string sourceKind,
                string sourceDescription)
            {
                ScenarioId = scenarioId;
                ScenarioType = scenarioType;
                PrefabFeatures = prefabFeatures;
                AuthoredPrefab = authoredPrefab;
                AuthoredAssetPath = authoredAssetPath;
                Contract = contract;
                SourceKind = sourceKind;
                SourceDescription = sourceDescription;
            }

            public string ScenarioId { get; }
            public Type ScenarioType { get; }
            public NetworkTestPrefabFeatures PrefabFeatures { get; }
            public GameObject AuthoredPrefab { get; }
            public string AuthoredAssetPath { get; }
            public JObject Contract { get; }
            public string SourceKind { get; }
            public string SourceDescription { get; }

            public static DiscoveredScenario FromType(
                string scenarioId,
                Type scenarioType,
                NetworkTestPrefabFeatures prefabFeatures,
                JObject contract,
                string sourceKind)
            {
                return new DiscoveredScenario(
                    scenarioId,
                    scenarioType,
                    prefabFeatures,
                    null,
                    null,
                    contract,
                    sourceKind,
                    $"{scenarioType.FullName}, {scenarioType.Assembly.GetName().Name}");
            }

            public static DiscoveredScenario FromPrefab(
                string scenarioId,
                GameObject prefab,
                string assetPath,
                JObject contract)
            {
                return new DiscoveredScenario(
                    scenarioId,
                    null,
                    NetworkTestPrefabFeatures.None,
                    prefab,
                    assetPath,
                    contract,
                    "ProjectPrefab",
                    assetPath);
            }
        }

        private sealed class BuildMetadata
        {
            public BuildMetadata(
                string harnessVersion,
                string harnessPackagePath,
                string purrNetVersion,
                string scriptingBackend)
            {
                HarnessVersion = harnessVersion;
                HarnessPackagePath = harnessPackagePath;
                PurrNetVersion = purrNetVersion;
                ScriptingBackend = scriptingBackend;
            }

            public string HarnessVersion { get; }
            public string HarnessPackagePath { get; }
            public string PurrNetVersion { get; }
            public string ScriptingBackend { get; }
        }

        private readonly struct ExecutionManifestReceipt
        {
            public ExecutionManifestReceipt(string fileName, string sha256)
            {
                FileName = fileName;
                Sha256 = sha256;
            }

            public string FileName { get; }
            public string Sha256 { get; }
        }

        private sealed class AssetDependencyReceipt
        {
            public string AssetPath { get; set; }
            public string Sha256 { get; set; }
            public string MetaSha256 { get; set; }
        }

        private sealed class NetworkTestBuildReceipt
        {
            public int SchemaVersion { get; set; }
            public string UnityVersion { get; set; }
            public string HarnessVersion { get; set; }
            public string PurrNetVersion { get; set; }
            public int ScenarioCount { get; set; }
            public string[] ScenarioIds { get; set; }
            public ulong BuildSizeBytes { get; set; }
            public string BuiltAtUtc { get; set; }
            public string ExecutionManifestFileName { get; set; }
            public string ExecutionManifestSha256 { get; set; }
            public string ProjectManifestSha256 { get; set; }
            public AssetDependencyReceipt[] InputAssetDependencies { get; set; }
        }
    }
}
