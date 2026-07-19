using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Amilverton.PurrNetTesting;
using PurrNet;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Amilverton.PurrNetTesting.Editor
{
    /// <summary>
    /// Generates a test-only bootstrap scene and builds one Windows Player for all roles.
    /// </summary>
    public static class NetworkTestPlayerBuilder
    {
        private const string BuildPathArgument = "-networkTestBuildPath";
        private const string GeneratedRoot = "Assets/PurrNetNetworkTestGenerated";
        private const string GeneratedScenarioRoot = GeneratedRoot + "/Scenarios";
        private const string BootstrapScenePath = GeneratedRoot + "/NetworkTestBootstrap.unity";

        /// <summary>
        /// Command-line entry point used by Tools~/Invoke-PurrNetNetworkTests.ps1.
        /// </summary>
        public static void BuildFromCommandLine()
        {
            string buildPath = ReadRequiredArgument(Environment.GetCommandLineArgs(), BuildPathArgument);
            Build(buildPath);
        }

        /// <summary>
        /// Build a Player containing every attributed scenario type in the project.
        /// </summary>
        public static void Build(string buildPath)
        {
            if (string.IsNullOrWhiteSpace(buildPath))
                throw new ArgumentException("Build path cannot be empty.", nameof(buildPath));

            string fullBuildPath = Path.GetFullPath(buildPath);
            string buildDirectory = Path.GetDirectoryName(fullBuildPath);
            if (string.IsNullOrEmpty(buildDirectory))
                throw new InvalidOperationException($"Build path '{fullBuildPath}' has no parent directory.");

            Directory.CreateDirectory(buildDirectory);

            string previousProductName = PlayerSettings.productName;
            bool previousRunInBackground = PlayerSettings.runInBackground;

            try
            {
                DeleteGeneratedAssets();
                CreateGeneratedFolders();

                List<DiscoveredScenario> scenarios = DiscoverScenarios();
                if (scenarios.Count == 0)
                    throw new InvalidOperationException("No concrete [NetworkTestScenario] types were discovered.");

                NetworkTestScenarioRegistration[] registrations = CreateScenarioPrefabs(scenarios);
                CreateBootstrapScene(registrations);

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

                WriteBuildReceipt(fullBuildPath, scenarios.Count, report);
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

        private static List<DiscoveredScenario> DiscoverScenarios()
        {
            TypeCache.TypeCollection discoveredTypes = TypeCache.GetTypesDerivedFrom<NetworkTestScenario>();
            List<DiscoveredScenario> scenarios = new List<DiscoveredScenario>();
            HashSet<string> scenarioIds = new HashSet<string>(StringComparer.Ordinal);

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

                if (!scenarioIds.Add(attribute.ScenarioId))
                    throw new InvalidOperationException($"Scenario ID '{attribute.ScenarioId}' is declared more than once.");

                scenarios.Add(new DiscoveredScenario(attribute.ScenarioId, scenarioType));
            }

            scenarios.Sort((left, right) => string.CompareOrdinal(left.ScenarioId, right.ScenarioId));
            return scenarios;
        }

        private static NetworkTestScenarioRegistration[] CreateScenarioPrefabs(
            IReadOnlyList<DiscoveredScenario> scenarios)
        {
            NetworkTestScenarioRegistration[] registrations =
                new NetworkTestScenarioRegistration[scenarios.Count];

            for (int i = 0; i < scenarios.Count; i++)
            {
                DiscoveredScenario scenario = scenarios[i];
                string safeName = GetSafeAssetName(scenario.ScenarioId);
                string prefabPath = $"{GeneratedScenarioRoot}/{safeName}.prefab";

                GameObject source = new GameObject(scenario.ScenarioType.Name);
                try
                {
                    Component component = source.AddComponent(scenario.ScenarioType);
                    if (component == null)
                        throw new InvalidOperationException($"Failed to add scenario component '{scenario.ScenarioType.FullName}'.");

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

        private static void CreateBootstrapScene(NetworkTestScenarioRegistration[] registrations)
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObject bootstrapRoot = new GameObject("PurrNet Network Test Harness");
            NetworkTestBootstrap bootstrap = bootstrapRoot.AddComponent<NetworkTestBootstrap>();

            GameObject networkRoot = new GameObject("PurrNet Runtime Network Root");
            networkRoot.SetActive(false);

            bootstrap.ConfigureForBuild(networkRoot, registrations);
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
        }

        private static void WriteBuildReceipt(string buildPath, int scenarioCount, BuildReport report)
        {
            UnityEditor.PackageManager.PackageInfo harnessPackage =
                UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(NetworkTestPlayerBuilder).Assembly);
            UnityEditor.PackageManager.PackageInfo purrNetPackage =
                UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(NetworkManager).Assembly);

            NetworkTestBuildReceipt receipt = new NetworkTestBuildReceipt
            {
                SchemaVersion = 1,
                UnityVersion = Application.unityVersion,
                HarnessVersion = harnessPackage?.version ?? "unknown",
                PurrNetVersion = purrNetPackage?.version ?? "unknown",
                ScenarioCount = scenarioCount,
                BuildSizeBytes = report.summary.totalSize,
                BuiltAtUtc = DateTime.UtcNow.ToString("O")
            };

            NetworkTestResultWriter writer = new NetworkTestResultWriter();
            NetworkTestWriteResult result = writer.Write(buildPath + ".build.json", receipt);
            if (!result.Succeeded)
                throw new InvalidOperationException(result.Failure);
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

        private readonly struct DiscoveredScenario
        {
            public DiscoveredScenario(string scenarioId, Type scenarioType)
            {
                ScenarioId = scenarioId;
                ScenarioType = scenarioType;
            }

            public string ScenarioId { get; }
            public Type ScenarioType { get; }
        }

        private sealed class NetworkTestBuildReceipt
        {
            public int SchemaVersion { get; set; }
            public string UnityVersion { get; set; }
            public string HarnessVersion { get; set; }
            public string PurrNetVersion { get; set; }
            public int ScenarioCount { get; set; }
            public ulong BuildSizeBytes { get; set; }
            public string BuiltAtUtc { get; set; }
        }
    }
}
