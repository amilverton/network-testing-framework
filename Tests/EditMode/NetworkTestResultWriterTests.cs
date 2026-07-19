using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace Amilverton.PurrNetTesting.Tests
{
    public sealed class NetworkTestResultWriterTests
    {
        private string _testDirectory;

        [SetUp]
        public void SetUp()
        {
            _testDirectory = Path.Combine(
                Path.GetTempPath(),
                "PurrNetNetworkTestWriter-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_testDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_testDirectory))
                Directory.Delete(_testDirectory, true);
        }

        [Test]
        public void Write_WithValidReport_PublishesCompleteJsonWithoutTemporarySibling()
        {
            string resultPath = Path.Combine(_testDirectory, "server.result.json");
            NetworkTestReport report = new NetworkTestReport
            {
                SchemaVersion = 2,
                RunId = "run-1",
                ScenarioId = "Harness.InventoryTransfer",
                Role = "Server",
                Status = "passed",
                Milestones = new List<string> { "server-listening" },
                StateRevision = 1,
                SharedFacts = new Dictionary<string, object> { { "amount", 3 } },
                RoleEvidence = new Dictionary<string, object> { { "role", "Server" } },
                Assertions = new List<string> { "server-validated-amount" },
                Failure = null,
                LogPath = "server.log"
            };

            NetworkTestResultWriter writer = new NetworkTestResultWriter();
            NetworkTestWriteResult writeResult = writer.Write(resultPath, report);

            Assert.That(writeResult.Succeeded, Is.True, writeResult.Failure);
            Assert.That(File.Exists(resultPath), Is.True);
            Assert.That(Directory.GetFiles(_testDirectory, "*.tmp-*"), Is.Empty);

            string json = File.ReadAllText(resultPath);
            Assert.That(json, Does.Contain("\"status\": \"passed\""));
            Assert.That(json, Does.Contain("\"stateRevision\": 1"));
        }

        [Test]
        public void Write_WhenDestinationExists_ReplacesItWithTheNewCompleteReport()
        {
            string resultPath = Path.Combine(_testDirectory, "server.result.json");
            File.WriteAllText(resultPath, "old");

            NetworkTestReadyReport report = new NetworkTestReadyReport
            {
                SchemaVersion = 2,
                RunId = "run-2",
                ScenarioId = "Harness.InventoryTransfer",
                Role = "Server",
                Milestones = new List<string> { "fixture-spawned" }
            };

            NetworkTestResultWriter writer = new NetworkTestResultWriter();
            NetworkTestWriteResult writeResult = writer.Write(resultPath, report);

            Assert.That(writeResult.Succeeded, Is.True, writeResult.Failure);
            Assert.That(File.ReadAllText(resultPath), Does.Contain("run-2"));
            Assert.That(Directory.GetFiles(_testDirectory, "*.tmp-*"), Is.Empty);
        }
    }
}
