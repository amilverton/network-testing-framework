using System;
using System.IO;
using Amilverton.PurrNetTesting.Editor.ProjectConfiguration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Amilverton.PurrNetTesting.Tests
{
    public sealed class ProjectNetworkTestManifestLoaderTests
    {
        private string _projectDirectory;

        [SetUp]
        public void SetUp()
        {
            _projectDirectory = Path.Combine(
                Path.GetTempPath(),
                "PurrNetProjectManifest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_projectDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_projectDirectory))
            {
                Directory.Delete(_projectDirectory, true);
            }
        }

        [Test]
        public void LoadProject_WithValidManifest_ReturnsValidatedDomainModel()
        {
            WriteManifest(CreateValidManifest().ToString(Formatting.None));

            ProjectNetworkTestManifestLoadResult result = LoadProject();

            Assert.That(result.Succeeded, Is.True, result.Failure);
            Assert.That(result.ManifestFound, Is.True);
            Assert.That(result.Manifest.SchemaVersion, Is.EqualTo(1));
            Assert.That(
                result.Manifest.BootstrapPrefabPath,
                Is.EqualTo("Assets/NetworkTests/Bootstrap.prefab"));
            Assert.That(result.Manifest.Scenarios, Has.Count.EqualTo(1));
            Assert.That(result.Manifest.Scenarios[0].Id, Is.EqualTo("Game.Damage"));
            Assert.That(result.Manifest.Scenarios[0].TypeName, Is.Null);
            Assert.That(
                result.Manifest.Scenarios[0].PrefabPath,
                Is.EqualTo("Assets/NetworkTests/Damage.prefab"));
            Assert.That(
                result.Manifest.Scenarios[0].Contract.SharedFacts["health"],
                Is.EqualTo(75));
            Assert.That(
                result.Manifest.Scenarios[0].Contract.Server.ReadyMilestones,
                Is.EqualTo(new[] { "server-listening", "fixture-spawned" }));
            Assert.That(result.Manifest.Suites, Has.Count.EqualTo(1));
            Assert.That(result.Manifest.Suites[0].ScenarioIds, Is.EqualTo(new[] { "Game.Damage" }));
        }

        [Test]
        public void LoadProject_WhenManifestIsMissing_ReturnsValidEmptyManifestWithoutCreatingFiles()
        {
            ProjectNetworkTestManifestLoadResult result = LoadProject();

            Assert.That(result.Succeeded, Is.True, result.Failure);
            Assert.That(result.ManifestFound, Is.False);
            Assert.That(result.Manifest.SchemaVersion, Is.EqualTo(1));
            Assert.That(result.Manifest.Scenarios, Is.Empty);
            Assert.That(result.Manifest.Suites, Is.Empty);
            Assert.That(
                Directory.Exists(Path.Combine(_projectDirectory, "ProjectSettings")),
                Is.False);
        }

        [Test]
        public void LoadProject_WithUnknownField_FailsClosed()
        {
            JObject manifest = CreateValidManifest();
            manifest["unexpected"] = true;

            AssertManifestFails(manifest, "unexpected");
        }

        [Test]
        public void LoadProject_WithDuplicateJsonKey_FailsClosed()
        {
            string json = CreateValidManifest().ToString(Formatting.None);
            string duplicateJson = json.Replace(
                "\"schemaVersion\":1",
                "\"schemaVersion\":1,\"schemaVersion\":1");
            WriteManifest(duplicateJson);

            ProjectNetworkTestManifestLoadResult result = LoadProject();

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Failure, Does.Contain("schemaVersion"));
            Assert.That(result.Failure, Does.Contain("parse"));
        }

        [Test]
        public void LoadProject_WithUnsupportedSchema_FailsClosed()
        {
            JObject manifest = CreateValidManifest();
            manifest["schemaVersion"] = 2;

            AssertManifestFails(manifest, "schemaVersion must be 1");
        }

        [Test]
        public void LoadProject_WithBothScenarioSources_FailsClosed()
        {
            JObject manifest = CreateValidManifest();
            Scenario(manifest)["typeName"] = "Game.Network.DamageScenario";

            AssertManifestFails(manifest, "exactly one nonblank typeName or prefabPath");
        }

        [Test]
        public void LoadProject_WithNoScenarioSource_FailsClosed()
        {
            JObject manifest = CreateValidManifest();
            Scenario(manifest).Property("prefabPath").Remove();

            AssertManifestFails(manifest, "exactly one nonblank typeName or prefabPath");
        }

        [Test]
        public void LoadProject_WithMissingContract_FailsClosed()
        {
            JObject manifest = CreateValidManifest();
            Scenario(manifest).Property("contract").Remove();

            AssertManifestFails(manifest, "contract");
        }

        [Test]
        public void LoadProject_WithMissingRole_FailsClosed()
        {
            JObject manifest = CreateValidManifest();
            Roles(manifest).Property("ObserverClient").Remove();

            AssertManifestFails(manifest, "ObserverClient");
        }

        [Test]
        public void LoadProject_WithCompositeSharedFact_FailsClosed()
        {
            JObject manifest = CreateValidManifest();
            SharedFacts(manifest)["nested"] = new JObject { ["value"] = 1 };

            AssertManifestFails(manifest, "Boolean, String, or Int32");
        }

        [Test]
        public void LoadProject_WithInt64SharedFact_FailsClosed()
        {
            JObject manifest = CreateValidManifest();
            SharedFacts(manifest)["oversized"] = (long)int.MaxValue + 1L;

            AssertManifestFails(manifest, "outside the Int32 range");
        }

        [Test]
        public void LoadProject_WhenReadyMilestonesAreNotMilestonePrefix_FailsClosed()
        {
            JObject manifest = CreateValidManifest();
            Role(manifest, "Server")["readyMilestones"] = new JArray(
                "server-listening",
                "not-the-second-milestone");

            AssertManifestFails(manifest, "exact prefix of milestones");
        }

        [Test]
        public void LoadProject_WithReservedHarnessScenarioId_FailsClosed()
        {
            JObject manifest = CreateValidManifest();
            Scenario(manifest)["id"] = "Harness.ProjectScenario";
            SuiteScenarios(manifest)[0] = "Harness.ProjectScenario";

            AssertManifestFails(manifest, "reserved Harness. prefix");
        }

        [Test]
        public void LoadProject_WithDuplicateSuiteReference_FailsClosed()
        {
            JObject manifest = CreateValidManifest();
            SuiteScenarios(manifest).Add("Game.Damage");

            AssertManifestFails(manifest, "duplicate scenario ID 'Game.Damage'");
        }

        [Test]
        public void LoadProject_WithUnknownSuiteMember_FailsClosed()
        {
            JObject manifest = CreateValidManifest();
            SuiteScenarios(manifest)[0] = "Game.Unknown";

            AssertManifestFails(manifest, "unknown scenario ID 'Game.Unknown'");
        }

        [Test]
        public void LoadProject_WithDisabledSuiteMember_FailsClosed()
        {
            JObject manifest = CreateValidManifest();
            Scenario(manifest)["enabled"] = false;

            AssertManifestFails(manifest, "disabled scenario ID 'Game.Damage'");
        }

        [TestCase("C:/External/Damage.prefab", "under Assets/")]
        [TestCase("Library/Generated/Damage.prefab", "under Assets/")]
        [TestCase("Assets/NetworkTests/../Damage.prefab", "invalid path segment")]
        [TestCase("Assets\\NetworkTests\\Damage.prefab", "under Assets/")]
        public void LoadProject_WithUnsafePrefabPath_FailsClosed(
            string prefabPath,
            string expectedFailure)
        {
            JObject manifest = CreateValidManifest();
            Scenario(manifest)["prefabPath"] = prefabPath;

            AssertManifestFails(manifest, expectedFailure);
        }

        private ProjectNetworkTestManifestLoadResult LoadProject()
        {
            ProjectNetworkTestManifestLoader loader = new ProjectNetworkTestManifestLoader();
            return loader.LoadProject(_projectDirectory);
        }

        private void AssertManifestFails(JObject manifest, string expectedFailure)
        {
            WriteManifest(manifest.ToString(Formatting.None));

            ProjectNetworkTestManifestLoadResult result = LoadProject();

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Manifest, Is.Null);
            Assert.That(result.Failure, Does.Contain(expectedFailure));
        }

        private void WriteManifest(string json)
        {
            string projectSettingsPath = Path.Combine(_projectDirectory, "ProjectSettings");
            Directory.CreateDirectory(projectSettingsPath);
            File.WriteAllText(
                Path.Combine(projectSettingsPath, "PurrNetNetworkTests.json"),
                json);
        }

        private static JObject Scenario(JObject manifest)
        {
            return (JObject)((JArray)manifest["scenarios"])[0];
        }

        private static JObject SharedFacts(JObject manifest)
        {
            return (JObject)((JObject)Scenario(manifest)["contract"])["sharedFacts"];
        }

        private static JObject Roles(JObject manifest)
        {
            return (JObject)((JObject)Scenario(manifest)["contract"])["roles"];
        }

        private static JObject Role(JObject manifest, string role)
        {
            return (JObject)Roles(manifest)[role];
        }

        private static JArray SuiteScenarios(JObject manifest)
        {
            JObject suite = (JObject)((JArray)manifest["suites"])[0];
            return (JArray)suite["scenarios"];
        }

        private static JObject CreateValidManifest()
        {
            JObject roles = new JObject
            {
                ["Server"] = CreateRole(
                    "server-listening",
                    "fixture-spawned",
                    "server-authoritative"),
                ["OwnerClient"] = CreateRole(
                    "client-connected",
                    "fixture-spawned",
                    "owner-observed"),
                ["ObserverClient"] = CreateRole(
                    "client-connected",
                    "fixture-spawned",
                    "observer-observed")
            };

            return new JObject
            {
                ["schemaVersion"] = 1,
                ["bootstrapPrefabPath"] = "Assets/NetworkTests/Bootstrap.prefab",
                ["scenarios"] = new JArray
                {
                    new JObject
                    {
                        ["id"] = "Game.Damage",
                        ["enabled"] = true,
                        ["prefabPath"] = "Assets/NetworkTests/Damage.prefab",
                        ["contract"] = new JObject
                        {
                            ["schemaVersion"] = 1,
                            ["stateRevision"] = 1,
                            ["sharedFacts"] = new JObject
                            {
                                ["health"] = 75,
                                ["alive"] = true,
                                ["label"] = "owner"
                            },
                            ["roles"] = roles
                        }
                    }
                },
                ["suites"] = new JArray
                {
                    new JObject
                    {
                        ["name"] = "smoke",
                        ["scenarios"] = new JArray("Game.Damage")
                    }
                }
            };
        }

        private static JObject CreateRole(
            string firstMilestone,
            string secondMilestone,
            string assertion)
        {
            return new JObject
            {
                ["evidence"] = new JObject { ["callbacks"] = 1 },
                ["readyMilestones"] = new JArray(firstMilestone, secondMilestone),
                ["assertions"] = new JArray(assertion),
                ["milestones"] = new JArray(
                    firstMilestone,
                    secondMilestone,
                    "completed")
            };
        }
    }
}
