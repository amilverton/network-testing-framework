using System;
using System.Collections.Generic;
using System.IO;
using Amilverton.PurrNetTesting.Editor.ProjectConfiguration;

namespace Amilverton.PurrNetTesting.Editor.PackageControl
{
    internal sealed class NetworkTestPackagePrerequisiteInspector
    {
        public const string TestedUnityVersion = "6000.4.10f1";
        public const string TestedPurrNetVersion = "1.19.1";

        private readonly INetworkTestPackageEnvironment _environment;
        private readonly ProjectNetworkTestManifestLoader _manifestLoader;

        public NetworkTestPackagePrerequisiteInspector(
            INetworkTestPackageEnvironment environment)
        {
            _environment = environment;
            _manifestLoader = new ProjectNetworkTestManifestLoader();
        }

        public NetworkTestSupportReport Inspect()
        {
            List<NetworkTestSupportStatus> statuses =
                new List<NetworkTestSupportStatus>();
            bool canInstallSkill = true;
            bool canLaunchSuite = true;

            AddRequiredStatus(
                statuses,
                "Platform",
                _environment.IsWindowsEditor,
                "Windows Editor is supported.",
                "The standalone runner and WPF viewer require the Windows Unity Editor.",
                ref canInstallSkill,
                ref canLaunchSuite);
            AddRequiredStatus(
                statuses,
                "Unity version",
                string.Equals(
                    _environment.UnityVersion,
                    TestedUnityVersion,
                    StringComparison.Ordinal),
                $"Unity {TestedUnityVersion} matches the executable support envelope.",
                $"Unity '{_environment.UnityVersion}' is outside the exact v1 envelope ({TestedUnityVersion}).",
                ref canInstallSkill,
                ref canLaunchSuite,
                false,
                true);
            AddRequiredStatus(
                statuses,
                "Windows build support",
                _environment.IsWindowsStandaloneSupported,
                "StandaloneWindows64 build support is installed.",
                "Install Windows Build Support for the current Unity Editor.",
                ref canInstallSkill,
                ref canLaunchSuite,
                false,
                true);
            AddRequiredStatus(
                statuses,
                "Standalone scripting backend",
                string.Equals(
                    _environment.StandaloneScriptingBackend,
                    "Mono2x",
                    StringComparison.Ordinal),
                "Standalone scripting backend is Mono.",
                $"Standalone scripting backend '{_environment.StandaloneScriptingBackend}' is outside the v1 Mono envelope.",
                ref canInstallSkill,
                ref canLaunchSuite,
                false,
                true);
            AddRequiredStatus(
                statuses,
                "PowerShell 7",
                _environment.IsPowerShellSeven,
                $"PowerShell 7 found at '{_environment.PowerShellPath}'.",
                "PowerShell 7 (pwsh.exe) was not found. Install it or add it to PATH.",
                ref canInstallSkill,
                ref canLaunchSuite);

            if (string.IsNullOrWhiteSpace(_environment.GitPath))
            {
                statuses.Add(new NetworkTestSupportStatus(
                    "Git",
                    NetworkTestSupportLevel.Warning,
                    "git.exe was not found on PATH. Existing packages may run, but Unity Package Manager cannot resolve new Git dependencies."));
            }
            else
            {
                statuses.Add(new NetworkTestSupportStatus(
                    "Git",
                    NetworkTestSupportLevel.Supported,
                    $"Git found at '{_environment.GitPath}'."));
            }

            AddRequiredStatus(
                statuses,
                "Harness package",
                !string.IsNullOrWhiteSpace(_environment.PackageRoot) &&
                !string.IsNullOrWhiteSpace(_environment.HarnessVersion),
                $"Harness {_environment.HarnessVersion} resolved at '{_environment.PackageRoot}'.",
                "Unity Package Manager could not resolve the harness package root and version.",
                ref canInstallSkill,
                ref canLaunchSuite);

            if (string.IsNullOrWhiteSpace(_environment.PurrNetVersion))
            {
                statuses.Add(new NetworkTestSupportStatus(
                    "PurrNet",
                    NetworkTestSupportLevel.Blocked,
                    "PurrNet is not installed or its package version could not be resolved."));
                canLaunchSuite = false;
            }
            else if (!_environment.PurrNetVersion.Equals(
                         TestedPurrNetVersion,
                         StringComparison.Ordinal))
            {
                statuses.Add(new NetworkTestSupportStatus(
                    "PurrNet",
                    NetworkTestSupportLevel.Blocked,
                    $"PurrNet {_environment.PurrNetVersion} is outside the exact v1 envelope ({TestedPurrNetVersion})."));
                canLaunchSuite = false;
            }
            else
            {
                statuses.Add(new NetworkTestSupportStatus(
                    "PurrNet",
                    NetworkTestSupportLevel.Supported,
                    $"PurrNet {TestedPurrNetVersion} matches the executable support envelope."));
            }

            string projectManifestPath = Path.Combine(
                _environment.ProjectPath,
                "Packages",
                "manifest.json");
            AddRequiredStatus(
                statuses,
                "Unity project manifest",
                File.Exists(projectManifestPath),
                $"Project manifest found at '{projectManifestPath}'.",
                $"Current project has no Packages/manifest.json at '{projectManifestPath}'.",
                ref canInstallSkill,
                ref canLaunchSuite);

            string installerPath = GetPackagePath("Tools~", "Install-PurrNetNetworkTestSkill.ps1");
            string runnerPath = GetPackagePath("Tools~", "Invoke-PurrNetNetworkTestSuiteInteractive.ps1");
            string skillPath = GetPackagePath(
                "Skills~",
                "run-purrnet-network-tests",
                "SKILL.md");
            AddRequiredStatus(
                statuses,
                "Skill installer",
                File.Exists(installerPath) && File.Exists(skillPath),
                "Packaged AI skill and refusal-safe installer are available.",
                $"Packaged skill or installer is missing. Expected '{skillPath}' and '{installerPath}'.",
                ref canInstallSkill,
                ref canLaunchSuite,
                true,
                false);
            AddRequiredStatus(
                statuses,
                "Interactive suite runner",
                File.Exists(runnerPath),
                $"Interactive suite runner found at '{runnerPath}'.",
                $"Interactive suite runner is missing at '{runnerPath}'.",
                ref canInstallSkill,
                ref canLaunchSuite,
                false,
                true);

            ProjectNetworkTestManifestLoadResult manifestResult =
                _manifestLoader.LoadProject(_environment.ProjectPath);
            if (!manifestResult.Succeeded)
            {
                statuses.Add(new NetworkTestSupportStatus(
                    "Project network-test manifest",
                    NetworkTestSupportLevel.Blocked,
                    manifestResult.Failure));
                canLaunchSuite = false;
            }
            else if (!manifestResult.ManifestFound)
            {
                statuses.Add(new NetworkTestSupportStatus(
                    "Project network-test manifest",
                    NetworkTestSupportLevel.Warning,
                    $"{ProjectNetworkTestManifestLoader.ManifestRelativePath} is absent. Built-in scenarios remain available; create the conventional manifest before adding project scenarios."));
            }
            else
            {
                statuses.Add(new NetworkTestSupportStatus(
                    "Project network-test manifest",
                    NetworkTestSupportLevel.Supported,
                    $"{ProjectNetworkTestManifestLoader.ManifestRelativePath} is valid with {manifestResult.Manifest.Scenarios.Count} scenario(s) and {manifestResult.Manifest.Suites.Count} suite(s)."));
            }

            AddSkillInstallationStatus(statuses);
            return new NetworkTestSupportReport(
                statuses,
                canInstallSkill,
                canLaunchSuite,
                manifestResult.Succeeded && manifestResult.ManifestFound);
        }

        public string GetInstallerPath()
        {
            return GetPackagePath("Tools~", "Install-PurrNetNetworkTestSkill.ps1");
        }

        public string GetInteractiveRunnerPath()
        {
            return GetPackagePath("Tools~", "Invoke-PurrNetNetworkTestSuiteInteractive.ps1");
        }

        private void AddSkillInstallationStatus(ICollection<NetworkTestSupportStatus> statuses)
        {
            string installedSkillPath = Path.Combine(
                _environment.ProjectPath,
                ".agents",
                "skills",
                "run-purrnet-network-tests");
            if (!Directory.Exists(installedSkillPath))
            {
                statuses.Add(new NetworkTestSupportStatus(
                    "Project AI skill",
                    NetworkTestSupportLevel.Warning,
                    $"Agent skill is not installed at '{installedSkillPath}'."));
                return;
            }

            string ownershipPath = Path.Combine(
                installedSkillPath,
                ".purrnet-network-tests-skill.json");
            if (!File.Exists(ownershipPath))
            {
                statuses.Add(new NetworkTestSupportStatus(
                    "Project AI skill",
                    NetworkTestSupportLevel.Warning,
                    $"Skill directory exists without the package ownership record. The installer will refuse to overwrite it."));
                return;
            }

            statuses.Add(new NetworkTestSupportStatus(
                "Project AI skill",
                NetworkTestSupportLevel.Supported,
                "Package ownership record found. The installer will verify the recorded content hash before any update."));
        }

        private string GetPackagePath(params string[] segments)
        {
            if (string.IsNullOrWhiteSpace(_environment.PackageRoot))
                return string.Empty;

            string path = _environment.PackageRoot;
            for (int i = 0; i < segments.Length; i++)
            {
                path = Path.Combine(path, segments[i]);
            }

            return path;
        }

        private static void AddRequiredStatus(
            ICollection<NetworkTestSupportStatus> statuses,
            string name,
            bool supported,
            string supportedDetail,
            string blockedDetail,
            ref bool canInstallSkill,
            ref bool canLaunchSuite,
            bool blocksSkillInstallation = true,
            bool blocksSuiteLaunch = true)
        {
            statuses.Add(new NetworkTestSupportStatus(
                name,
                supported
                    ? NetworkTestSupportLevel.Supported
                    : NetworkTestSupportLevel.Blocked,
                supported ? supportedDetail : blockedDetail));
            if (supported)
                return;

            if (blocksSkillInstallation)
                canInstallSkill = false;

            if (blocksSuiteLaunch)
                canLaunchSuite = false;
        }
    }
}
