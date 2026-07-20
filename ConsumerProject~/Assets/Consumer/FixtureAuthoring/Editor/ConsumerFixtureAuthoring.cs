using System;
using System.Collections.Generic;
using Caffeinated.NetworkTesting;
using ConsumerProject.Networking;
using ConsumerProject.NetworkTests;
using PurrNet;
using PurrNet.Transports;
using UnityEditor;
using UnityEngine;

namespace ConsumerProject.FixtureAuthoring.Editor
{
    /// <summary>
    /// Creates the exact authored assets used by the clean consumer portability fixture.
    /// </summary>
    public static class ConsumerFixtureAuthoring
    {
        public const string BootstrapPrefabPath =
            "Assets/Consumer/Generated/ConsumerNetworkTestBootstrap.prefab";

        public const string ProjectPrefabPath =
            "Assets/Consumer/Generated/ConsumerProjectNetworkEntity.prefab";

        public const string NetworkPrefabsPath =
            "Assets/Consumer/Generated/ConsumerProjectNetworkPrefabs.asset";

        public const string NetworkRulesPath =
            "Assets/Consumer/Generated/ConsumerNetworkRules.asset";

        private const string GeneratedFolderPath = "Assets/Consumer/Generated";
        /// <summary>
        /// Idempotently create or update the authored bootstrap and project-provider assets.
        /// </summary>
        [MenuItem("Tools/Consumer Portability/Create Fixture Assets", priority = 1901)]
        public static void CreateFixtureAssets()
        {
            EnsureGeneratedFolder();

            GameObject projectPrefab = CreateProjectPrefab();
            ConsumerProjectNetworkRules networkRules =
                LoadOrCreateOwnedAsset<ConsumerProjectNetworkRules>(NetworkRulesPath);
            ConsumerProjectNetworkPrefabs networkPrefabs =
                LoadOrCreateOwnedAsset<ConsumerProjectNetworkPrefabs>(NetworkPrefabsPath);
            ConfigureNetworkPrefabs(networkPrefabs, projectPrefab);
            CreateBootstrapPrefab(
                projectPrefab,
                networkPrefabs,
                networkRules);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            ValidateGeneratedAssets(projectPrefab, networkPrefabs, networkRules);

            Debug.Log(
                $"[CreateFixtureAssets] Authored consumer portability fixture at " +
                $"'{BootstrapPrefabPath}' with project prefab '{ProjectPrefabPath}'.");
        }

        private static void EnsureGeneratedFolder()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Consumer"))
            {
                throw new InvalidOperationException(
                    "[CreateFixtureAssets] Expected source folder 'Assets/Consumer' was not imported.");
            }

            if (AssetDatabase.IsValidFolder(GeneratedFolderPath))
                return;

            string guid = AssetDatabase.CreateFolder("Assets/Consumer", "Generated");
            if (string.IsNullOrWhiteSpace(guid) ||
                !AssetDatabase.IsValidFolder(GeneratedFolderPath))
            {
                throw new InvalidOperationException(
                    $"[CreateFixtureAssets] Failed to create '{GeneratedFolderPath}'.");
            }
        }

        private static GameObject CreateProjectPrefab()
        {
            GameObject source = new GameObject("Consumer Project Network Entity");
            try
            {
                ConsumerProjectNetworkEntity entity =
                    source.AddComponent<ConsumerProjectNetworkEntity>();
                if (entity == null)
                {
                    throw new InvalidOperationException(
                        "[CreateFixtureAssets] Failed to add ConsumerProjectNetworkEntity.");
                }

                GameObject projectPrefab = PrefabUtility.SaveAsPrefabAsset(source, ProjectPrefabPath);
                if (projectPrefab == null)
                {
                    throw new InvalidOperationException(
                        $"[CreateFixtureAssets] Failed to save '{ProjectPrefabPath}'.");
                }

                return projectPrefab;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(source);
            }
        }

        private static T LoadOrCreateOwnedAsset<T>(string assetPath) where T : ScriptableObject
        {
            T existingAsset = AssetDatabase.LoadAssetAtPath<T>(assetPath);
            if (existingAsset != null)
                return existingAsset;

            if (System.IO.File.Exists(System.IO.Path.GetFullPath(assetPath)) &&
                !AssetDatabase.DeleteAsset(assetPath))
            {
                throw new InvalidOperationException(
                    $"[CreateFixtureAssets] Invalid owned asset '{assetPath}' could not be regenerated.");
            }

            T createdAsset = ScriptableObject.CreateInstance<T>();
            createdAsset.name = System.IO.Path.GetFileNameWithoutExtension(assetPath);
            AssetDatabase.CreateAsset(createdAsset, assetPath);
            return createdAsset;
        }

        private static void ConfigureNetworkPrefabs(
            NetworkPrefabs networkPrefabs,
            GameObject projectPrefab)
        {
            string projectPrefabGuid = AssetDatabase.AssetPathToGUID(ProjectPrefabPath);
            if (string.IsNullOrWhiteSpace(projectPrefabGuid))
            {
                throw new InvalidOperationException(
                    $"[CreateFixtureAssets] Project prefab '{ProjectPrefabPath}' has no asset GUID.");
            }

            networkPrefabs.autoGenerate = false;
            networkPrefabs.networkOnly = true;
            networkPrefabs.poolByDefault = false;
            networkPrefabs.folder = null;
            networkPrefabs.linkedNetworkPrefabs = new List<NetworkPrefabs>();
            networkPrefabs.prefabs = new List<NetworkPrefabs.UserPrefabData>
            {
                new NetworkPrefabs.UserPrefabData
                {
                    guid = projectPrefabGuid,
                    prefab = projectPrefab,
                    pooled = false,
                    warmupCount = 0
                }
            };
            networkPrefabs.Refresh();
            EditorUtility.SetDirty(networkPrefabs);
        }

        private static void CreateBootstrapPrefab(
            GameObject projectPrefab,
            NetworkPrefabs networkPrefabs,
            NetworkRules networkRules)
        {
            GameObject source = new GameObject("Consumer Network Test Bootstrap");

            try
            {
                source.SetActive(false);

                ConsumerNetworkTestBootstrapHook hook =
                    source.AddComponent<ConsumerNetworkTestBootstrapHook>();

                if (hook == null)
                {
                    throw new InvalidOperationException(
                        "[CreateFixtureAssets] Could not add the project bootstrap hook.");
                }

                hook.ConfigureForBuild(projectPrefab, networkPrefabs, networkRules);

                GameObject bootstrapPrefab =
                    PrefabUtility.SaveAsPrefabAsset(source, BootstrapPrefabPath);
                if (bootstrapPrefab == null)
                {
                    throw new InvalidOperationException(
                        $"[CreateFixtureAssets] Failed to save '{BootstrapPrefabPath}'.");
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(source);
            }
        }

        private static void ValidateGeneratedAssets(
            GameObject expectedProjectPrefab,
            NetworkPrefabs expectedNetworkPrefabs,
            NetworkRules expectedNetworkRules)
        {
            GameObject bootstrapPrefab =
                AssetDatabase.LoadAssetAtPath<GameObject>(BootstrapPrefabPath);
            if (bootstrapPrefab == null || bootstrapPrefab.activeSelf)
            {
                throw new InvalidOperationException(
                    "[CreateFixtureAssets] Saved bootstrap prefab must exist and remain inactive.");
            }

            NetworkManager[] networkManagers =
                bootstrapPrefab.GetComponentsInChildren<NetworkManager>(true);
            UDPTransport[] udpTransports =
                bootstrapPrefab.GetComponentsInChildren<UDPTransport>(true);
            GenericTransport[] allTransports =
                bootstrapPrefab.GetComponentsInChildren<GenericTransport>(true);
            ConsumerNetworkTestBootstrapHook[] hooks =
                bootstrapPrefab.GetComponentsInChildren<ConsumerNetworkTestBootstrapHook>(true);

            if (networkManagers.Length != 0 ||
                udpTransports.Length != 0 ||
                allTransports.Length != 0 ||
                hooks.Length != 1)
            {
                throw new InvalidOperationException(
                    $"[CreateFixtureAssets] Hook-provisioned bootstrap envelope is invalid: " +
                    $"NetworkManager={networkManagers.Length}, UDPTransport={udpTransports.Length}, " +
                    $"all transports={allTransports.Length}, hooks={hooks.Length}.");
            }

            ConsumerNetworkTestBootstrapHook hook = hooks[0];
            if (!hook.ProvisionsUdpNetworkRoot ||
                hook.ConfiguredNetworkPrefabs != expectedNetworkPrefabs ||
                hook.ConfiguredNetworkRules != expectedNetworkRules ||
                hook.ProjectPrefab != expectedProjectPrefab)
            {
                throw new InvalidOperationException(
                    "[CreateFixtureAssets] Bootstrap hook provisioning references were not retained.");
            }

            ConsumerProjectNetworkEntity projectEntity =
                expectedProjectPrefab.GetComponent<ConsumerProjectNetworkEntity>();
            if (projectEntity == null ||
                expectedProjectPrefab.GetComponent<NetworkTestScenario>() != null)
            {
                throw new InvalidOperationException(
                    "[CreateFixtureAssets] Project provider prefab must contain one project entity and no test scenario.");
            }

            expectedNetworkPrefabs.Refresh();
            if (!expectedNetworkPrefabs.TryGetPrefabData(
                    expectedProjectPrefab,
                    out PrefabData prefabData) ||
                prefabData.prefabId != 0 ||
                prefabData.prefab != expectedProjectPrefab)
            {
                throw new InvalidOperationException(
                    "[CreateFixtureAssets] Project prefab provider did not retain its prefab at ID 0.");
            }
        }
    }
}
