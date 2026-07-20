using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Amilverton.PurrNetTesting.Editor;
using NUnit.Framework;
using PurrNet;
using PurrNet.Transports;
using UnityEngine;
using UnityEngine.TestTools;

namespace Amilverton.PurrNetTesting.Tests
{
    public sealed class NetworkTestBootstrapTests
    {
        [Test]
        public void TryConfigure_GeneratedFallback_AddsUdpManagerAndDefaultRules()
        {
            GameObject networkRoot = new GameObject("Generated fallback root");
            networkRoot.SetActive(false);
            NetworkRules runtimeRules = null;

            try
            {
                NetworkTestNetworkRootConfigurationResult result =
                    NetworkTestNetworkRootConfigurator.TryConfigure(
                        networkRoot,
                        NetworkTestNetworkRootMode.GeneratedFallback);

                Assert.That(result.Succeeded, Is.True, result.Failure);
                Assert.That(result.NetworkManager, Is.SameAs(networkRoot.GetComponent<NetworkManager>()));
                Assert.That(result.UdpTransport, Is.SameAs(networkRoot.GetComponent<UDPTransport>()));
                Assert.That(result.NetworkManager.transport, Is.SameAs(result.UdpTransport));
                Assert.That((int)result.NetworkManager.startServerFlags, Is.Zero);
                Assert.That((int)result.NetworkManager.startClientFlags, Is.Zero);
                Assert.That(result.NetworkManager.networkRules, Is.Not.Null);
                Assert.That(result.Hook, Is.Null);
                Assert.That(result.AuthoredRules, Is.Null);

                runtimeRules = result.NetworkManager.networkRules;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(networkRoot);
                if (runtimeRules != null)
                    UnityEngine.Object.DestroyImmediate(runtimeRules);
            }
        }

        [Test]
        public void TryConfigure_ProjectAuthored_PreservesRulesAndClearsAutoStartFlags()
        {
            GameObject networkRoot = new GameObject("Authored root");
            networkRoot.SetActive(false);
            UDPTransport udpTransport = networkRoot.AddComponent<UDPTransport>();
            NetworkManager networkManager = networkRoot.AddComponent<NetworkManager>();
            NetworkPrefabs authoredPrefabs = ScriptableObject.CreateInstance<NetworkPrefabs>();
            NetworkRules authoredRules = ScriptableObject.CreateInstance<NetworkRules>();
            networkManager.transport = udpTransport;
            networkManager.startServerFlags = (StartFlags)int.MaxValue;
            networkManager.startClientFlags = (StartFlags)int.MaxValue;
            AssignNetworkPrefabs(networkManager, authoredPrefabs);
            AssignNetworkRules(networkManager, authoredRules);

            try
            {
                NetworkTestNetworkRootConfigurationResult result =
                    NetworkTestNetworkRootConfigurator.TryConfigure(
                        networkRoot,
                        NetworkTestNetworkRootMode.ProjectAuthored);

                Assert.That(result.Succeeded, Is.True, result.Failure);
                Assert.That(result.NetworkManager, Is.SameAs(networkManager));
                Assert.That(result.UdpTransport, Is.SameAs(udpTransport));
                Assert.That(result.AuthoredRules, Is.SameAs(authoredRules));
                Assert.That(networkManager.networkRules, Is.SameAs(authoredRules));
                Assert.That((int)networkManager.startServerFlags, Is.Zero);
                Assert.That((int)networkManager.startClientFlags, Is.Zero);
                Assert.That(networkRoot.activeSelf, Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(networkRoot);
                UnityEngine.Object.DestroyImmediate(authoredPrefabs);
                UnityEngine.Object.DestroyImmediate(authoredRules);
            }
        }

        [Test]
        public void TryConfigure_ProjectAuthoredHookProvisioning_AddsConfiguredUdpRoot()
        {
            GameObject networkRoot = new GameObject("Hook-provisioned authored root");
            networkRoot.SetActive(false);
            NetworkTestBootstrapTestHook hook =
                networkRoot.AddComponent<NetworkTestBootstrapTestHook>();
            NetworkPrefabs networkPrefabs = ScriptableObject.CreateInstance<NetworkPrefabs>();
            NetworkRules networkRules = ScriptableObject.CreateInstance<NetworkRules>();
            hook.ConfigureUdpNetworkRootForBuild(networkPrefabs, networkRules);

            try
            {
                NetworkTestNetworkRootConfigurationResult result =
                    NetworkTestNetworkRootConfigurator.TryConfigure(
                        networkRoot,
                        NetworkTestNetworkRootMode.ProjectAuthored);

                Assert.That(result.Succeeded, Is.True, result.Failure);
                Assert.That(result.Hook, Is.SameAs(hook));
                Assert.That(result.NetworkManager, Is.SameAs(networkRoot.GetComponent<NetworkManager>()));
                Assert.That(result.UdpTransport, Is.SameAs(networkRoot.GetComponent<UDPTransport>()));
                Assert.That(result.NetworkManager.transport, Is.SameAs(result.UdpTransport));
                Assert.That(result.NetworkManager.networkRules, Is.SameAs(networkRules));
                Assert.That(result.AuthoredRules, Is.SameAs(networkRules));
                Assert.That(ReadNetworkPrefabs(result.NetworkManager), Is.SameAs(networkPrefabs));
                Assert.That(networkRoot.activeSelf, Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(networkRoot);
                UnityEngine.Object.DestroyImmediate(networkPrefabs);
                UnityEngine.Object.DestroyImmediate(networkRules);
            }
        }

        [Test]
        public void TryConfigure_ProjectAuthoredHookProvisioningWithSerializedTransport_FailsClosed()
        {
            GameObject networkRoot = new GameObject("Conflicting hook-provisioned root");
            networkRoot.SetActive(false);
            networkRoot.AddComponent<UDPTransport>();
            NetworkTestBootstrapTestHook hook =
                networkRoot.AddComponent<NetworkTestBootstrapTestHook>();
            NetworkPrefabs networkPrefabs = ScriptableObject.CreateInstance<NetworkPrefabs>();
            NetworkRules networkRules = ScriptableObject.CreateInstance<NetworkRules>();
            hook.ConfigureUdpNetworkRootForBuild(networkPrefabs, networkRules);

            try
            {
                NetworkTestNetworkRootConfigurationResult result =
                    NetworkTestNetworkRootConfigurator.TryConfigure(
                        networkRoot,
                        NetworkTestNetworkRootMode.ProjectAuthored);

                Assert.That(result.Succeeded, Is.False);
                Assert.That(result.Failure, Does.Contain("must not also serialize"));
                Assert.That(networkRoot.GetComponentsInChildren<NetworkManager>(true), Is.Empty);
                Assert.That(networkRoot.activeSelf, Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(networkRoot);
                UnityEngine.Object.DestroyImmediate(networkPrefabs);
                UnityEngine.Object.DestroyImmediate(networkRules);
            }
        }

        [Test]
        public void ValidateAuthoredBootstrapPrefab_HookProvisionedRoot_AcceptsBeforeBuild()
        {
            GameObject networkRoot = new GameObject("Hook-provisioned prefab source");
            networkRoot.SetActive(false);
            NetworkTestBootstrapTestHook hook =
                networkRoot.AddComponent<NetworkTestBootstrapTestHook>();
            NetworkPrefabs networkPrefabs = ScriptableObject.CreateInstance<NetworkPrefabs>();
            NetworkRules networkRules = ScriptableObject.CreateInstance<NetworkRules>();
            hook.ConfigureUdpNetworkRootForBuild(networkPrefabs, networkRules);

            try
            {
                Assert.DoesNotThrow(
                    () => NetworkTestPlayerBuilder.ValidateAuthoredBootstrapPrefab(
                        networkRoot,
                        "Assets/NetworkTests/ProjectRoot.prefab"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(networkRoot);
                UnityEngine.Object.DestroyImmediate(networkPrefabs);
                UnityEngine.Object.DestroyImmediate(networkRules);
            }
        }

        [Test]
        public void ValidateAuthoredBootstrapPrefab_MixedProvisioning_FailsBeforeBuild()
        {
            GameObject networkRoot = new GameObject("Mixed prefab source");
            networkRoot.SetActive(false);
            networkRoot.AddComponent<UDPTransport>();
            NetworkTestBootstrapTestHook hook =
                networkRoot.AddComponent<NetworkTestBootstrapTestHook>();
            NetworkPrefabs networkPrefabs = ScriptableObject.CreateInstance<NetworkPrefabs>();
            NetworkRules networkRules = ScriptableObject.CreateInstance<NetworkRules>();
            hook.ConfigureUdpNetworkRootForBuild(networkPrefabs, networkRules);

            try
            {
                InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                    () => NetworkTestPlayerBuilder.ValidateAuthoredBootstrapPrefab(
                        networkRoot,
                        "Assets/NetworkTests/MixedRoot.prefab"));
                Assert.That(exception.Message, Does.Contain("mixes serialized PurrNet components"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(networkRoot);
                UnityEngine.Object.DestroyImmediate(networkPrefabs);
                UnityEngine.Object.DestroyImmediate(networkRules);
            }
        }

        [Test]
        public void TryConfigure_ProjectAuthoredWithMissingManager_FailsWithoutActivatingRoot()
        {
            GameObject networkRoot = new GameObject("Invalid authored root");
            networkRoot.SetActive(false);
            networkRoot.AddComponent<UDPTransport>();

            try
            {
                NetworkTestNetworkRootConfigurationResult result =
                    NetworkTestNetworkRootConfigurator.TryConfigure(
                        networkRoot,
                        NetworkTestNetworkRootMode.ProjectAuthored);

                Assert.That(result.Succeeded, Is.False);
                Assert.That(result.Failure, Does.Contain("exactly one NetworkManager"));
                Assert.That(networkRoot.activeSelf, Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(networkRoot);
            }
        }

        [Test]
        public void TryConfigure_ProjectAuthoredWithActiveRoot_FailsBeforeAddingComponents()
        {
            GameObject networkRoot = new GameObject("Active authored root");

            try
            {
                NetworkTestNetworkRootConfigurationResult result =
                    NetworkTestNetworkRootConfigurator.TryConfigure(
                        networkRoot,
                        NetworkTestNetworkRootMode.ProjectAuthored);

                Assert.That(result.Succeeded, Is.False);
                Assert.That(result.Failure, Does.Contain("must remain inactive"));
                Assert.That(networkRoot.GetComponentsInChildren<NetworkManager>(true), Is.Empty);
                Assert.That(networkRoot.GetComponentsInChildren<UDPTransport>(true), Is.Empty);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(networkRoot);
            }
        }

        [Test]
        public void TryConfigure_ProjectAuthoredWithoutRules_Fails()
        {
            GameObject networkRoot = new GameObject("Authored root without rules");
            networkRoot.SetActive(false);
            UDPTransport udpTransport = networkRoot.AddComponent<UDPTransport>();
            NetworkManager networkManager = networkRoot.AddComponent<NetworkManager>();
            networkManager.transport = udpTransport;

            try
            {
                NetworkTestNetworkRootConfigurationResult result =
                    NetworkTestNetworkRootConfigurator.TryConfigure(
                        networkRoot,
                        NetworkTestNetworkRootMode.ProjectAuthored);

                Assert.That(result.Succeeded, Is.False);
                Assert.That(result.Failure, Does.Contain("serialized NetworkRules"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(networkRoot);
            }
        }

        [Test]
        public void TryConfigure_ProjectAuthoredWithTwoUdpTransports_Fails()
        {
            GameObject networkRoot = new GameObject("Invalid authored root");
            networkRoot.SetActive(false);
            UDPTransport selectedTransport = networkRoot.AddComponent<UDPTransport>();
            networkRoot.AddComponent<UDPTransport>();
            NetworkManager networkManager = networkRoot.AddComponent<NetworkManager>();
            NetworkRules authoredRules = ScriptableObject.CreateInstance<NetworkRules>();
            networkManager.transport = selectedTransport;
            AssignNetworkRules(networkManager, authoredRules);

            try
            {
                NetworkTestNetworkRootConfigurationResult result =
                    NetworkTestNetworkRootConfigurator.TryConfigure(
                        networkRoot,
                        NetworkTestNetworkRootMode.ProjectAuthored);

                Assert.That(result.Succeeded, Is.False);
                Assert.That(result.Failure, Does.Contain("exactly one UDPTransport"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(networkRoot);
                UnityEngine.Object.DestroyImmediate(authoredRules);
            }
        }

        [Test]
        public void HookCallbacks_WhenInvokedTwice_RunExactlyOnce()
        {
            GameObject bootstrapObject = new GameObject("Inactive bootstrap test");
            bootstrapObject.SetActive(false);
            NetworkTestBootstrap bootstrap = bootstrapObject.AddComponent<NetworkTestBootstrap>();

            GameObject networkRoot = new GameObject("Inactive network root");
            networkRoot.SetActive(false);
            NetworkManager networkManager = networkRoot.AddComponent<NetworkManager>();
            NetworkTestBootstrapTestHook hook = networkRoot.AddComponent<NetworkTestBootstrapTestHook>();
            SetPrivateField(bootstrap, "_networkManager", networkManager);
            SetPrivateField(bootstrap, "_bootstrapHook", hook);

            try
            {
                Assert.That(bootstrap.TryInvokePreNetworkStartHook(out string firstPreFailure), Is.True);
                Assert.That(firstPreFailure, Is.Null);
                Assert.That(bootstrap.TryInvokePreNetworkStartHook(out string secondPreFailure), Is.True);
                Assert.That(secondPreFailure, Is.Null);

                Assert.That(bootstrap.TryInvokePostNetworkStopHook(out string firstPostFailure), Is.True);
                Assert.That(firstPostFailure, Is.Null);
                Assert.That(bootstrap.TryInvokePostNetworkStopHook(out string secondPostFailure), Is.True);
                Assert.That(secondPostFailure, Is.Null);

                Assert.That(hook.PreStartCallCount, Is.EqualTo(1));
                Assert.That(hook.PostStopCallCount, Is.EqualTo(1));
                Assert.That(hook.ReceivedBootstrap, Is.SameAs(bootstrap));
                Assert.That(hook.ReceivedNetworkManager, Is.SameAs(networkManager));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(bootstrapObject);
                UnityEngine.Object.DestroyImmediate(networkRoot);
            }
        }

        [Test]
        public void PreStartHook_WhenItThrows_RetainsFailureAndDoesNotRetry()
        {
            GameObject bootstrapObject = new GameObject("Inactive bootstrap test");
            bootstrapObject.SetActive(false);
            NetworkTestBootstrap bootstrap = bootstrapObject.AddComponent<NetworkTestBootstrap>();

            GameObject networkRoot = new GameObject("Inactive network root");
            networkRoot.SetActive(false);
            NetworkManager networkManager = networkRoot.AddComponent<NetworkManager>();
            NetworkTestBootstrapTestHook hook = networkRoot.AddComponent<NetworkTestBootstrapTestHook>();
            hook.ThrowBeforeStart = true;
            SetPrivateField(bootstrap, "_networkManager", networkManager);
            SetPrivateField(bootstrap, "_bootstrapHook", hook);

            try
            {
                Assert.That(bootstrap.TryInvokePreNetworkStartHook(out string firstFailure), Is.False);
                Assert.That(firstFailure, Does.Contain("hook-pre-start-failure"));
                Assert.That(bootstrap.TryInvokePreNetworkStartHook(out string secondFailure), Is.False);
                Assert.That(secondFailure, Is.EqualTo(firstFailure));
                Assert.That(hook.PreStartCallCount, Is.EqualTo(1));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(bootstrapObject);
                UnityEngine.Object.DestroyImmediate(networkRoot);
            }
        }

        [Test]
        public void Pass_WithValidEvidence_IsProvisionalAndDoesNotWriteResultImmediately()
        {
            string testDirectory = Path.Combine(
                Path.GetTempPath(),
                "PurrNetNetworkTestBootstrap-" + Guid.NewGuid().ToString("N"));
            string resultPath = Path.Combine(testDirectory, "server.result.json");
            Directory.CreateDirectory(testDirectory);

            GameObject gameObject = new GameObject("Inactive bootstrap test");
            gameObject.SetActive(false);
            NetworkTestBootstrap bootstrap = gameObject.AddComponent<NetworkTestBootstrap>();

            try
            {
                SetPrivateField(bootstrap, "_arguments", ParseArguments(resultPath));
                SetPrivateField(bootstrap, "_readyPublished", true);
                bootstrap.SetFact("value", 4);
                bootstrap.RecordAssertion("state-observed");

                bootstrap.Pass(4);

                Assert.That(File.Exists(resultPath), Is.False);
                Assert.That(GetPrivateField<bool>(bootstrap, "_completionRequested"), Is.True);
                Assert.That(GetPrivateField<bool>(bootstrap, "_finished"), Is.False);
                Assert.That(GetPrivateField<NetworkTestReport>(bootstrap, "_publishedReport"), Is.Null);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
                Directory.Delete(testDirectory, true);
            }
        }

        [Test]
        public void Fail_AfterPassWasPublished_RevokesResultAndSetsNonZeroExit()
        {
            string testDirectory = Path.Combine(
                Path.GetTempPath(),
                "PurrNetNetworkTestBootstrap-" + Guid.NewGuid().ToString("N"));
            string resultPath = Path.Combine(testDirectory, "server.result.json");
            Directory.CreateDirectory(testDirectory);

            GameObject gameObject = new GameObject("Inactive bootstrap test");
            gameObject.SetActive(false);
            NetworkTestBootstrap bootstrap = gameObject.AddComponent<NetworkTestBootstrap>();

            try
            {
                NetworkTestArguments arguments = ParseArguments(resultPath);
                NetworkTestReport provisionalPass = new NetworkTestReport
                {
                    SchemaVersion = 2,
                    RunId = "run-late-fail",
                    ScenarioId = "Harness.Probe",
                    Role = "Server",
                    Status = "passed",
                    Milestones = new List<string> { "fixture-spawned" },
                    StateRevision = 1,
                    SharedFacts = new Dictionary<string, object> { { "value", 1 } },
                    RoleEvidence = new Dictionary<string, object> { { "role", "Server" } },
                    Assertions = new List<string> { "provisional-assertion" },
                    LogPath = "server.log"
                };

                SetPrivateField(bootstrap, "_arguments", arguments);
                SetPrivateField(bootstrap, "_finished", true);
                SetPrivateField(bootstrap, "_publishedReport", provisionalPass);
                SetPrivateField(bootstrap, "_exitCode", 0);

                LogAssert.Expect(LogType.Error, "[Fail] late duplicate callback");
                bootstrap.Fail("late duplicate callback");

                string revokedJson = File.ReadAllText(resultPath);
                Assert.That(revokedJson, Does.Contain("\"status\": \"failed\""));
                Assert.That(revokedJson, Does.Contain("\"failure\": \"late duplicate callback\""));
                Assert.That(GetPrivateField<int>(bootstrap, "_exitCode"), Is.EqualTo(1));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
                Directory.Delete(testDirectory, true);
            }
        }

        private static NetworkTestArguments ParseArguments(string resultPath)
        {
            string[] arguments =
            {
                "NetworkTestPlayer.exe",
                "-networkTestRunId", "run-late-fail",
                "-networkTestScenario", "Harness.Probe",
                "-networkTestRole", "Server",
                "-networkTestConfig", "config.json",
                "-networkTestReady", "server.ready.json",
                "-networkTestResult", resultPath,
                "-networkTestLog", "server.log"
            };

            NetworkTestArgumentsParseResult parseResult = NetworkTestArguments.Parse(arguments);
            Assert.That(parseResult.Succeeded, Is.True, parseResult.Failure);
            return parseResult.Arguments;
        }

        private static void SetPrivateField<T>(NetworkTestBootstrap target, string name, T value)
        {
            FieldInfo field = typeof(NetworkTestBootstrap).GetField(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"Private field '{name}' was not found.");
            field.SetValue(target, value);
        }

        private static T GetPrivateField<T>(NetworkTestBootstrap target, string name)
        {
            FieldInfo field = typeof(NetworkTestBootstrap).GetField(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"Private field '{name}' was not found.");
            return (T)field.GetValue(target);
        }

        private static void AssignNetworkRules(NetworkManager networkManager, NetworkRules rules)
        {
            FieldInfo field = typeof(NetworkManager).GetField(
                "_networkRules",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "PurrNet 1.19.1 NetworkRules field was not found.");
            field.SetValue(networkManager, rules);
        }

        private static void AssignNetworkPrefabs(
            NetworkManager networkManager,
            NetworkPrefabs networkPrefabs)
        {
            FieldInfo field = typeof(NetworkManager).GetField(
                "_networkPrefabs",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "PurrNet 1.19.1 NetworkPrefabs field was not found.");
            field.SetValue(networkManager, networkPrefabs);
        }

        private static NetworkPrefabs ReadNetworkPrefabs(NetworkManager networkManager)
        {
            FieldInfo field = typeof(NetworkManager).GetField(
                "_networkPrefabs",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "PurrNet 1.19.1 NetworkPrefabs field was not found.");
            return (NetworkPrefabs)field.GetValue(networkManager);
        }
    }

    public sealed class NetworkTestBootstrapTestHook : NetworkTestBootstrapHook
    {
        public int PreStartCallCount { get; private set; }
        public int PostStopCallCount { get; private set; }
        public NetworkTestBootstrap ReceivedBootstrap { get; private set; }
        public NetworkManager ReceivedNetworkManager { get; private set; }
        public bool ThrowBeforeStart { get; set; }

        public override void OnPreNetworkStart(
            NetworkTestBootstrap bootstrap,
            NetworkManager networkManager)
        {
            PreStartCallCount++;
            ReceivedBootstrap = bootstrap;
            ReceivedNetworkManager = networkManager;

            if (ThrowBeforeStart)
                throw new InvalidOperationException("hook-pre-start-failure");
        }

        public override void OnPostNetworkStop(
            NetworkTestBootstrap bootstrap,
            NetworkManager networkManager)
        {
            PostStopCallCount++;
            ReceivedBootstrap = bootstrap;
            ReceivedNetworkManager = networkManager;
        }
    }
}
