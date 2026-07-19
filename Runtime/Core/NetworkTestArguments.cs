using System;
using System.Collections.Generic;

namespace Amilverton.PurrNetTesting
{
    /// <summary>
    /// Holds the validated command-line arguments for one Player role.
    /// </summary>
    public sealed class NetworkTestArguments
    {
        private const string RunIdKey = "-networkTestRunId";
        private const string ScenarioKey = "-networkTestScenario";
        private const string RoleKey = "-networkTestRole";
        private const string ConfigurationKey = "-networkTestConfig";
        private const string ReadyKey = "-networkTestReady";
        private const string ResultKey = "-networkTestResult";
        private const string LogKey = "-networkTestLog";

        private static readonly string[] RequiredKeys =
        {
            RunIdKey,
            ScenarioKey,
            RoleKey,
            ConfigurationKey,
            ReadyKey,
            ResultKey,
            LogKey
        };

        private NetworkTestArguments(
            string runId,
            string scenarioId,
            NetworkTestRole role,
            string configurationPath,
            string readyPath,
            string resultPath,
            string logPath)
        {
            RunId = runId;
            ScenarioId = scenarioId;
            Role = role;
            ConfigurationPath = configurationPath;
            ReadyPath = readyPath;
            ResultPath = resultPath;
            LogPath = logPath;
        }

        public string RunId { get; }
        public string ScenarioId { get; }
        public NetworkTestRole Role { get; }
        public string ConfigurationPath { get; }
        public string ReadyPath { get; }
        public string ResultPath { get; }
        public string LogPath { get; }

        /// <summary>
        /// Parse the supplied process arguments without throwing for expected input errors.
        /// </summary>
        public static NetworkTestArgumentsParseResult Parse(IReadOnlyList<string> arguments)
        {
            if (arguments == null)
                return NetworkTestArgumentsParseResult.Failed("Command-line arguments cannot be null.");

            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < arguments.Count; i++)
            {
                string argument = arguments[i];
                if (!IsRequiredKey(argument))
                    continue;

                if (i + 1 >= arguments.Count || IsRequiredKey(arguments[i + 1]))
                    return NetworkTestArgumentsParseResult.Failed($"Argument '{argument}' requires a value.");

                if (values.ContainsKey(argument))
                    return NetworkTestArgumentsParseResult.Failed($"Argument '{argument}' was supplied more than once.");

                values.Add(argument, arguments[i + 1]);
                i++;
            }

            for (int i = 0; i < RequiredKeys.Length; i++)
            {
                string requiredKey = RequiredKeys[i];
                if (!values.TryGetValue(requiredKey, out string value) || string.IsNullOrWhiteSpace(value))
                    return NetworkTestArgumentsParseResult.Failed($"Required argument '{requiredKey}' is missing or empty.");
            }

            if (!Enum.TryParse(values[RoleKey], true, out NetworkTestRole role))
                return NetworkTestArgumentsParseResult.Failed($"Network test role '{values[RoleKey]}' is invalid.");

            NetworkTestArguments parsed = new NetworkTestArguments(
                values[RunIdKey],
                values[ScenarioKey],
                role,
                values[ConfigurationKey],
                values[ReadyKey],
                values[ResultKey],
                values[LogKey]);

            return NetworkTestArgumentsParseResult.Passed(parsed);
        }

        private static bool IsRequiredKey(string value)
        {
            for (int i = 0; i < RequiredKeys.Length; i++)
            {
                if (string.Equals(RequiredKeys[i], value, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Reports either parsed arguments or one actionable validation failure.
    /// </summary>
    public readonly struct NetworkTestArgumentsParseResult
    {
        private NetworkTestArgumentsParseResult(NetworkTestArguments arguments, string failure)
        {
            Arguments = arguments;
            Failure = failure;
        }

        public NetworkTestArguments Arguments { get; }
        public string Failure { get; }
        public bool Succeeded => Arguments != null;

        public static NetworkTestArgumentsParseResult Passed(NetworkTestArguments arguments)
        {
            return new NetworkTestArgumentsParseResult(arguments, null);
        }

        public static NetworkTestArgumentsParseResult Failed(string failure)
        {
            return new NetworkTestArgumentsParseResult(null, failure);
        }
    }
}
