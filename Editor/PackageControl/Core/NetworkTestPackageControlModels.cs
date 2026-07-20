using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Amilverton.PurrNetTesting.Editor.PackageControl
{
    internal enum NetworkTestSupportLevel : byte
    {
        Supported,
        Warning,
        Blocked
    }

    internal sealed class NetworkTestSupportStatus
    {
        public NetworkTestSupportStatus(
            string name,
            NetworkTestSupportLevel level,
            string detail)
        {
            Name = name;
            Level = level;
            Detail = detail;
        }

        public string Name { get; }
        public NetworkTestSupportLevel Level { get; }
        public string Detail { get; }
    }

    internal sealed class NetworkTestSupportReport
    {
        private readonly ReadOnlyCollection<NetworkTestSupportStatus> _statuses;

        public NetworkTestSupportReport(
            IList<NetworkTestSupportStatus> statuses,
            bool canInstallSkill,
            bool canLaunchSuite,
            bool projectManifestFound)
        {
            NetworkTestSupportStatus[] statusArray =
                new NetworkTestSupportStatus[statuses.Count];
            statuses.CopyTo(statusArray, 0);
            _statuses = Array.AsReadOnly(statusArray);
            CanInstallSkill = canInstallSkill;
            CanLaunchSuite = canLaunchSuite;
            ProjectManifestFound = projectManifestFound;
        }

        public IReadOnlyList<NetworkTestSupportStatus> Statuses => _statuses;
        public bool CanInstallSkill { get; }
        public bool CanLaunchSuite { get; }
        public bool ProjectManifestFound { get; }
    }

    internal readonly struct NetworkTestPackageActionResult
    {
        public NetworkTestPackageActionResult(bool succeeded, string message)
        {
            Succeeded = succeeded;
            Message = message;
        }

        public bool Succeeded { get; }
        public string Message { get; }
    }

    internal sealed class NetworkTestProcessRequest
    {
        private readonly ReadOnlyCollection<string> _arguments;

        public NetworkTestProcessRequest(
            string operationName,
            string executablePath,
            string workingDirectory,
            IList<string> arguments)
        {
            OperationName = operationName;
            ExecutablePath = executablePath;
            WorkingDirectory = workingDirectory;

            string[] argumentArray = new string[arguments.Count];
            arguments.CopyTo(argumentArray, 0);
            _arguments = Array.AsReadOnly(argumentArray);
        }

        public string OperationName { get; }
        public string ExecutablePath { get; }
        public string WorkingDirectory { get; }
        public IReadOnlyList<string> Arguments => _arguments;
    }

    internal sealed class NetworkTestProcessPollResult
    {
        public NetworkTestProcessPollResult(
            string[] outputLines,
            bool completed,
            int exitCode,
            string operationName)
        {
            OutputLines = outputLines ?? Array.Empty<string>();
            Completed = completed;
            ExitCode = exitCode;
            OperationName = operationName;
        }

        public IReadOnlyList<string> OutputLines { get; }
        public bool Completed { get; }
        public int ExitCode { get; }
        public string OperationName { get; }
    }
}
