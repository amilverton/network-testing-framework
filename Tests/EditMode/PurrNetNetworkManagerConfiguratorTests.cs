using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using PurrNet;
using UnityEngine;

namespace Amilverton.PurrNetTesting.Tests
{
    public sealed class PurrNetNetworkManagerConfiguratorTests
    {
        private readonly List<GameObject> _createdPrefabs = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _createdPrefabs.Count; i++)
                Object.DestroyImmediate(_createdPrefabs[i]);

            _createdPrefabs.Clear();
        }

        [Test]
        public void TryCreateConfiguredPrefabProvider_WithoutProjectProvider_UsesScenarioProvider()
        {
            TestPrefabProvider scenarioProvider = new TestPrefabProvider(
                CreateEntry(0, "Scenario"));

            bool succeeded = PurrNetNetworkManagerConfigurator.TryCreateConfiguredPrefabProvider(
                null,
                scenarioProvider,
                out IPrefabProvider configuredProvider,
                out string failure);

            Assert.That(succeeded, Is.True, failure);
            Assert.That(configuredProvider, Is.SameAs(scenarioProvider));
            Assert.That(scenarioProvider.RefreshCount, Is.EqualTo(1));
        }

        [Test]
        public void TryConfigurePrefabProvider_WhenProjectProviderIsRequiredButMissing_Fails()
        {
            GameObject networkRoot = new GameObject("Inactive project root");
            networkRoot.SetActive(false);
            NetworkManager networkManager = networkRoot.AddComponent<NetworkManager>();
            GameObject scenarioPrefab = CreatePrefab("Scenario");
            NetworkTestPrefabProvider scenarioProvider = new NetworkTestPrefabProvider(
                new[]
                {
                    new NetworkTestScenarioRegistration("Test.Scenario", scenarioPrefab)
                });

            try
            {
                bool succeeded = PurrNetNetworkManagerConfigurator.TryConfigurePrefabProvider(
                    networkManager,
                    scenarioProvider,
                    true,
                    out string failure);

                Assert.That(succeeded, Is.False);
                Assert.That(failure, Does.Contain("did not initialize its serialized prefab provider"));
            }
            finally
            {
                Object.DestroyImmediate(networkRoot);
            }
        }

        [Test]
        public void TryCreateConfiguredPrefabProvider_WithSparseProjectIds_PreservesIdsAndOffsetsScenario()
        {
            PrefabData projectTwo = CreateEntry(2, "Project Two");
            PrefabData projectSeven = CreateEntry(7, "Project Seven");
            PrefabData scenarioZero = CreateEntry(0, "Scenario Zero");
            projectTwo.pooled = true;
            projectTwo.warmupCount = 3;
            TestPrefabProvider projectProvider = new TestPrefabProvider(projectTwo, projectSeven);
            TestPrefabProvider scenarioProvider = new TestPrefabProvider(scenarioZero);

            bool succeeded = PurrNetNetworkManagerConfigurator.TryCreateConfiguredPrefabProvider(
                projectProvider,
                scenarioProvider,
                out IPrefabProvider configuredProvider,
                out string failure);

            Assert.That(succeeded, Is.True, failure);
            Assert.That(configuredProvider, Is.TypeOf<CompositePrefabProvider>());
            Assert.That(projectProvider.RefreshCount, Is.EqualTo(1));
            Assert.That(scenarioProvider.RefreshCount, Is.EqualTo(1));

            Assert.That(configuredProvider.TryGetPrefabData(2, out PrefabData retainedTwo), Is.True);
            Assert.That(retainedTwo.prefab, Is.SameAs(projectTwo.prefab));
            Assert.That(retainedTwo.pooled, Is.True);
            Assert.That(retainedTwo.warmupCount, Is.EqualTo(3));
            Assert.That(configuredProvider.TryGetPrefabData(7, out PrefabData retainedSeven), Is.True);
            Assert.That(retainedSeven.prefab, Is.SameAs(projectSeven.prefab));
            Assert.That(configuredProvider.TryGetPrefabData(8, out PrefabData offsetScenario), Is.True);
            Assert.That(offsetScenario.prefab, Is.SameAs(scenarioZero.prefab));
        }

        [Test]
        public void TryCreateConfiguredPrefabProvider_WithDuplicateProjectId_Fails()
        {
            TestPrefabProvider projectProvider = new TestPrefabProvider(
                CreateEntry(3, "Project A"),
                CreateEntry(3, "Project B"));
            TestPrefabProvider scenarioProvider = new TestPrefabProvider(
                CreateEntry(0, "Scenario"));

            bool succeeded = PurrNetNetworkManagerConfigurator.TryCreateConfiguredPrefabProvider(
                projectProvider,
                scenarioProvider,
                out IPrefabProvider configuredProvider,
                out string failure);

            Assert.That(succeeded, Is.False);
            Assert.That(configuredProvider, Is.Null);
            Assert.That(failure, Does.Contain("duplicate prefab ID 3"));
        }

        [Test]
        public void TryCreateConfiguredPrefabProvider_WithNegativeProjectId_Fails()
        {
            TestPrefabProvider projectProvider = new TestPrefabProvider(
                CreateEntry(-1, "Project Negative"));
            TestPrefabProvider scenarioProvider = new TestPrefabProvider(
                CreateEntry(0, "Scenario"));

            bool succeeded = PurrNetNetworkManagerConfigurator.TryCreateConfiguredPrefabProvider(
                projectProvider,
                scenarioProvider,
                out IPrefabProvider configuredProvider,
                out string failure);

            Assert.That(succeeded, Is.False);
            Assert.That(configuredProvider, Is.Null);
            Assert.That(failure, Does.Contain("negative prefab ID -1"));
        }

        [Test]
        public void TryCreateConfiguredPrefabProvider_WithNullProjectPrefab_Fails()
        {
            TestPrefabProvider projectProvider = new TestPrefabProvider(
                new PrefabData { prefabId = 1, prefab = null });
            TestPrefabProvider scenarioProvider = new TestPrefabProvider(
                CreateEntry(0, "Scenario"));

            bool succeeded = PurrNetNetworkManagerConfigurator.TryCreateConfiguredPrefabProvider(
                projectProvider,
                scenarioProvider,
                out IPrefabProvider configuredProvider,
                out string failure);

            Assert.That(succeeded, Is.False);
            Assert.That(configuredProvider, Is.Null);
            Assert.That(failure, Does.Contain("null prefab at ID 1"));
        }

        [Test]
        public void TryCreateConfiguredPrefabProvider_WithPrefabInBothProviders_Fails()
        {
            GameObject sharedPrefab = CreatePrefab("Shared");
            TestPrefabProvider projectProvider = new TestPrefabProvider(
                new PrefabData { prefabId = 0, prefab = sharedPrefab });
            TestPrefabProvider scenarioProvider = new TestPrefabProvider(
                new PrefabData { prefabId = 0, prefab = sharedPrefab });

            bool succeeded = PurrNetNetworkManagerConfigurator.TryCreateConfiguredPrefabProvider(
                projectProvider,
                scenarioProvider,
                out IPrefabProvider configuredProvider,
                out string failure);

            Assert.That(succeeded, Is.False);
            Assert.That(configuredProvider, Is.Null);
            Assert.That(failure, Does.Contain("already registered by the project provider"));
        }

        [Test]
        public void TryCreateConfiguredPrefabProvider_WhenScenarioOffsetOverflows_Fails()
        {
            TestPrefabProvider projectProvider = new TestPrefabProvider(
                CreateEntry(int.MaxValue, "Project Max"));
            TestPrefabProvider scenarioProvider = new TestPrefabProvider(
                CreateEntry(0, "Scenario"));

            bool succeeded = PurrNetNetworkManagerConfigurator.TryCreateConfiguredPrefabProvider(
                projectProvider,
                scenarioProvider,
                out IPrefabProvider configuredProvider,
                out string failure);

            Assert.That(succeeded, Is.False);
            Assert.That(configuredProvider, Is.Null);
            Assert.That(failure, Does.Contain("leaves no valid integer range"));
        }

        [Test]
        public void TryCreateConfiguredPrefabProvider_WithNestedComposite_Fails()
        {
            CompositePrefabProvider projectProvider = new CompositePrefabProvider();
            TestPrefabProvider scenarioProvider = new TestPrefabProvider(
                CreateEntry(0, "Scenario"));

            bool succeeded = PurrNetNetworkManagerConfigurator.TryCreateConfiguredPrefabProvider(
                projectProvider,
                scenarioProvider,
                out IPrefabProvider configuredProvider,
                out string failure);

            Assert.That(succeeded, Is.False);
            Assert.That(configuredProvider, Is.Null);
            Assert.That(failure, Does.Contain("Nested CompositePrefabProvider"));
        }

        [Test]
        public void TryCreateConfiguredPrefabProvider_WithAsyncProjectProvider_Fails()
        {
            TestAsyncPrefabProvider projectProvider = new TestAsyncPrefabProvider(
                CreateEntry(0, "Async Project"));
            TestPrefabProvider scenarioProvider = new TestPrefabProvider(
                CreateEntry(0, "Scenario"));

            bool succeeded = PurrNetNetworkManagerConfigurator.TryCreateConfiguredPrefabProvider(
                projectProvider,
                scenarioProvider,
                out IPrefabProvider configuredProvider,
                out string failure);

            Assert.That(succeeded, Is.False);
            Assert.That(configuredProvider, Is.Null);
            Assert.That(failure, Does.Contain("Async or Addressables"));
        }

        private PrefabData CreateEntry(int prefabId, string name)
        {
            return new PrefabData
            {
                prefabId = prefabId,
                prefab = CreatePrefab(name),
                pooled = false,
                warmupCount = 0
            };
        }

        private GameObject CreatePrefab(string name)
        {
            GameObject prefab = new GameObject(name);
            _createdPrefabs.Add(prefab);
            return prefab;
        }
    }

    internal class TestPrefabProvider : IPrefabProvider
    {
        private readonly List<PrefabData> _entries;

        public TestPrefabProvider(params PrefabData[] entries)
        {
            _entries = new List<PrefabData>(entries);
        }

        public IEnumerable<PrefabData> allPrefabs => _entries;
        public int RefreshCount { get; private set; }

        public bool TryGetPrefabData(int prefabId, out PrefabData prefabData)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].prefabId != prefabId)
                    continue;

                prefabData = _entries[i];
                return true;
            }

            prefabData = default;
            return false;
        }

        public bool TryGetPrefabData(GameObject prefab, out PrefabData prefabData)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].prefab != prefab)
                    continue;

                prefabData = _entries[i];
                return true;
            }

            prefabData = default;
            return false;
        }

        public void Refresh()
        {
            RefreshCount++;
        }
    }

    internal sealed class TestAsyncPrefabProvider : TestPrefabProvider, IAsyncPrefabProvider
    {
        public TestAsyncPrefabProvider(params PrefabData[] entries)
            : base(entries)
        {
        }

        public bool NeedsLoad(int prefabId)
        {
            return false;
        }

        public Task<PrefabData> LoadPrefabAsync(int prefabId)
        {
            TryGetPrefabData(prefabId, out PrefabData prefabData);
            return Task.FromResult(prefabData);
        }
    }
}
