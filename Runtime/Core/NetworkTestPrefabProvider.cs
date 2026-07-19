using System.Collections.Generic;
using PurrNet;
using UnityEngine;

namespace Amilverton.PurrNetTesting
{
    /// <summary>
    /// Provides generated scenario prefabs to PurrNet without a project-owned ScriptableObject asset.
    /// </summary>
    internal sealed class NetworkTestPrefabProvider : IPrefabProvider
    {
        private readonly List<PrefabData> _prefabs;

        public NetworkTestPrefabProvider(IReadOnlyList<NetworkTestScenarioRegistration> registrations)
        {
            _prefabs = new List<PrefabData>(registrations.Count);

            for (int i = 0; i < registrations.Count; i++)
            {
                _prefabs.Add(new PrefabData
                {
                    prefabId = i,
                    prefab = registrations[i].Prefab,
                    pooled = false,
                    warmupCount = 0
                });
            }
        }

        public IEnumerable<PrefabData> allPrefabs => _prefabs;

        public bool TryGetPrefabData(int prefabId, out PrefabData prefabData)
        {
            if (prefabId >= 0 && prefabId < _prefabs.Count)
            {
                prefabData = _prefabs[prefabId];
                return prefabData.prefab != null;
            }

            prefabData = default;
            return false;
        }

        public bool TryGetPrefabData(GameObject prefab, out PrefabData prefabData)
        {
            for (int i = 0; i < _prefabs.Count; i++)
            {
                PrefabData candidate = _prefabs[i];
                if (candidate.prefab != prefab)
                    continue;

                prefabData = candidate;
                return true;
            }

            prefabData = default;
            return false;
        }

        public void Refresh()
        {
        }
    }
}
