using System;
using System.IO;

namespace Amilverton.PurrNetTesting.Editor.ProjectConfiguration
{
    /// <summary>
    /// Reads and validates the optional project-owned network-test manifest.
    /// </summary>
    public sealed class ProjectNetworkTestManifestLoader
    {
        public const string ManifestRelativePath = "ProjectSettings/PurrNetNetworkTests.json";

        private readonly ProjectNetworkTestManifestJsonCodec _codec =
            new ProjectNetworkTestManifestJsonCodec();
        private readonly ProjectNetworkTestManifestValidator _validator =
            new ProjectNetworkTestManifestValidator();

        /// <summary>
        /// Load the conventional manifest under the supplied Unity project root.
        /// A missing file represents a valid project with no project scenarios.
        /// </summary>
        public ProjectNetworkTestManifestLoadResult LoadProject(string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
            {
                return ProjectNetworkTestManifestLoadResult.CreateFailure(
                    "Unity project path cannot be empty.");
            }

            string fullProjectPath;
            try
            {
                fullProjectPath = Path.GetFullPath(projectPath);
            }
            catch (ArgumentException exception)
            {
                return ProjectNetworkTestManifestLoadResult.CreateFailure(
                    $"Unity project path is invalid: {exception.Message}");
            }
            catch (NotSupportedException exception)
            {
                return ProjectNetworkTestManifestLoadResult.CreateFailure(
                    $"Unity project path is unsupported: {exception.Message}");
            }

            string manifestPath = Path.Combine(
                fullProjectPath,
                "ProjectSettings",
                "PurrNetNetworkTests.json");
            if (!File.Exists(manifestPath))
            {
                return ProjectNetworkTestManifestLoadResult.CreateSuccess(
                    ProjectNetworkTestManifest.CreateEmpty(),
                    false);
            }

            string json;
            try
            {
                json = File.ReadAllText(manifestPath);
            }
            catch (IOException exception)
            {
                return ProjectNetworkTestManifestLoadResult.CreateFailure(
                    $"Failed to read {ManifestRelativePath}: {exception.Message}");
            }
            catch (UnauthorizedAccessException exception)
            {
                return ProjectNetworkTestManifestLoadResult.CreateFailure(
                    $"Failed to read {ManifestRelativePath}: {exception.Message}");
            }

            if (!_codec.TryDeserialize(
                    json,
                    out ProjectNetworkTestManifestDto source,
                    out string codecFailure))
            {
                return ProjectNetworkTestManifestLoadResult.CreateFailure(
                    $"Failed to parse {ManifestRelativePath}: {codecFailure}");
            }

            if (!_validator.TryValidate(
                    source,
                    out ProjectNetworkTestManifest manifest,
                    out string validationFailure))
            {
                return ProjectNetworkTestManifestLoadResult.CreateFailure(
                    $"Failed to validate {ManifestRelativePath}: {validationFailure}");
            }

            return ProjectNetworkTestManifestLoadResult.CreateSuccess(manifest, true);
        }
    }

    /// <summary>
    /// Result of loading the optional project-owned manifest.
    /// </summary>
    public sealed class ProjectNetworkTestManifestLoadResult
    {
        private ProjectNetworkTestManifestLoadResult(
            bool succeeded,
            bool manifestFound,
            ProjectNetworkTestManifest manifest,
            string failure)
        {
            Succeeded = succeeded;
            ManifestFound = manifestFound;
            Manifest = manifest;
            Failure = failure;
        }

        public bool Succeeded { get; }
        public bool ManifestFound { get; }
        public ProjectNetworkTestManifest Manifest { get; }
        public string Failure { get; }

        internal static ProjectNetworkTestManifestLoadResult CreateSuccess(
            ProjectNetworkTestManifest manifest,
            bool manifestFound)
        {
            return new ProjectNetworkTestManifestLoadResult(true, manifestFound, manifest, null);
        }

        internal static ProjectNetworkTestManifestLoadResult CreateFailure(string failure)
        {
            return new ProjectNetworkTestManifestLoadResult(false, false, null, failure);
        }
    }
}
