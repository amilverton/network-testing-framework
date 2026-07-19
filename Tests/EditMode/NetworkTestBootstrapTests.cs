using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Amilverton.PurrNetTesting.Tests
{
    public sealed class NetworkTestBootstrapTests
    {
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
    }
}
