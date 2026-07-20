using System;

namespace Caffeinated.NetworkTesting
{
    [Flags]
    public enum NetworkTestPrefabFeatures
    {
        None = 0,
        NetworkTransform = 1
    }

    /// <summary>
    /// Gives a scenario type the stable command-line identifier used by the builder and runner.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class NetworkTestScenarioAttribute : Attribute
    {
        public NetworkTestScenarioAttribute(
            string scenarioId,
            NetworkTestPrefabFeatures prefabFeatures = NetworkTestPrefabFeatures.None)
        {
            ScenarioId = scenarioId;
            PrefabFeatures = prefabFeatures;
        }

        public string ScenarioId { get; }
        public NetworkTestPrefabFeatures PrefabFeatures { get; }
    }
}
