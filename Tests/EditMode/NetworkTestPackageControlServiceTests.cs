using System;
using System.Collections.Generic;
using System.IO;
using Amilverton.PurrNetTesting.Editor.PackageControl;
using NUnit.Framework;

namespace Amilverton.PurrNetTesting.Tests
{
    public sealed class NetworkTestPackageControlServiceTests
    {
        private string _fixtureRoot;
        private FakePackageEnvironment _environment;
        private FakeProcessRunner _runner;

        [SetUp]
        public void SetUp()
        {
            _fixtureRoot = Path.Combine(
                Path.GetTempPath(),
                "PurrNetPackageControlService-" + Guid.NewGuid().ToString("N"));
            string projectPath = Path.Combine(_fixtureRoot, "Consumer Project");
            string packageRoot = Path.Combine(_fixtureRoot, "Package Root");
            WriteFile(
                Path.Combine(projectPath, "Packages", "manifest.json"),
                "{\"dependencies\":{}}");
            WriteFile(
                Path.Combine(packageRoot, "Tools~", "Install-PurrNetNetworkTestSkill.ps1"),
                "# installer");
            WriteFile(
                Path.Combine(packageRoot, "Tools~", "Invoke-PurrNetNetworkTestSuiteInteractive.ps1"),
                "# runner");
            WriteFile(
                Path.Combine(packageRoot, "Skills~", "run-purrnet-network-tests", "SKILL.md"),
                "---\nname: run-purrnet-network-tests\n---");

            _environment = new FakePackageEnvironment
            {
                ProjectPath = projectPath,
                PackageRoot = packageRoot,
                UnityVersion = "6000.4.10f1",
                HarnessVersion = "0.3.0",
                PurrNetVersion = "1.19.1",
                StandaloneScriptingBackend = "Mono2x",
                PowerShellPath = @"C:\Program Files\PowerShell\7\pwsh.exe",
                GitPath = @"C:\Program Files\Git\cmd\git.exe",
                IsWindowsEditor = true,
                IsWindowsStandaloneSupported = true,
                IsPowerShellSeven = true
            };
            _runner = new FakeProcessRunner();
        }

        [TearDown]
        public void TearDown()
        {
            _runner.Dispose();
            if (Directory.Exists(_fixtureRoot))
                Directory.Delete(_fixtureRoot, true);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void InstallOrUpdateSkill_DelegatesToPackagedInstallerWithDiscreteArguments(
            bool stageIncoming)
        {
            NetworkTestPackageControlService service = CreateService();

            NetworkTestPackageActionResult result =
                service.InstallOrUpdateSkill(stageIncoming);

            Assert.That(result.Succeeded, Is.True, result.Message);
            Assert.That(_runner.Request.OperationName, Does.Contain("skill"));
            Assert.That(_runner.Request.ExecutablePath, Is.EqualTo(_environment.PowerShellPath));
            Assert.That(_runner.Request.WorkingDirectory, Is.EqualTo(_environment.ProjectPath));
            Assert.That(
                _runner.Request.Arguments,
                Does.Contain(Path.Combine(
                    _environment.PackageRoot,
                    "Tools~",
                    "Install-PurrNetNetworkTestSkill.ps1")));
            Assert.That(_runner.Request.Arguments, Does.Contain(_environment.ProjectPath));
            if (stageIncoming)
            {
                Assert.That(_runner.Request.Arguments, Does.Contain("-StageIncoming"));
            }
            else
            {
                Assert.That(_runner.Request.Arguments, Does.Not.Contain("-StageIncoming"));
            }
        }

        [Test]
        public void LaunchInteractiveSuite_UsesCurrentProjectAndDoesNotForceBuildInPlace()
        {
            NetworkTestPackageControlService service = CreateService();

            NetworkTestPackageActionResult result = service.LaunchInteractiveSuite();

            Assert.That(result.Succeeded, Is.True, result.Message);
            Assert.That(_runner.Request.OperationName, Does.Contain("interactive"));
            Assert.That(_runner.Request.Arguments, Does.Contain(_environment.ProjectPath));
            Assert.That(_runner.Request.Arguments, Does.Not.Contain("-BuildInPlace"));
            Assert.That(
                _runner.Request.Arguments,
                Does.Contain(Path.Combine(
                    _environment.PackageRoot,
                    "Tools~",
                    "Invoke-PurrNetNetworkTestSuiteInteractive.ps1")));
        }

        [Test]
        public void Inspect_WithUntestedPurrNetVersion_BlocksExecutableValidation()
        {
            _environment.PurrNetVersion = "1.20.0";
            NetworkTestPackageControlService service = CreateService();

            NetworkTestSupportReport report = service.Inspect();

            Assert.That(report.CanLaunchSuite, Is.False);
            NetworkTestSupportStatus purrNetStatus = FindStatus(report, "PurrNet");
            Assert.That(purrNetStatus.Level, Is.EqualTo(NetworkTestSupportLevel.Blocked));
            Assert.That(purrNetStatus.Detail, Does.Contain("1.19.1"));
        }

        [Test]
        public void Inspect_WithIl2CppStandaloneBackend_BlocksExecutableValidation()
        {
            _environment.StandaloneScriptingBackend = "IL2CPP";
            NetworkTestPackageControlService service = CreateService();

            NetworkTestSupportReport report = service.Inspect();

            Assert.That(report.CanLaunchSuite, Is.False);
            NetworkTestSupportStatus backendStatus =
                FindStatus(report, "Standalone scripting backend");
            Assert.That(backendStatus.Level, Is.EqualTo(NetworkTestSupportLevel.Blocked));
            Assert.That(backendStatus.Detail, Does.Contain("Mono"));
        }

        [Test]
        public void LaunchInteractiveSuite_WhenPowerShellSevenIsMissing_RefusesBeforeProcessStart()
        {
            _environment.IsPowerShellSeven = false;
            NetworkTestPackageControlService service = CreateService();

            NetworkTestPackageActionResult result = service.LaunchInteractiveSuite();

            Assert.That(result.Succeeded, Is.False);
            Assert.That(_runner.Request, Is.Null);
        }

        private NetworkTestPackageControlService CreateService()
        {
            return new NetworkTestPackageControlService(_environment, _runner);
        }

        private static NetworkTestSupportStatus FindStatus(
            NetworkTestSupportReport report,
            string name)
        {
            for (int i = 0; i < report.Statuses.Count; i++)
            {
                if (report.Statuses[i].Name == name)
                    return report.Statuses[i];
            }

            Assert.Fail($"Status '{name}' was not found.");
            return null;
        }

        private static void WriteFile(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content);
        }

        private sealed class FakePackageEnvironment : INetworkTestPackageEnvironment
        {
            public string ProjectPath { get; set; }
            public string PackageRoot { get; set; }
            public string UnityVersion { get; set; }
            public string HarnessVersion { get; set; }
            public string PurrNetVersion { get; set; }
            public string StandaloneScriptingBackend { get; set; }
            public string PowerShellPath { get; set; }
            public string GitPath { get; set; }
            public bool IsWindowsEditor { get; set; }
            public bool IsWindowsStandaloneSupported { get; set; }
            public bool IsPowerShellSeven { get; set; }
        }

        private sealed class FakeProcessRunner : INetworkTestProcessRunner
        {
            public NetworkTestProcessRequest Request { get; private set; }
            public bool IsRunning { get; private set; }

            public NetworkTestPackageActionResult TryStart(NetworkTestProcessRequest request)
            {
                Request = request;
                IsRunning = true;
                return new NetworkTestPackageActionResult(true, "started");
            }

            public NetworkTestProcessPollResult Poll()
            {
                return new NetworkTestProcessPollResult(
                    Array.Empty<string>(),
                    false,
                    0,
                    Request == null ? null : Request.OperationName);
            }

            public void Cancel()
            {
                IsRunning = false;
            }

            public void Dispose()
            {
                Cancel();
            }
        }
    }
}
