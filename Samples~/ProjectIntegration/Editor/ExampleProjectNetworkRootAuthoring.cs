using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Amilverton.PurrNetTesting.Sample.ProjectIntegration.Editor
{
    /// <summary>
    /// Creates the minimal project-owned assets for a hook-provisioned PurrNet test root.
    /// </summary>
    public static class ExampleProjectNetworkRootAuthoring
    {
        private const string GeneratedFolderPath = "Assets/NetworkTests/Generated";
        private const string NetworkPrefabsPath =
            GeneratedFolderPath + "/ProjectNetworkPrefabs.asset";
        private const string NetworkRulesPath =
            GeneratedFolderPath + "/ProjectNetworkRules.asset";
        private const string BootstrapPrefabPath =
            GeneratedFolderPath + "/ProjectNetworkTestBootstrap.prefab";

        [MenuItem(
            "Tools/PurrNet Network Tests/Samples/Create Hook-Provisioned Root",
            priority = 1902)]
        public static void CreateHookProvisionedRoot()
        {
            EnsureFolder(GeneratedFolderPath);

            ExampleProjectNetworkPrefabs networkPrefabs =
                LoadOrCreateAsset<ExampleProjectNetworkPrefabs>(
                    NetworkPrefabsPath,
                    out bool networkPrefabsCreated);
            ExampleProjectNetworkRules networkRules =
                LoadOrCreateAsset<ExampleProjectNetworkRules>(NetworkRulesPath, out _);

            if (networkPrefabsCreated)
                ConfigureNewProjectCatalog(networkPrefabs);

            GameObject existingBootstrap =
                AssetDatabase.LoadAssetAtPath<GameObject>(BootstrapPrefabPath);
            if (existingBootstrap != null)
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                Selection.activeObject = existingBootstrap;
                Debug.Log(
                    $"[CreateHookProvisionedRoot] Preserved existing project bootstrap " +
                    $"'{BootstrapPrefabPath}'.");
                return;
            }

            if (AssetDatabase.LoadMainAssetAtPath(BootstrapPrefabPath) != null)
            {
                throw new InvalidOperationException(
                    $"Asset '{BootstrapPrefabPath}' exists with an incompatible type; " +
                    "it was not overwritten.");
            }

            GameObject source = new GameObject("Project Network Test Bootstrap");
            try
            {
                source.SetActive(false);
                ExampleProjectNetworkTestBootstrapHook hook =
                    source.AddComponent<ExampleProjectNetworkTestBootstrapHook>();
                hook.ConfigureUdpNetworkRootForBuild(networkPrefabs, networkRules);

                GameObject savedPrefab =
                    PrefabUtility.SaveAsPrefabAsset(source, BootstrapPrefabPath);
                if (savedPrefab == null)
                {
                    throw new InvalidOperationException(
                        $"Could not save project bootstrap prefab '{BootstrapPrefabPath}'.");
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(source);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Selection.activeObject =
                AssetDatabase.LoadAssetAtPath<GameObject>(BootstrapPrefabPath);
            Debug.Log(
                $"[CreateHookProvisionedRoot] Created inactive project bootstrap " +
                $"'{BootstrapPrefabPath}'.");
        }

        private static T LoadOrCreateAsset<T>(string path, out bool created)
            where T : ScriptableObject
        {
            T existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing != null)
            {
                created = false;
                return existing;
            }

            if (AssetDatabase.LoadMainAssetAtPath(path) != null)
            {
                throw new InvalidOperationException(
                    $"Asset '{path}' exists with an incompatible type; it was not overwritten.");
            }

            T createdAsset = ScriptableObject.CreateInstance<T>();
            createdAsset.name = Path.GetFileNameWithoutExtension(path);
            AssetDatabase.CreateAsset(createdAsset, path);
            created = true;
            return createdAsset;
        }

        private static void ConfigureNewProjectCatalog(
            ExampleProjectNetworkPrefabs networkPrefabs)
        {
            networkPrefabs.autoGenerate = false;
            networkPrefabs.networkOnly = true;
            networkPrefabs.poolByDefault = false;
            networkPrefabs.folder = null;
            networkPrefabs.linkedNetworkPrefabs = new List<PurrNet.NetworkPrefabs>();
            networkPrefabs.prefabs = new List<PurrNet.NetworkPrefabs.UserPrefabData>();
            networkPrefabs.Refresh();
            EditorUtility.SetDirty(networkPrefabs);
        }

        private static void EnsureFolder(string assetFolderPath)
        {
            string[] segments = assetFolderPath.Split('/');
            string current = segments[0];
            for (int i = 1; i < segments.Length; i++)
            {
                string next = current + "/" + segments[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    string guid = AssetDatabase.CreateFolder(current, segments[i]);
                    if (string.IsNullOrWhiteSpace(guid))
                        throw new InvalidOperationException($"Could not create asset folder '{next}'.");
                }

                current = next;
            }
        }
    }
}
