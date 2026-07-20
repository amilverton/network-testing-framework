using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Amilverton.PurrNetTesting.Editor.ProjectConfiguration
{
    /// <summary>
    /// Validated project-owned network-test configuration.
    /// </summary>
    public sealed class ProjectNetworkTestManifest
    {
        private readonly ReadOnlyCollection<ProjectNetworkTestScenario> _scenarios;
        private readonly ReadOnlyCollection<ProjectNetworkTestSuite> _suites;

        internal ProjectNetworkTestManifest(
            int schemaVersion,
            string bootstrapPrefabPath,
            ProjectNetworkTestScenario[] scenarios,
            ProjectNetworkTestSuite[] suites)
        {
            SchemaVersion = schemaVersion;
            BootstrapPrefabPath = bootstrapPrefabPath;
            _scenarios = Array.AsReadOnly(
                scenarios == null
                    ? Array.Empty<ProjectNetworkTestScenario>()
                    : (ProjectNetworkTestScenario[])scenarios.Clone());
            _suites = Array.AsReadOnly(
                suites == null
                    ? Array.Empty<ProjectNetworkTestSuite>()
                    : (ProjectNetworkTestSuite[])suites.Clone());
        }

        public int SchemaVersion { get; }
        public string BootstrapPrefabPath { get; }
        public IReadOnlyList<ProjectNetworkTestScenario> Scenarios => _scenarios;
        public IReadOnlyList<ProjectNetworkTestSuite> Suites => _suites;

        internal static ProjectNetworkTestManifest CreateEmpty()
        {
            return new ProjectNetworkTestManifest(
                ProjectNetworkTestManifestValidator.SupportedSchemaVersion,
                null,
                Array.Empty<ProjectNetworkTestScenario>(),
                Array.Empty<ProjectNetworkTestSuite>());
        }
    }

    /// <summary>
    /// One validated project scenario and its exact result contract.
    /// </summary>
    public sealed class ProjectNetworkTestScenario
    {
        internal ProjectNetworkTestScenario(
            string id,
            bool enabled,
            string typeName,
            string prefabPath,
            ProjectNetworkTestContract contract)
        {
            Id = id;
            Enabled = enabled;
            TypeName = typeName;
            PrefabPath = prefabPath;
            Contract = contract;
        }

        public string Id { get; }
        public bool Enabled { get; }
        public string TypeName { get; }
        public string PrefabPath { get; }
        public ProjectNetworkTestContract Contract { get; }
    }

    /// <summary>
    /// Exact shared and role-owned expectations for one project scenario.
    /// </summary>
    public sealed class ProjectNetworkTestContract
    {
        private readonly ReadOnlyDictionary<string, object> _sharedFacts;

        internal ProjectNetworkTestContract(
            int schemaVersion,
            int stateRevision,
            IDictionary<string, object> sharedFacts,
            ProjectNetworkTestRoleContract server,
            ProjectNetworkTestRoleContract ownerClient,
            ProjectNetworkTestRoleContract observerClient)
        {
            SchemaVersion = schemaVersion;
            StateRevision = stateRevision;
            _sharedFacts = new ReadOnlyDictionary<string, object>(
                new Dictionary<string, object>(sharedFacts, StringComparer.Ordinal));
            Server = server;
            OwnerClient = ownerClient;
            ObserverClient = observerClient;
        }

        public int SchemaVersion { get; }
        public int StateRevision { get; }
        public IReadOnlyDictionary<string, object> SharedFacts => _sharedFacts;
        public ProjectNetworkTestRoleContract Server { get; }
        public ProjectNetworkTestRoleContract OwnerClient { get; }
        public ProjectNetworkTestRoleContract ObserverClient { get; }
    }

    /// <summary>
    /// Exact evidence and ordered assertions expected from one process role.
    /// </summary>
    public sealed class ProjectNetworkTestRoleContract
    {
        private readonly ReadOnlyDictionary<string, object> _evidence;
        private readonly ReadOnlyCollection<string> _readyMilestones;
        private readonly ReadOnlyCollection<string> _assertions;
        private readonly ReadOnlyCollection<string> _milestones;

        internal ProjectNetworkTestRoleContract(
            IDictionary<string, object> evidence,
            string[] readyMilestones,
            string[] assertions,
            string[] milestones)
        {
            _evidence = new ReadOnlyDictionary<string, object>(
                new Dictionary<string, object>(evidence, StringComparer.Ordinal));
            _readyMilestones = Array.AsReadOnly((string[])readyMilestones.Clone());
            _assertions = Array.AsReadOnly((string[])assertions.Clone());
            _milestones = Array.AsReadOnly((string[])milestones.Clone());
        }

        public IReadOnlyDictionary<string, object> Evidence => _evidence;
        public IReadOnlyList<string> ReadyMilestones => _readyMilestones;
        public IReadOnlyList<string> Assertions => _assertions;
        public IReadOnlyList<string> Milestones => _milestones;
    }

    /// <summary>
    /// One named, ordered set of enabled project scenario IDs.
    /// </summary>
    public sealed class ProjectNetworkTestSuite
    {
        private readonly ReadOnlyCollection<string> _scenarioIds;

        internal ProjectNetworkTestSuite(string name, string[] scenarioIds)
        {
            Name = name;
            _scenarioIds = Array.AsReadOnly((string[])scenarioIds.Clone());
        }

        public string Name { get; }
        public IReadOnlyList<string> ScenarioIds => _scenarioIds;
    }
}
