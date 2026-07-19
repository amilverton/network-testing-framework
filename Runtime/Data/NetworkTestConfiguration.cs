using Newtonsoft.Json;

namespace Amilverton.PurrNetTesting
{
    /// <summary>
    /// Defines the shared endpoint and timeout for one test run.
    /// </summary>
    public sealed class NetworkTestConfiguration
    {
        [JsonProperty("schemaVersion")]
        public int SchemaVersion { get; set; }

        [JsonProperty("runId")]
        public string RunId { get; set; }

        [JsonProperty("scenarioId")]
        public string ScenarioId { get; set; }

        [JsonProperty("address")]
        public string Address { get; set; }

        [JsonProperty("port")]
        public int Port { get; set; }

        [JsonProperty("timeoutSeconds")]
        public int TimeoutSeconds { get; set; }
    }
}
