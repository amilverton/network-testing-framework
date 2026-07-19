using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using PurrNet;
using PurrNet.Transports;
using UnityEngine;

namespace Amilverton.PurrNetTesting
{
    /// <summary>
    /// Starts one standalone network role and owns its result-file lifecycle.
    /// </summary>
    public sealed class NetworkTestBootstrap : MonoBehaviour
    {
        private const int SchemaVersion = 2;

        [SerializeField] private GameObject networkRoot;
        private NetworkManager networkManager;
        private UDPTransport udpTransport;
        [SerializeField] private NetworkTestScenarioRegistration[] scenarioRegistrations =
            Array.Empty<NetworkTestScenarioRegistration>();

        private readonly Dictionary<string, object> _sharedFacts = new Dictionary<string, object>();
        private readonly Dictionary<string, object> _roleEvidence = new Dictionary<string, object>();
        private readonly List<string> _assertions = new List<string>();
        private readonly List<string> _milestones = new List<string>();
        private readonly NetworkTestResultWriter _resultWriter = new NetworkTestResultWriter();

        private NetworkTestArguments _arguments;
        private NetworkTestConfiguration _configuration;
        private NetworkTestScenarioRegistration _selectedScenario;
        private float _startedAt;
        private bool _initialized;
        private bool _readyPublished;
        private bool _finished;
        private int _exitCode;
        private NetworkTestReport _publishedReport;

        public static NetworkTestBootstrap Current { get; private set; }

        public NetworkTestRole Role => _arguments == null ? NetworkTestRole.Server : _arguments.Role;
        public string RunId => _arguments?.RunId;
        public string ScenarioId => _arguments?.ScenarioId;

        /// <summary>
        /// Assign generated build assets before the bootstrap scene is saved.
        /// </summary>
        public void ConfigureForBuild(
            GameObject configuredNetworkRoot,
            NetworkTestScenarioRegistration[] configuredScenarios)
        {
            networkRoot = configuredNetworkRoot;
            scenarioRegistrations = configuredScenarios ?? Array.Empty<NetworkTestScenarioRegistration>();
        }

        private void Awake()
        {
            if (Current != null && Current != this)
            {
                Debug.LogError("[Awake] More than one NetworkTestBootstrap exists in the Player.");
                enabled = false;
                return;
            }

            Current = this;
            Application.runInBackground = true;
            Application.logMessageReceived += HandleLogMessage;

            if (networkRoot == null)
            {
                QuitWithoutReport("[Awake] Inactive PurrNet network root is not configured in the bootstrap scene.");
                return;
            }

            if (networkRoot.activeSelf)
            {
                QuitWithoutReport("[Awake] PurrNet network root must remain inactive until runtime rules are configured.");
                return;
            }

            udpTransport = networkRoot.AddComponent<UDPTransport>();
            networkManager = networkRoot.AddComponent<NetworkManager>();
            networkManager.startServerFlags = (StartFlags)0;
            networkManager.startClientFlags = (StartFlags)0;
            networkManager.transport = udpTransport;

            NetworkTestArgumentsParseResult parseResult = NetworkTestArguments.Parse(Environment.GetCommandLineArgs());
            if (!parseResult.Succeeded)
            {
                QuitWithoutReport($"[Awake] {parseResult.Failure}");
                return;
            }

            _arguments = parseResult.Arguments;
            if (!TryReadConfiguration(_arguments.ConfigurationPath, out _configuration, out string configurationFailure))
            {
                Fail(configurationFailure);
                return;
            }

            if (!TryValidateConfiguration(_configuration, out string validationFailure))
            {
                Fail(validationFailure);
                return;
            }

            if (!TryFindScenario(_arguments.ScenarioId, out _selectedScenario))
            {
                Fail($"Scenario '{_arguments.ScenarioId}' is not registered in this Player build.");
                return;
            }

            networkManager.SetPrefabProvider(new NetworkTestPrefabProvider(scenarioRegistrations));
            if (networkManager.prefabProvider == null)
            {
                Fail("PurrNet did not accept the generated scenario prefab provider.");
                return;
            }

            if (!PurrNetNetworkManagerConfigurator.TryApplyDefaultRules(networkManager, out string rulesFailure))
            {
                Fail(rulesFailure);
                return;
            }

            udpTransport.address = _configuration.Address;
            udpTransport.serverPort = (ushort)_configuration.Port;
            networkRoot.SetActive(true);

            if (networkManager.networkRules == null)
            {
                Fail("PurrNet NetworkManager activated without runtime NetworkRules.");
                return;
            }

            _startedAt = Time.realtimeSinceStartup;
            _initialized = true;
        }

        private IEnumerator Start()
        {
            if (!_initialized)
                yield break;

            StartCoroutine(WatchTimeout());

            if (Role == NetworkTestRole.Server)
            {
                yield return StartServer();
                yield break;
            }

            yield return StartClient();
        }

        private IEnumerator StartServer()
        {
            networkManager.StartServer();

            while (!_finished && networkManager.serverState != ConnectionState.Connected)
                yield return null;

            if (_finished)
                yield break;

            AddMilestone("server-listening");
            GameObject scenarioInstance = UnityProxy.Instantiate(_selectedScenario.Prefab);
            if (scenarioInstance == null)
            {
                Fail($"Failed to instantiate scenario prefab for '{ScenarioId}'.");
                yield break;
            }

            AddMilestone("fixture-created");
        }

        private IEnumerator StartClient()
        {
            networkManager.StartClient();

            while (!_finished && networkManager.clientState != ConnectionState.Connected)
                yield return null;

            if (_finished)
                yield break;

            AddMilestone("client-connected");
        }

        private IEnumerator WatchTimeout()
        {
            while (!_finished)
            {
                float elapsedSeconds = Time.realtimeSinceStartup - _startedAt;
                if (elapsedSeconds >= _configuration.TimeoutSeconds)
                {
                    Fail($"Scenario '{ScenarioId}' timed out after {_configuration.TimeoutSeconds} seconds.");
                    yield break;
                }

                yield return null;
            }
        }

        /// <summary>
        /// Record a named synchronization or verification milestone once.
        /// </summary>
        public void AddMilestone(string milestone)
        {
            if (string.IsNullOrWhiteSpace(milestone))
                return;

            if (_milestones.Contains(milestone))
                return;

            _milestones.Add(milestone);
            Debug.Log($"[Harness:{Role}] Milestone: {milestone}");
        }

        /// <summary>
        /// Add or replace a shared string fact in the final compact report.
        /// </summary>
        public void SetFact(string key, string value)
        {
            SetFactValue(key, value);
        }

        /// <summary>
        /// Add or replace a shared integer fact in the final compact report.
        /// </summary>
        public void SetFact(string key, int value)
        {
            SetFactValue(key, value);
        }

        /// <summary>
        /// Add or replace a shared boolean fact in the final compact report.
        /// </summary>
        public void SetFact(string key, bool value)
        {
            SetFactValue(key, value);
        }

        /// <summary>
        /// Add role-local string evidence which is intentionally not compared across roles.
        /// </summary>
        public void SetEvidence(string key, string value)
        {
            SetEvidenceValue(key, value);
        }

        /// <summary>
        /// Add role-local integer evidence which is intentionally not compared across roles.
        /// </summary>
        public void SetEvidence(string key, int value)
        {
            SetEvidenceValue(key, value);
        }

        /// <summary>
        /// Add role-local boolean evidence which is intentionally not compared across roles.
        /// </summary>
        public void SetEvidence(string key, bool value)
        {
            SetEvidenceValue(key, value);
        }

        /// <summary>
        /// Record one role-owned assertion that must be unique in the final report.
        /// </summary>
        public void RecordAssertion(string assertion)
        {
            if (_finished || string.IsNullOrWhiteSpace(assertion))
                return;

            if (_assertions.Contains(assertion))
            {
                Fail($"Assertion '{assertion}' was recorded more than once by role '{Role}'.");
                return;
            }

            _assertions.Add(assertion);
            Debug.Log($"[Harness:{Role}] Assertion passed: {assertion}");
        }

        /// <summary>
        /// Publish role readiness after the scenario, not merely the process, is ready.
        /// </summary>
        public void PublishReady()
        {
            if (_finished || _readyPublished)
                return;

            AddMilestone("fixture-spawned");

            NetworkTestReadyReport readyReport = new NetworkTestReadyReport
            {
                SchemaVersion = SchemaVersion,
                RunId = RunId,
                ScenarioId = ScenarioId,
                Role = Role.ToString(),
                Milestones = new List<string>(_milestones)
            };

            NetworkTestWriteResult writeResult = _resultWriter.Write(_arguments.ReadyPath, readyReport);
            if (!writeResult.Succeeded)
            {
                Fail(writeResult.Failure);
                return;
            }

            _readyPublished = true;
            Debug.Log($"[Harness:{Role}] Ready for scenario '{ScenarioId}'.");
        }

        /// <summary>
        /// Publish a passing result after all role-owned assertions have completed.
        /// </summary>
        public void Pass(int stateRevision)
        {
            if (!_readyPublished)
            {
                Fail("A scenario cannot pass before it publishes role readiness.");
                return;
            }

            if (_sharedFacts.Count == 0)
            {
                Fail("A passing scenario must publish at least one shared fact.");
                return;
            }

            if (_assertions.Count == 0)
            {
                Fail("A passing scenario must record at least one role-owned assertion.");
                return;
            }

            Complete("passed", stateRevision, null, 0);
        }

        /// <summary>
        /// Publish an actionable failure and exit the Player non-zero.
        /// </summary>
        public void Fail(string failure)
        {
            string actionableFailure = string.IsNullOrWhiteSpace(failure)
                ? "The network test failed without an error message."
                : failure;

            Debug.LogError($"[Fail] {actionableFailure}");

            if (_finished)
            {
                RevokePublishedPass(actionableFailure);
                return;
            }

            Complete("failed", -1, actionableFailure, 1);
        }

        private void RevokePublishedPass(string failure)
        {
            if (_publishedReport == null || _publishedReport.Status != "passed" || _arguments == null)
                return;

            _publishedReport.Status = "failed";
            _publishedReport.Failure = failure;
            _exitCode = 1;

            NetworkTestWriteResult writeResult = _resultWriter.Write(_arguments.ResultPath, _publishedReport);
            if (!writeResult.Succeeded)
            {
                Debug.LogError($"[RevokePublishedPass] {writeResult.Failure}");
                _exitCode = 2;
            }
        }

        private void SetFactValue(string key, object value)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                Debug.LogError("[SetFactValue] Fact key cannot be empty.");
                return;
            }

            _sharedFacts[key] = value;
            Debug.Log($"[Harness:{Role}] Shared fact: {key}={value}");
        }

        private void SetEvidenceValue(string key, object value)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                Debug.LogError("[SetEvidenceValue] Evidence key cannot be empty.");
                return;
            }

            _roleEvidence[key] = value;
            Debug.Log($"[Harness:{Role}] Role evidence: {key}={value}");
        }

        private void Complete(string status, int stateRevision, string failure, int exitCode)
        {
            if (_finished)
                return;

            _finished = true;
            _exitCode = exitCode;

            if (_arguments == null)
            {
                StartCoroutine(QuitAfterDelay());
                return;
            }

            _roleEvidence["role"] = Role.ToString();
            _roleEvidence["processId"] = System.Diagnostics.Process.GetCurrentProcess().Id;
            _roleEvidence["provenance"] = Role == NetworkTestRole.Server
                ? "dedicated-server-authority"
                : "client-replicated-read";
            _roleEvidence["transitionTrace"] = string.Join(">", _milestones);

            NetworkTestReport report = new NetworkTestReport
            {
                SchemaVersion = SchemaVersion,
                RunId = RunId,
                ScenarioId = ScenarioId,
                Role = Role.ToString(),
                Status = status,
                Milestones = new List<string>(_milestones),
                StateRevision = stateRevision,
                SharedFacts = new Dictionary<string, object>(_sharedFacts),
                RoleEvidence = new Dictionary<string, object>(_roleEvidence),
                Assertions = new List<string>(_assertions),
                Failure = failure,
                LogPath = _arguments.LogPath
            };

            _publishedReport = report;
            NetworkTestWriteResult writeResult = _resultWriter.Write(_arguments.ResultPath, report);
            if (!writeResult.Succeeded)
            {
                Debug.LogError($"[Complete] {writeResult.Failure}");
                _exitCode = 2;
            }

            Debug.Log($"[Harness:{Role}] Result published: {status}, revision={stateRevision}, assertions={_assertions.Count}.");
            StartCoroutine(QuitAfterDelay());
        }

        private IEnumerator QuitAfterDelay()
        {
            yield return new WaitForSecondsRealtime(0.75f);
            Application.Quit(_exitCode);
        }

        private void HandleLogMessage(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Exception && type != LogType.Assert)
                return;

            if (!_finished)
            {
                Fail($"An unhandled {type} was logged before completion: {condition}");
                return;
            }

            if (_publishedReport == null || _publishedReport.Status != "passed")
                return;

            RevokePublishedPass($"A late {type} was logged after Pass: {condition}");
        }

        private bool TryFindScenario(string scenarioId, out NetworkTestScenarioRegistration registration)
        {
            for (int i = 0; i < scenarioRegistrations.Length; i++)
            {
                NetworkTestScenarioRegistration candidate = scenarioRegistrations[i];
                if (!string.Equals(candidate.ScenarioId, scenarioId, StringComparison.Ordinal))
                    continue;

                if (candidate.Prefab == null)
                    break;

                registration = candidate;
                return true;
            }

            registration = default;
            return false;
        }

        private bool TryValidateConfiguration(NetworkTestConfiguration configuration, out string failure)
        {
            if (configuration == null)
            {
                failure = "Network test configuration is null.";
                return false;
            }

            if (configuration.SchemaVersion != SchemaVersion)
            {
                failure = $"Configuration schema version {configuration.SchemaVersion} is unsupported. Expected {SchemaVersion}.";
                return false;
            }

            if (!string.Equals(configuration.RunId, RunId, StringComparison.Ordinal))
            {
                failure = $"Configuration run ID '{configuration.RunId}' does not match argument run ID '{RunId}'.";
                return false;
            }

            if (!string.Equals(configuration.ScenarioId, ScenarioId, StringComparison.Ordinal))
            {
                failure = $"Configuration scenario '{configuration.ScenarioId}' does not match argument scenario '{ScenarioId}'.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(configuration.Address))
            {
                failure = "Configuration address cannot be empty.";
                return false;
            }

            if (configuration.Port < 1 || configuration.Port > ushort.MaxValue)
            {
                failure = $"Configuration port {configuration.Port} is outside the valid UDP port range.";
                return false;
            }

            if (configuration.TimeoutSeconds < 1)
            {
                failure = $"Configuration timeout must be positive. Received {configuration.TimeoutSeconds}.";
                return false;
            }

            failure = null;
            return true;
        }

        private static bool TryReadConfiguration(
            string configurationPath,
            out NetworkTestConfiguration configuration,
            out string failure)
        {
            try
            {
                string json = File.ReadAllText(configurationPath);
                configuration = JsonConvert.DeserializeObject<NetworkTestConfiguration>(json);
                failure = null;
                return true;
            }
            catch (Exception exception)
            {
                configuration = null;
                failure = $"Failed to read configuration '{configurationPath}': {exception.Message}";
                return false;
            }
        }

        private void QuitWithoutReport(string failure)
        {
            _finished = true;
            Debug.LogError(failure);
            _exitCode = 2;
            StartCoroutine(QuitAfterDelay());
        }

        private void OnDestroy()
        {
            Application.logMessageReceived -= HandleLogMessage;

            if (Current == this)
                Current = null;
        }
    }
}
