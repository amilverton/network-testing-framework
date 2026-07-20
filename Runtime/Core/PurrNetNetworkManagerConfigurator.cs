using System;
using System.Collections.Generic;
using System.Reflection;
using PurrNet;
using PurrNet.Transports;
using UnityEngine;

namespace Caffeinated.NetworkTesting
{
    /// <summary>
    /// Isolates the one version-sensitive PurrNet configuration seam used by generated Players.
    /// </summary>
    internal static class PurrNetNetworkManagerConfigurator
    {
        private const string NetworkRulesFieldName = "_networkRules";
        private const string NetworkPrefabsFieldName = "_networkPrefabs";
        private const string AddressableNetworkPrefabsFieldName = "_addressableNetworkPrefabs";

        private static readonly FieldInfo NetworkRulesField = typeof(NetworkManager).GetField(
            NetworkRulesFieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo NetworkPrefabsField = typeof(NetworkManager).GetField(
            NetworkPrefabsFieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo AddressableNetworkPrefabsField = typeof(NetworkManager).GetField(
            AddressableNetworkPrefabsFieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);

        public static bool TryApplyDefaultRules(NetworkManager networkManager, out string failure)
        {
            if (networkManager == null)
            {
                failure = "Cannot configure PurrNet rules on a null NetworkManager.";
                return false;
            }

            if (networkManager.networkRules != null)
            {
                failure = null;
                return true;
            }

            if (NetworkRulesField == null || NetworkRulesField.FieldType != typeof(NetworkRules))
            {
                failure =
                    $"PurrNet compatibility failure under Unity {Application.unityVersion}: NetworkManager field " +
                    $"'{NetworkRulesFieldName}' was not found with type NetworkRules.";
                return false;
            }

            NetworkRules rules = ScriptableObject.CreateInstance<NetworkRules>();
            rules.name = "Caffeinated Network Test Rules";
            rules.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                NetworkRulesField.SetValue(networkManager, rules);
            }
            catch (Exception exception)
            {
                failure = $"Failed to assign PurrNet runtime NetworkRules: {exception.Message}";
                UnityEngine.Object.Destroy(rules);
                return false;
            }

            if (networkManager.networkRules != rules)
            {
                failure = "PurrNet did not retain the runtime NetworkRules instance.";
                UnityEngine.Object.Destroy(rules);
                return false;
            }

            failure = null;
            return true;
        }

        public static bool TryApplyAuthoredConfiguration(
            NetworkManager networkManager,
            GenericTransport transport,
            NetworkPrefabs networkPrefabs,
            NetworkRules networkRules,
            out string failure)
        {
            if (networkManager == null || transport == null)
            {
                failure = "Cannot configure an authored PurrNet root with null runtime components.";
                return false;
            }

            if (networkPrefabs == null || networkRules == null)
            {
                failure = "Authored PurrNet root provisioning requires project NetworkPrefabs and NetworkRules assets.";
                return false;
            }

            if (NetworkPrefabsField == null || NetworkPrefabsField.FieldType != typeof(NetworkPrefabs) ||
                NetworkRulesField == null || NetworkRulesField.FieldType != typeof(NetworkRules))
            {
                failure =
                    $"PurrNet compatibility failure under Unity {Application.unityVersion}: expected " +
                    $"NetworkManager fields '{NetworkPrefabsFieldName}' and '{NetworkRulesFieldName}' were not found.";
                return false;
            }

            try
            {
                networkManager.transport = transport;
                networkManager.startServerFlags = (StartFlags)0;
                networkManager.startClientFlags = (StartFlags)0;
                NetworkPrefabsField.SetValue(networkManager, networkPrefabs);
                NetworkRulesField.SetValue(networkManager, networkRules);
            }
            catch (Exception exception)
            {
                failure = $"Failed to apply the authored PurrNet root configuration: {exception.Message}";
                return false;
            }

            object retainedNetworkPrefabs;
            try
            {
                retainedNetworkPrefabs = NetworkPrefabsField.GetValue(networkManager);
            }
            catch (Exception exception)
            {
                failure = $"Failed to verify the authored PurrNet root configuration: {exception.Message}";
                return false;
            }

            if (networkManager.transport != transport || networkManager.networkRules != networkRules ||
                retainedNetworkPrefabs != networkPrefabs)
            {
                failure = "PurrNet did not retain the authored transport, prefab catalog, and rules configuration.";
                return false;
            }

            failure = null;
            return true;
        }

        public static bool TryValidateAuthoredProviderEnvelopeBeforeActivation(
            NetworkManager networkManager,
            out string failure)
        {
            if (networkManager == null)
            {
                failure = "Cannot validate the prefab-provider envelope on a null NetworkManager.";
                return false;
            }

            if (NetworkPrefabsField == null || NetworkPrefabsField.FieldType != typeof(NetworkPrefabs))
            {
                failure =
                    $"PurrNet compatibility failure under Unity {Application.unityVersion}: NetworkManager field " +
                    $"'{NetworkPrefabsFieldName}' was not found with type NetworkPrefabs.";
                return false;
            }

            object configuredNetworkPrefabs;
            try
            {
                configuredNetworkPrefabs = NetworkPrefabsField.GetValue(networkManager);
            }
            catch (Exception exception)
            {
                failure =
                    $"Failed to inspect the PurrNet 1.19.1 project prefab-provider field: " +
                    $"{exception.Message}";
                return false;
            }

            if (configuredNetworkPrefabs == null ||
                configuredNetworkPrefabs is UnityEngine.Object networkPrefabsObject &&
                networkPrefabsObject == null)
            {
                failure =
                    "Project-authored NetworkManager must retain serialized NetworkPrefabs.";
                return false;
            }

            if (AddressableNetworkPrefabsField == null)
            {
                failure = null;
                return true;
            }

            object configuredAddressables;
            try
            {
                configuredAddressables = AddressableNetworkPrefabsField.GetValue(networkManager);
            }
            catch (Exception exception)
            {
                failure =
                    $"Failed to inspect the PurrNet 1.19.1 Addressables provider field: " +
                    $"{exception.Message}";
                return false;
            }

            if (configuredAddressables == null)
            {
                failure = null;
                return true;
            }

            if (configuredAddressables is UnityEngine.Object configuredObject && configuredObject == null)
            {
                failure = null;
                return true;
            }

            failure =
                "Project-authored Addressables prefab providers are unsupported by the PurrNet 1.19.1 " +
                "network-test runtime.";
            return false;
        }

        public static bool TryConfigurePrefabProvider(
            NetworkManager networkManager,
            NetworkTestPrefabProvider scenarioProvider,
            bool includeProjectProvider,
            out string failure)
        {
            if (networkManager == null)
            {
                failure = "Cannot configure prefabs on a null NetworkManager.";
                return false;
            }

            if (scenarioProvider == null)
            {
                failure = "Cannot configure a null scenario prefab provider.";
                return false;
            }

            IPrefabProvider projectProvider = includeProjectProvider
                ? networkManager.prefabProvider
                : null;
            if (includeProjectProvider && projectProvider == null)
            {
                failure =
                    "Project-authored NetworkManager did not initialize its serialized prefab provider.";
                return false;
            }

            if (!TryCreateConfiguredPrefabProvider(
                    projectProvider,
                    scenarioProvider,
                    out IPrefabProvider configuredProvider,
                    out failure))
            {
                return false;
            }

            try
            {
                networkManager.SetPrefabProvider(configuredProvider);
            }
            catch (Exception exception)
            {
                failure = $"PurrNet rejected the validated prefab provider: {exception.Message}";
                return false;
            }

            if (!ReferenceEquals(networkManager.prefabProvider, configuredProvider))
            {
                failure = "PurrNet did not retain the validated prefab provider.";
                return false;
            }

            failure = null;
            return true;
        }

        internal static bool TryCreateConfiguredPrefabProvider(
            IPrefabProvider projectProvider,
            IPrefabProvider scenarioProvider,
            out IPrefabProvider configuredProvider,
            out string failure)
        {
            configuredProvider = null;

            if (scenarioProvider == null)
            {
                failure = "Scenario prefab provider cannot be null.";
                return false;
            }

            if (projectProvider is CompositePrefabProvider)
            {
                failure = "Nested CompositePrefabProvider configurations are unsupported in v1.";
                return false;
            }

            if (projectProvider is IAsyncPrefabProvider)
            {
                failure = "Async or Addressables project prefab providers are unsupported in v1.";
                return false;
            }

            if (!TryReadProviderEntries(
                    scenarioProvider,
                    "scenario",
                    out List<PrefabData> scenarioEntries,
                    out int scenarioMaximumId,
                    out failure))
            {
                return false;
            }

            if (projectProvider == null)
            {
                configuredProvider = scenarioProvider;
                failure = null;
                return true;
            }

            if (!TryReadProviderEntries(
                    projectProvider,
                    "project",
                    out List<PrefabData> projectEntries,
                    out int projectMaximumId,
                    out failure))
            {
                return false;
            }

            if (!TryValidateDistinctProviderPrefabs(projectEntries, scenarioEntries, out failure))
                return false;

            long projectSpan = (long)projectMaximumId + 1L;
            long scenarioSpan = (long)scenarioMaximumId + 1L;
            if (projectSpan + scenarioSpan > int.MaxValue)
            {
                failure =
                    $"Project prefab ID span ending at {projectMaximumId} leaves no valid integer range " +
                    $"for scenario prefab ID {scenarioMaximumId}.";
                return false;
            }

            CompositePrefabProvider compositeProvider = new CompositePrefabProvider();
            try
            {
                compositeProvider.AddProvider(projectProvider);
                compositeProvider.AddProvider(scenarioProvider);
                compositeProvider.Refresh();
            }
            catch (Exception exception)
            {
                failure = $"Failed to compose validated PurrNet prefab providers: {exception.Message}";
                return false;
            }

            if (!TryVerifyProjectPrefabIds(compositeProvider, projectEntries, out failure))
                return false;

            configuredProvider = compositeProvider;
            failure = null;
            return true;
        }

        private static bool TryReadProviderEntries(
            IPrefabProvider provider,
            string providerName,
            out List<PrefabData> entries,
            out int maximumId,
            out string failure)
        {
            entries = null;
            maximumId = -1;

            try
            {
                provider.Refresh();
                IEnumerable<PrefabData> allPrefabs = provider.allPrefabs;
                if (allPrefabs == null)
                {
                    failure = $"The {providerName} prefab provider returned a null prefab sequence.";
                    return false;
                }

                entries = new List<PrefabData>(allPrefabs);
            }
            catch (Exception exception)
            {
                failure = $"Failed to refresh the {providerName} prefab provider: {exception.Message}";
                return false;
            }

            HashSet<int> prefabIds = new HashSet<int>();
            HashSet<GameObject> prefabs = new HashSet<GameObject>();

            for (int i = 0; i < entries.Count; i++)
            {
                PrefabData entry = entries[i];
                if (entry.prefabId < 0)
                {
                    failure =
                        $"The {providerName} prefab provider contains negative prefab ID {entry.prefabId}.";
                    return false;
                }

                if (!prefabIds.Add(entry.prefabId))
                {
                    failure =
                        $"The {providerName} prefab provider contains duplicate prefab ID {entry.prefabId}.";
                    return false;
                }

                if (entry.prefab == null)
                {
                    failure =
                        $"The {providerName} prefab provider contains a null prefab at ID {entry.prefabId}.";
                    return false;
                }

                if (!prefabs.Add(entry.prefab))
                {
                    failure =
                        $"The {providerName} prefab provider registers prefab '{entry.prefab.name}' more than once.";
                    return false;
                }

                if (entry.prefabId > maximumId)
                    maximumId = entry.prefabId;
            }

            failure = null;
            return true;
        }

        private static bool TryValidateDistinctProviderPrefabs(
            IReadOnlyList<PrefabData> projectEntries,
            IReadOnlyList<PrefabData> scenarioEntries,
            out string failure)
        {
            HashSet<GameObject> projectPrefabs = new HashSet<GameObject>();
            for (int i = 0; i < projectEntries.Count; i++)
                projectPrefabs.Add(projectEntries[i].prefab);

            for (int i = 0; i < scenarioEntries.Count; i++)
            {
                GameObject scenarioPrefab = scenarioEntries[i].prefab;
                if (!projectPrefabs.Contains(scenarioPrefab))
                    continue;

                failure =
                    $"Scenario prefab '{scenarioPrefab.name}' is already registered by the project provider.";
                return false;
            }

            failure = null;
            return true;
        }

        private static bool TryVerifyProjectPrefabIds(
            CompositePrefabProvider compositeProvider,
            IReadOnlyList<PrefabData> projectEntries,
            out string failure)
        {
            for (int i = 0; i < projectEntries.Count; i++)
            {
                PrefabData expected = projectEntries[i];
                if (!compositeProvider.TryGetPrefabData(expected.prefabId, out PrefabData actual))
                {
                    failure =
                        $"Composite provider did not retain project prefab ID {expected.prefabId}.";
                    return false;
                }

                if (actual.prefab != expected.prefab ||
                    actual.pooled != expected.pooled ||
                    actual.warmupCount != expected.warmupCount)
                {
                    failure =
                        $"Composite provider changed project prefab ID {expected.prefabId}.";
                    return false;
                }
            }

            failure = null;
            return true;
        }
    }
}
