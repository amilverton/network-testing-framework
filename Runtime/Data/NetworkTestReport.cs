using System.Collections.Generic;
using Newtonsoft.Json;

namespace Caffeinated.NetworkTesting
{
    /// <summary>
    /// Contains the compact final result published by one Player process.
    /// </summary>
    public sealed class NetworkTestReport
    {
        [JsonProperty("schemaVersion")]
        public int SchemaVersion { get; set; }

        [JsonProperty("runId")]
        public string RunId { get; set; }

        [JsonProperty("scenarioId")]
        public string ScenarioId { get; set; }

        [JsonProperty("role")]
        public string Role { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("milestones")]
        public List<string> Milestones { get; set; }

        [JsonProperty("stateRevision")]
        public int StateRevision { get; set; }

        [JsonProperty("sharedFacts")]
        public Dictionary<string, object> SharedFacts { get; set; }

        [JsonProperty("roleEvidence")]
        public Dictionary<string, object> RoleEvidence { get; set; }

        [JsonProperty("assertions")]
        public List<string> Assertions { get; set; }

        [JsonProperty("failure")]
        public string Failure { get; set; }

        [JsonProperty("logPath")]
        public string LogPath { get; set; }
    }
}
