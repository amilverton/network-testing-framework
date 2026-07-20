using System;
using Amilverton.PurrNetTesting;
using ConsumerProject.Core;
using ConsumerProject.Networking;
using PurrNet;
using UnityEngine;

namespace ConsumerProject.NetworkTests
{
    /// <summary>
    /// Verifies the authored project provider before start and records synchronous cleanup.
    /// </summary>
    public sealed class ConsumerNetworkTestBootstrapHook : NetworkTestBootstrapHook
    {
        [SerializeField] private GameObject projectPrefab;

        public GameObject ProjectPrefab => projectPrefab;

        public void ConfigureForBuild(
            GameObject configuredProjectPrefab,
            NetworkPrefabs configuredNetworkPrefabs,
            NetworkRules configuredNetworkRules)
        {
            if (configuredProjectPrefab == null)
                throw new ArgumentNullException(nameof(configuredProjectPrefab));

            projectPrefab = configuredProjectPrefab;
            ConfigureUdpNetworkRootForBuild(configuredNetworkPrefabs, configuredNetworkRules);
        }

        public override void OnPreNetworkStart(
            NetworkTestBootstrap bootstrap,
            NetworkManager networkManager)
        {
            if (bootstrap == null)
                throw new InvalidOperationException("Consumer bootstrap hook received a null test bootstrap.");

            if (networkManager == null)
                throw new InvalidOperationException("Consumer bootstrap hook received a null NetworkManager.");

            if (networkManager.networkRules == null)
                throw new InvalidOperationException("Consumer NetworkManager lost its serialized NetworkRules.");

            if (projectPrefab == null)
                throw new InvalidOperationException("Consumer bootstrap hook has no serialized project prefab.");

            ConsumerProjectNetworkEntity projectEntity =
                projectPrefab.GetComponent<ConsumerProjectNetworkEntity>();
            if (projectEntity == null || projectEntity.InitialCounter != ConsumerCounterRules.InitialValue)
            {
                throw new InvalidOperationException(
                    "Consumer project prefab does not contain the expected project network entity.");
            }

            if (!(networkManager.prefabProvider is CompositePrefabProvider compositeProvider))
            {
                throw new InvalidOperationException(
                    "Consumer NetworkManager did not receive the harness composite prefab provider.");
            }

            if (!compositeProvider.TryGetPrefabData(projectPrefab, out PrefabData prefabData) ||
                prefabData.prefab != projectPrefab ||
                prefabData.prefabId != 0)
            {
                throw new InvalidOperationException(
                    "Consumer project prefab was not preserved at prefab ID 0 in the composite provider.");
            }

            if (!IsConsumerScenario(bootstrap))
                return;

            bootstrap.AddMilestone("project-prefab-provider-preserved");
            bootstrap.SetEvidence("projectPrefabPreserved", true);
            bootstrap.SetEvidence("projectPrefabId", prefabData.prefabId);
        }

        public override void OnPostNetworkStop(
            NetworkTestBootstrap bootstrap,
            NetworkManager networkManager)
        {
            if (bootstrap == null)
                throw new InvalidOperationException("Consumer cleanup hook received a null test bootstrap.");

            if (networkManager == null)
                throw new InvalidOperationException("Consumer cleanup hook received a null NetworkManager.");

            if (!IsConsumerScenario(bootstrap))
                return;

            bootstrap.AddMilestone("project-bootstrap-hook-stopped");
            bootstrap.SetEvidence("projectHookStopped", true);
        }

        private static bool IsConsumerScenario(NetworkTestBootstrap bootstrap)
        {
            return bootstrap.ScenarioId != null &&
                   bootstrap.ScenarioId.StartsWith("Consumer.", StringComparison.Ordinal);
        }
    }
}
