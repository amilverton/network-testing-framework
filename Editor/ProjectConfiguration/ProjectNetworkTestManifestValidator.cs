using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Caffeinated.NetworkTesting.Editor.ProjectConfiguration
{
    internal sealed class ProjectNetworkTestManifestValidator
    {
        public const int SupportedSchemaVersion = 1;

        public bool TryValidate(
            ProjectNetworkTestManifestDto source,
            out ProjectNetworkTestManifest manifest,
            out string failure)
        {
            manifest = null;

            if (source == null)
            {
                failure = "Manifest root cannot be null.";
                return false;
            }

            if (source.SchemaVersion != SupportedSchemaVersion)
            {
                failure =
                    $"Manifest schemaVersion must be {SupportedSchemaVersion}. Received {source.SchemaVersion}.";
                return false;
            }

            if (!TryValidateOptionalAssetPath(
                    source.BootstrapPrefabPath,
                    "bootstrapPrefabPath",
                    out failure))
            {
                return false;
            }

            if (source.Scenarios == null)
            {
                failure = "Manifest scenarios array cannot be null.";
                return false;
            }

            if (source.Suites == null)
            {
                failure = "Manifest suites array cannot be null.";
                return false;
            }

            ProjectNetworkTestScenario[] scenarios =
                new ProjectNetworkTestScenario[source.Scenarios.Length];
            Dictionary<string, bool> enabledScenarioIds =
                new Dictionary<string, bool>(StringComparer.Ordinal);

            for (int i = 0; i < source.Scenarios.Length; i++)
            {
                ProjectNetworkTestScenarioDto scenarioSource = source.Scenarios[i];
                if (!TryValidateScenario(
                        scenarioSource,
                        i,
                        enabledScenarioIds,
                        out ProjectNetworkTestScenario scenario,
                        out failure))
                {
                    return false;
                }

                scenarios[i] = scenario;
            }

            ProjectNetworkTestSuite[] suites = new ProjectNetworkTestSuite[source.Suites.Length];
            HashSet<string> suiteNames = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < source.Suites.Length; i++)
            {
                if (!TryValidateSuite(
                        source.Suites[i],
                        i,
                        suiteNames,
                        enabledScenarioIds,
                        out ProjectNetworkTestSuite suite,
                        out failure))
                {
                    return false;
                }

                suites[i] = suite;
            }

            manifest = new ProjectNetworkTestManifest(
                source.SchemaVersion,
                source.BootstrapPrefabPath,
                scenarios,
                suites);
            failure = null;
            return true;
        }

        private static bool TryValidateScenario(
            ProjectNetworkTestScenarioDto source,
            int index,
            IDictionary<string, bool> enabledScenarioIds,
            out ProjectNetworkTestScenario scenario,
            out string failure)
        {
            scenario = null;
            string path = $"scenarios[{index}]";

            if (source == null)
            {
                failure = $"{path} cannot be null.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(source.Id))
            {
                failure = $"{path}.id must be nonblank.";
                return false;
            }

            if (source.Id.StartsWith("Harness.", StringComparison.Ordinal))
            {
                failure = $"{path}.id '{source.Id}' uses the reserved Harness. prefix.";
                return false;
            }

            if (enabledScenarioIds.ContainsKey(source.Id))
            {
                failure = $"Scenario ID '{source.Id}' is declared more than once.";
                return false;
            }

            bool hasTypeName = !string.IsNullOrWhiteSpace(source.TypeName);
            bool hasPrefabPath = !string.IsNullOrWhiteSpace(source.PrefabPath);
            if (hasTypeName == hasPrefabPath)
            {
                failure =
                    $"{path} must provide exactly one nonblank typeName or prefabPath.";
                return false;
            }

            if (hasPrefabPath && !TryValidateAssetPath(source.PrefabPath, $"{path}.prefabPath", out failure))
            {
                return false;
            }

            if (!TryValidateContract(
                    source.Contract,
                    $"{path}.contract",
                    out ProjectNetworkTestContract contract,
                    out failure))
            {
                return false;
            }

            enabledScenarioIds.Add(source.Id, source.Enabled);
            scenario = new ProjectNetworkTestScenario(
                source.Id,
                source.Enabled,
                hasTypeName ? source.TypeName : null,
                hasPrefabPath ? source.PrefabPath : null,
                contract);
            failure = null;
            return true;
        }

        private static bool TryValidateContract(
            ProjectNetworkTestContractDto source,
            string path,
            out ProjectNetworkTestContract contract,
            out string failure)
        {
            contract = null;

            if (source == null)
            {
                failure = $"{path} is required.";
                return false;
            }

            if (source.SchemaVersion != SupportedSchemaVersion)
            {
                failure =
                    $"{path}.schemaVersion must be {SupportedSchemaVersion}. Received {source.SchemaVersion}.";
                return false;
            }

            if (source.StateRevision < 0)
            {
                failure = $"{path}.stateRevision cannot be negative. Received {source.StateRevision}.";
                return false;
            }

            if (!TryValidatePrimitiveMap(
                    source.SharedFacts,
                    $"{path}.sharedFacts",
                    out Dictionary<string, object> sharedFacts,
                    out failure))
            {
                return false;
            }

            if (source.Roles == null)
            {
                failure = $"{path}.roles is required.";
                return false;
            }

            if (!TryValidateRoleContract(
                    source.Roles.Server,
                    $"{path}.roles.Server",
                    out ProjectNetworkTestRoleContract server,
                    out failure))
            {
                return false;
            }

            if (!TryValidateRoleContract(
                    source.Roles.OwnerClient,
                    $"{path}.roles.OwnerClient",
                    out ProjectNetworkTestRoleContract ownerClient,
                    out failure))
            {
                return false;
            }

            if (!TryValidateRoleContract(
                    source.Roles.ObserverClient,
                    $"{path}.roles.ObserverClient",
                    out ProjectNetworkTestRoleContract observerClient,
                    out failure))
            {
                return false;
            }

            contract = new ProjectNetworkTestContract(
                source.SchemaVersion,
                source.StateRevision,
                sharedFacts,
                server,
                ownerClient,
                observerClient);
            failure = null;
            return true;
        }

        private static bool TryValidateRoleContract(
            ProjectNetworkTestRoleContractDto source,
            string path,
            out ProjectNetworkTestRoleContract roleContract,
            out string failure)
        {
            roleContract = null;

            if (source == null)
            {
                failure = $"{path} is required.";
                return false;
            }

            if (!TryValidatePrimitiveMap(
                    source.Evidence,
                    $"{path}.evidence",
                    out Dictionary<string, object> evidence,
                    out failure))
            {
                return false;
            }

            if (!TryValidateUniqueStrings(
                    source.ReadyMilestones,
                    $"{path}.readyMilestones",
                    out failure))
            {
                return false;
            }

            if (!TryValidateUniqueStrings(source.Assertions, $"{path}.assertions", out failure))
            {
                return false;
            }

            if (!TryValidateUniqueStrings(source.Milestones, $"{path}.milestones", out failure))
            {
                return false;
            }

            if (source.ReadyMilestones.Length > source.Milestones.Length)
            {
                failure = $"{path}.readyMilestones must be an exact prefix of milestones.";
                return false;
            }

            for (int i = 0; i < source.ReadyMilestones.Length; i++)
            {
                if (string.Equals(
                        source.ReadyMilestones[i],
                        source.Milestones[i],
                        StringComparison.Ordinal))
                {
                    continue;
                }

                failure =
                    $"{path}.readyMilestones must be an exact prefix of milestones. " +
                    $"Mismatch at index {i}.";
                return false;
            }

            roleContract = new ProjectNetworkTestRoleContract(
                evidence,
                source.ReadyMilestones,
                source.Assertions,
                source.Milestones);
            failure = null;
            return true;
        }

        private static bool TryValidatePrimitiveMap(
            JObject source,
            string path,
            out Dictionary<string, object> values,
            out string failure)
        {
            values = null;

            if (source == null)
            {
                failure = $"{path} is required.";
                return false;
            }

            values = new Dictionary<string, object>(StringComparer.Ordinal);
            List<JProperty> properties = new List<JProperty>(source.Properties());
            for (int i = 0; i < properties.Count; i++)
            {
                JProperty property = properties[i];
                if (string.IsNullOrWhiteSpace(property.Name))
                {
                    failure = $"{path} contains a blank key.";
                    values = null;
                    return false;
                }

                JToken token = property.Value;
                switch (token.Type)
                {
                    case JTokenType.Boolean:
                        values.Add(property.Name, token.Value<bool>());
                        break;
                    case JTokenType.String:
                        values.Add(property.Name, token.Value<string>());
                        break;
                    case JTokenType.Integer:
                        string integerText = token.ToString(Formatting.None);
                        if (!long.TryParse(
                                integerText,
                                NumberStyles.Integer,
                                CultureInfo.InvariantCulture,
                                out long integerValue) ||
                            integerValue < int.MinValue ||
                            integerValue > int.MaxValue)
                        {
                            failure =
                                $"{path}.{property.Name} integer '{integerText}' is outside the Int32 range.";
                            values = null;
                            return false;
                        }

                        values.Add(property.Name, (int)integerValue);
                        break;
                    default:
                        failure =
                            $"{path}.{property.Name} must be a Boolean, String, or Int32. " +
                            $"Received {token.Type}.";
                        values = null;
                        return false;
                }
            }

            failure = null;
            return true;
        }

        private static bool TryValidateUniqueStrings(
            string[] values,
            string path,
            out string failure)
        {
            if (values == null || values.Length == 0)
            {
                failure = $"{path} must contain at least one value.";
                return false;
            }

            HashSet<string> uniqueValues = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < values.Length; i++)
            {
                string value = values[i];
                if (string.IsNullOrWhiteSpace(value))
                {
                    failure = $"{path}[{i}] must be nonblank.";
                    return false;
                }

                if (!uniqueValues.Add(value))
                {
                    failure = $"{path} contains duplicate value '{value}'.";
                    return false;
                }
            }

            failure = null;
            return true;
        }

        private static bool TryValidateSuite(
            ProjectNetworkTestSuiteDto source,
            int index,
            ISet<string> suiteNames,
            IReadOnlyDictionary<string, bool> enabledScenarioIds,
            out ProjectNetworkTestSuite suite,
            out string failure)
        {
            suite = null;
            string path = $"suites[{index}]";

            if (source == null)
            {
                failure = $"{path} cannot be null.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(source.Name))
            {
                failure = $"{path}.name must be nonblank.";
                return false;
            }

            if (!suiteNames.Add(source.Name))
            {
                failure = $"Suite name '{source.Name}' is declared more than once.";
                return false;
            }

            if (source.ScenarioIds == null)
            {
                failure = $"{path}.scenarios cannot be null.";
                return false;
            }

            HashSet<string> suiteScenarioIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < source.ScenarioIds.Length; i++)
            {
                string scenarioId = source.ScenarioIds[i];
                if (string.IsNullOrWhiteSpace(scenarioId))
                {
                    failure = $"{path}.scenarios[{i}] must be nonblank.";
                    return false;
                }

                if (!suiteScenarioIds.Add(scenarioId))
                {
                    failure = $"{path}.scenarios contains duplicate scenario ID '{scenarioId}'.";
                    return false;
                }

                if (!enabledScenarioIds.TryGetValue(scenarioId, out bool enabled))
                {
                    failure = $"{path}.scenarios references unknown scenario ID '{scenarioId}'.";
                    return false;
                }

                if (!enabled)
                {
                    failure = $"{path}.scenarios references disabled scenario ID '{scenarioId}'.";
                    return false;
                }
            }

            suite = new ProjectNetworkTestSuite(source.Name, source.ScenarioIds);
            failure = null;
            return true;
        }

        private static bool TryValidateOptionalAssetPath(
            string assetPath,
            string path,
            out string failure)
        {
            if (assetPath == null)
            {
                failure = null;
                return true;
            }

            return TryValidateAssetPath(assetPath, path, out failure);
        }

        private static bool TryValidateAssetPath(
            string assetPath,
            string path,
            out string failure)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                failure = $"{path} must be omitted or contain a nonblank project asset path.";
                return false;
            }

            if (Path.IsPathRooted(assetPath) ||
                assetPath.IndexOf('\\') >= 0 ||
                assetPath.IndexOf(':') >= 0 ||
                !assetPath.StartsWith("Assets/", StringComparison.Ordinal))
            {
                failure = $"{path} must be a forward-slash project path under Assets/. Received '{assetPath}'.";
                return false;
            }

            string[] segments = assetPath.Split('/');
            for (int i = 0; i < segments.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(segments[i]) &&
                    !string.Equals(segments[i], ".", StringComparison.Ordinal) &&
                    !string.Equals(segments[i], "..", StringComparison.Ordinal))
                {
                    continue;
                }

                failure = $"{path} contains an invalid path segment. Received '{assetPath}'.";
                return false;
            }

            failure = null;
            return true;
        }
    }
}
