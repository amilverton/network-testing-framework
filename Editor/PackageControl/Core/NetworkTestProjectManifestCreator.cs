using System;
using System.IO;
using System.Text;
using Amilverton.PurrNetTesting.Editor.ProjectConfiguration;

namespace Amilverton.PurrNetTesting.Editor.PackageControl
{
    internal sealed class NetworkTestProjectManifestCreator
    {
        private const string EmptyManifest =
            "{\n" +
            "  \"schemaVersion\": 1,\n" +
            "  \"scenarios\": [],\n" +
            "  \"suites\": []\n" +
            "}\n";

        public NetworkTestPackageActionResult CreateWhenMissing(string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
            {
                return new NetworkTestPackageActionResult(
                    false,
                    "[CreateWhenMissing] Unity project path cannot be empty.");
            }

            string fullProjectPath;
            try
            {
                fullProjectPath = Path.GetFullPath(projectPath);
            }
            catch (ArgumentException exception)
            {
                return new NetworkTestPackageActionResult(
                    false,
                    $"[CreateWhenMissing] Unity project path is invalid: {exception.Message}");
            }
            catch (NotSupportedException exception)
            {
                return new NetworkTestPackageActionResult(
                    false,
                    $"[CreateWhenMissing] Unity project path is unsupported: {exception.Message}");
            }

            string projectSettingsPath = Path.Combine(fullProjectPath, "ProjectSettings");
            string manifestPath = Path.Combine(
                fullProjectPath,
                ProjectNetworkTestManifestLoader.ManifestRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(manifestPath))
            {
                return new NetworkTestPackageActionResult(
                    false,
                    $"[CreateWhenMissing] {ProjectNetworkTestManifestLoader.ManifestRelativePath} already exists. No files were changed.");
            }

            try
            {
                Directory.CreateDirectory(projectSettingsPath);
                using (FileStream stream = new FileStream(
                           manifestPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None))
                using (StreamWriter writer = new StreamWriter(
                           stream,
                           new UTF8Encoding(false)))
                {
                    writer.Write(EmptyManifest);
                }
            }
            catch (IOException exception)
            {
                return new NetworkTestPackageActionResult(
                    false,
                    $"[CreateWhenMissing] Could not create {ProjectNetworkTestManifestLoader.ManifestRelativePath} without overwriting: {exception.Message}");
            }
            catch (UnauthorizedAccessException exception)
            {
                return new NetworkTestPackageActionResult(
                    false,
                    $"[CreateWhenMissing] ProjectSettings is not writable: {exception.Message}");
            }

            return new NetworkTestPackageActionResult(
                true,
                $"Created {ProjectNetworkTestManifestLoader.ManifestRelativePath}. Add project scenarios and suites when needed.");
        }
    }
}
