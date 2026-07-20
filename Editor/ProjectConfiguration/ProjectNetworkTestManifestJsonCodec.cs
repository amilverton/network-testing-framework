using System;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Amilverton.PurrNetTesting.Editor.ProjectConfiguration
{
    internal sealed class ProjectNetworkTestManifestJsonCodec
    {
        public bool TryDeserialize(
            string json,
            out ProjectNetworkTestManifestDto manifest,
            out string failure)
        {
            manifest = null;

            if (string.IsNullOrWhiteSpace(json))
            {
                failure = "Manifest JSON cannot be empty.";
                return false;
            }

            try
            {
                JObject root;
                using (StringReader stringReader = new StringReader(json))
                using (JsonTextReader jsonReader = new JsonTextReader(stringReader))
                {
                    jsonReader.DateParseHandling = DateParseHandling.None;
                    jsonReader.FloatParseHandling = FloatParseHandling.Decimal;
                    root = JObject.Load(
                        jsonReader,
                        new JsonLoadSettings
                        {
                            DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
                        });

                    if (jsonReader.Read())
                    {
                        failure = "Manifest JSON contains content after the root object.";
                        return false;
                    }
                }

                JsonSerializer serializer = JsonSerializer.Create(
                    new JsonSerializerSettings
                    {
                        Culture = CultureInfo.InvariantCulture,
                        DateParseHandling = DateParseHandling.None,
                        FloatParseHandling = FloatParseHandling.Decimal,
                        MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
                        MissingMemberHandling = MissingMemberHandling.Error,
                        TypeNameHandling = TypeNameHandling.None
                    });

                manifest = root.ToObject<ProjectNetworkTestManifestDto>(serializer);
                if (manifest == null)
                {
                    failure = "Manifest JSON did not produce a root object.";
                    return false;
                }

                failure = null;
                return true;
            }
            catch (JsonException exception)
            {
                failure = $"Manifest JSON is invalid: {exception.Message}";
                return false;
            }
        }
    }

    [JsonObject(MemberSerialization.OptIn)]
    internal sealed class ProjectNetworkTestManifestDto
    {
        [JsonProperty("schemaVersion", Required = Required.Always)]
        public int SchemaVersion { get; set; }

        [JsonProperty("bootstrapPrefabPath")]
        public string BootstrapPrefabPath { get; set; }

        [JsonProperty("scenarios", Required = Required.Always)]
        public ProjectNetworkTestScenarioDto[] Scenarios { get; set; }

        [JsonProperty("suites", Required = Required.Always)]
        public ProjectNetworkTestSuiteDto[] Suites { get; set; }
    }

    [JsonObject(MemberSerialization.OptIn)]
    internal sealed class ProjectNetworkTestScenarioDto
    {
        [JsonProperty("id", Required = Required.Always)]
        public string Id { get; set; }

        [JsonProperty("enabled", Required = Required.Always)]
        public bool Enabled { get; set; }

        [JsonProperty("typeName")]
        public string TypeName { get; set; }

        [JsonProperty("prefabPath")]
        public string PrefabPath { get; set; }

        [JsonProperty("contract", Required = Required.Always)]
        public ProjectNetworkTestContractDto Contract { get; set; }
    }

    [JsonObject(MemberSerialization.OptIn)]
    internal sealed class ProjectNetworkTestContractDto
    {
        [JsonProperty("schemaVersion", Required = Required.Always)]
        public int SchemaVersion { get; set; }

        [JsonProperty("stateRevision", Required = Required.Always)]
        public int StateRevision { get; set; }

        [JsonProperty("sharedFacts", Required = Required.Always)]
        public JObject SharedFacts { get; set; }

        [JsonProperty("roles", Required = Required.Always)]
        public ProjectNetworkTestRolesDto Roles { get; set; }
    }

    [JsonObject(MemberSerialization.OptIn)]
    internal sealed class ProjectNetworkTestRolesDto
    {
        [JsonProperty("Server", Required = Required.Always)]
        public ProjectNetworkTestRoleContractDto Server { get; set; }

        [JsonProperty("OwnerClient", Required = Required.Always)]
        public ProjectNetworkTestRoleContractDto OwnerClient { get; set; }

        [JsonProperty("ObserverClient", Required = Required.Always)]
        public ProjectNetworkTestRoleContractDto ObserverClient { get; set; }
    }

    [JsonObject(MemberSerialization.OptIn)]
    internal sealed class ProjectNetworkTestRoleContractDto
    {
        [JsonProperty("evidence", Required = Required.Always)]
        public JObject Evidence { get; set; }

        [JsonProperty("readyMilestones", Required = Required.Always)]
        public string[] ReadyMilestones { get; set; }

        [JsonProperty("assertions", Required = Required.Always)]
        public string[] Assertions { get; set; }

        [JsonProperty("milestones", Required = Required.Always)]
        public string[] Milestones { get; set; }
    }

    [JsonObject(MemberSerialization.OptIn)]
    internal sealed class ProjectNetworkTestSuiteDto
    {
        [JsonProperty("name", Required = Required.Always)]
        public string Name { get; set; }

        [JsonProperty("scenarios", Required = Required.Always)]
        public string[] ScenarioIds { get; set; }
    }
}
