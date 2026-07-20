using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using PurrNet;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using PackageManagerPackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Amilverton.PurrNetTesting.Editor.PackageControl
{
    internal interface INetworkTestPackageEnvironment
    {
        string ProjectPath { get; }
        string PackageRoot { get; }
        string UnityVersion { get; }
        string HarnessVersion { get; }
        string PurrNetVersion { get; }
        string StandaloneScriptingBackend { get; }
        string PowerShellPath { get; }
        string GitPath { get; }
        bool IsWindowsEditor { get; }
        bool IsWindowsStandaloneSupported { get; }
        bool IsPowerShellSeven { get; }
    }

    internal sealed class NetworkTestPackageEnvironment : INetworkTestPackageEnvironment
    {
        public NetworkTestPackageEnvironment()
        {
            ProjectPath = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

            PackageManagerPackageInfo harnessPackage = PackageManagerPackageInfo.FindForAssembly(
                typeof(NetworkTestPackageEnvironment).Assembly);
            PackageRoot = harnessPackage == null ? null : harnessPackage.resolvedPath;
            HarnessVersion = harnessPackage == null ? null : harnessPackage.version;

            PackageManagerPackageInfo purrNetPackage =
                PackageManagerPackageInfo.FindForAssembly(typeof(NetworkManager).Assembly);
            PurrNetVersion = purrNetPackage == null ? null : purrNetPackage.version;
            StandaloneScriptingBackend = PlayerSettings
                .GetScriptingBackend(NamedBuildTarget.Standalone)
                .ToString();

            UnityVersion = Application.unityVersion;
            IsWindowsEditor = Application.platform == RuntimePlatform.WindowsEditor;
            IsWindowsStandaloneSupported = BuildPipeline.IsBuildTargetSupported(
                BuildTargetGroup.Standalone,
                BuildTarget.StandaloneWindows64);
            PowerShellPath = FindPowerShellPath();
            IsPowerShellSeven = IsPowerShellSevenExecutable(PowerShellPath);
            GitPath = FindExecutableOnPath("git.exe");
        }

        public string ProjectPath { get; }
        public string PackageRoot { get; }
        public string UnityVersion { get; }
        public string HarnessVersion { get; }
        public string PurrNetVersion { get; }
        public string StandaloneScriptingBackend { get; }
        public string PowerShellPath { get; }
        public string GitPath { get; }
        public bool IsWindowsEditor { get; }
        public bool IsWindowsStandaloneSupported { get; }
        public bool IsPowerShellSeven { get; }

        private static string FindPowerShellPath()
        {
            string programFiles = Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrWhiteSpace(programFiles))
            {
                string conventionalPath = Path.Combine(
                    programFiles,
                    "PowerShell",
                    "7",
                    "pwsh.exe");
                if (File.Exists(conventionalPath))
                    return conventionalPath;
            }

            return FindExecutableOnPath("pwsh.exe");
        }

        private static string FindExecutableOnPath(string fileName)
        {
            string pathValue = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(pathValue))
                return null;

            string[] directories = pathValue.Split(Path.PathSeparator);
            for (int i = 0; i < directories.Length; i++)
            {
                string directory = directories[i].Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(directory))
                    continue;

                string candidate;
                try
                {
                    candidate = Path.Combine(directory, fileName);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }

            return null;
        }

        private static bool IsPowerShellSevenExecutable(string executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
                return false;

            try
            {
                FileVersionInfo version = FileVersionInfo.GetVersionInfo(executablePath);
                return version.ProductMajorPart == 7;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (Win32Exception)
            {
                return false;
            }
        }
    }
}
