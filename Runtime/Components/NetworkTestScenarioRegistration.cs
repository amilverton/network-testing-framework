using System;
using UnityEngine;

namespace Caffeinated.NetworkTesting
{
    /// <summary>
    /// Maps one stable scenario identifier to its generated network prefab.
    /// </summary>
    [Serializable]
    public struct NetworkTestScenarioRegistration
    {
        [SerializeField] private string scenarioId;
        [SerializeField] private GameObject prefab;

        public NetworkTestScenarioRegistration(string scenarioId, GameObject prefab)
        {
            this.scenarioId = scenarioId;
            this.prefab = prefab;
        }

        public string ScenarioId => scenarioId;
        public GameObject Prefab => prefab;
    }
}
