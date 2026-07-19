using System;

namespace Amilverton.PurrNetTesting
{
    /// <summary>
    /// Gives a scenario type the stable command-line identifier used by the builder and runner.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class NetworkTestScenarioAttribute : Attribute
    {
        public NetworkTestScenarioAttribute(string scenarioId)
        {
            ScenarioId = scenarioId;
        }

        public string ScenarioId { get; }
    }
}
