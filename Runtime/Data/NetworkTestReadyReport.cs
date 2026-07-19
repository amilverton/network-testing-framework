using System.Collections.Generic;
using Newtonsoft.Json;

namespace Amilverton.PurrNetTesting
{
    /// <summary>
    /// Confirms that one process reached scenario-owned readiness.
    /// </summary>
    public sealed class NetworkTestReadyReport
    {
        [JsonProperty("schemaVersion")]
        public int SchemaVersion { get; set; }

        [JsonProperty("runId")]
        public string RunId { get; set; }

        [JsonProperty("scenarioId")]
        public string ScenarioId { get; set; }

        [JsonProperty("role")]
        public string Role { get; set; }

        [JsonProperty("milestones")]
        public List<string> Milestones { get; set; }
    }
}
