using System;
using System.Collections.Generic;

namespace Amilverton.PurrNetTesting.Editor.PackageControl
{
    internal sealed class NetworkTestPackageControlService : IDisposable
    {
        private readonly INetworkTestPackageEnvironment _environment;
        private readonly INetworkTestProcessRunner _processRunner;
        private readonly NetworkTestPackagePrerequisiteInspector _prerequisiteInspector;
        private readonly NetworkTestProjectManifestCreator _manifestCreator;

        public NetworkTestPackageControlService(
            INetworkTestPackageEnvironment environment,
            INetworkTestProcessRunner processRunner)
        {
            _environment = environment;
            _processRunner = processRunner;
            _prerequisiteInspector = new NetworkTestPackagePrerequisiteInspector(environment);
            _manifestCreator = new NetworkTestProjectManifestCreator();
        }

        public bool IsOperationRunning => _processRunner.IsRunning;

        public static NetworkTestPackageControlService CreateDefault()
        {
            return new NetworkTestPackageControlService(
                new NetworkTestPackageEnvironment(),
                new NetworkTestProcessRunner());
        }

        public NetworkTestSupportReport Inspect()
        {
            return _prerequisiteInspector.Inspect();
        }

        public NetworkTestPackageActionResult CreateProjectManifest()
        {
            if (IsOperationRunning)
            {
                return new NetworkTestPackageActionResult(
                    false,
                    "[CreateProjectManifest] Wait for the active external operation before changing project configuration.");
            }

            return _manifestCreator.CreateWhenMissing(_environment.ProjectPath);
        }

        public NetworkTestPackageActionResult InstallOrUpdateSkill(bool stageIncoming)
        {
            NetworkTestSupportReport report = Inspect();
            if (!report.CanInstallSkill)
            {
                return new NetworkTestPackageActionResult(
                    false,
                    "[InstallOrUpdateSkill] Required installer prerequisites are blocked. Refresh and inspect the status list.");
            }

            List<string> arguments = new List<string>
            {
                "-NoProfile",
                "-File",
                _prerequisiteInspector.GetInstallerPath(),
                "-ProjectPath",
                _environment.ProjectPath
            };
            if (stageIncoming)
                arguments.Add("-StageIncoming");

            NetworkTestProcessRequest request = new NetworkTestProcessRequest(
                stageIncoming ? "Stage incoming agent skill" : "Install or update agent skill",
                _environment.PowerShellPath,
                _environment.ProjectPath,
                arguments);
            return _processRunner.TryStart(request);
        }

        public NetworkTestPackageActionResult LaunchInteractiveSuite()
        {
            NetworkTestSupportReport report = Inspect();
            if (!report.CanLaunchSuite)
            {
                return new NetworkTestPackageActionResult(
                    false,
                    "[LaunchInteractiveSuite] Required runner prerequisites are blocked. Refresh and inspect the status list.");
            }

            List<string> arguments = new List<string>
            {
                "-NoProfile",
                "-File",
                _prerequisiteInspector.GetInteractiveRunnerPath(),
                "-ProjectPath",
                _environment.ProjectPath
            };
            NetworkTestProcessRequest request = new NetworkTestProcessRequest(
                "Run interactive PurrNet test suite",
                _environment.PowerShellPath,
                _environment.ProjectPath,
                arguments);
            return _processRunner.TryStart(request);
        }

        public NetworkTestProcessPollResult PollOperation()
        {
            return _processRunner.Poll();
        }

        public void CancelOperation()
        {
            _processRunner.Cancel();
        }

        public void Dispose()
        {
            _processRunner.Dispose();
        }
    }
}
