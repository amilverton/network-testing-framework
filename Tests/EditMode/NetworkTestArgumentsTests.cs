using NUnit.Framework;

namespace Caffeinated.NetworkTesting.Tests
{
    public sealed class NetworkTestArgumentsTests
    {
        [Test]
        public void Parse_WithCompleteArguments_ReturnsTypedValues()
        {
            string[] arguments =
            {
                "NetworkTestPlayer.exe",
                "-networkTestRunId", "run-1",
                "-networkTestScenario", "Harness.InventoryTransfer",
                "-networkTestRole", "OwnerClient",
                "-networkTestConfig", "config.json",
                "-networkTestReady", "owner.ready.json",
                "-networkTestResult", "owner.result.json",
                "-networkTestLog", "owner.log"
            };

            NetworkTestArgumentsParseResult result = NetworkTestArguments.Parse(arguments);

            Assert.That(result.Succeeded, Is.True, result.Failure);
            Assert.That(result.Arguments.RunId, Is.EqualTo("run-1"));
            Assert.That(result.Arguments.ScenarioId, Is.EqualTo("Harness.InventoryTransfer"));
            Assert.That(result.Arguments.Role, Is.EqualTo(NetworkTestRole.OwnerClient));
            Assert.That(result.Arguments.ResultPath, Is.EqualTo("owner.result.json"));
        }

        [Test]
        public void Parse_WithMissingResultArgument_ReturnsActionableFailure()
        {
            string[] arguments =
            {
                "NetworkTestPlayer.exe",
                "-networkTestRunId", "run-1",
                "-networkTestScenario", "Harness.InventoryTransfer",
                "-networkTestRole", "Server",
                "-networkTestConfig", "config.json",
                "-networkTestReady", "server.ready.json",
                "-networkTestLog", "server.log"
            };

            NetworkTestArgumentsParseResult result = NetworkTestArguments.Parse(arguments);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Failure, Does.Contain("-networkTestResult"));
        }

        [Test]
        public void Parse_WithDuplicateRoleArgument_ReturnsActionableFailure()
        {
            string[] arguments =
            {
                "NetworkTestPlayer.exe",
                "-networkTestRunId", "run-1",
                "-networkTestScenario", "Harness.InventoryTransfer",
                "-networkTestRole", "Server",
                "-networkTestRole", "ObserverClient",
                "-networkTestConfig", "config.json",
                "-networkTestReady", "server.ready.json",
                "-networkTestResult", "server.result.json",
                "-networkTestLog", "server.log"
            };

            NetworkTestArgumentsParseResult result = NetworkTestArguments.Parse(arguments);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Failure, Does.Contain("more than once"));
        }
    }
}
