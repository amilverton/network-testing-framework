using System;
using System.IO;
using Caffeinated.NetworkTesting.Editor.PackageControl;
using Caffeinated.NetworkTesting.Editor.ProjectConfiguration;
using NUnit.Framework;

namespace Caffeinated.NetworkTesting.Tests
{
    public sealed class NetworkTestProjectManifestCreatorTests
    {
        private string _projectPath;

        [SetUp]
        public void SetUp()
        {
            _projectPath = Path.Combine(
                Path.GetTempPath(),
                "PurrNetPackageControlManifest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_projectPath);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_projectPath))
                Directory.Delete(_projectPath, true);
        }

        [Test]
        public void CreateWhenMissing_CreatesValidConventionalEmptyManifest()
        {
            NetworkTestProjectManifestCreator creator =
                new NetworkTestProjectManifestCreator();

            NetworkTestPackageActionResult action = creator.CreateWhenMissing(_projectPath);
            ProjectNetworkTestManifestLoadResult load =
                new ProjectNetworkTestManifestLoader().LoadProject(_projectPath);

            Assert.That(action.Succeeded, Is.True, action.Message);
            Assert.That(load.Succeeded, Is.True, load.Failure);
            Assert.That(load.ManifestFound, Is.True);
            Assert.That(load.Manifest.SchemaVersion, Is.EqualTo(1));
            Assert.That(load.Manifest.Scenarios, Is.Empty);
            Assert.That(load.Manifest.Suites, Is.Empty);
        }

        [Test]
        public void CreateWhenMissing_WhenManifestExists_RefusesWithoutChangingBytes()
        {
            string manifestPath = Path.Combine(
                _projectPath,
                "ProjectSettings",
                "PurrNetNetworkTests.json");
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath));
            byte[] originalBytes = { 0x7B, 0x20, 0x7D, 0x0A };
            File.WriteAllBytes(manifestPath, originalBytes);
            NetworkTestProjectManifestCreator creator =
                new NetworkTestProjectManifestCreator();

            NetworkTestPackageActionResult action = creator.CreateWhenMissing(_projectPath);

            Assert.That(action.Succeeded, Is.False);
            Assert.That(action.Message, Does.Contain("already exists"));
            Assert.That(action.Message, Does.Contain("No files were changed"));
            Assert.That(File.ReadAllBytes(manifestPath), Is.EqualTo(originalBytes));
        }
    }
}
