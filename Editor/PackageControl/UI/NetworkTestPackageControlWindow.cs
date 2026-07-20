using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Caffeinated.NetworkTesting.Editor.PackageControl
{
    internal sealed class NetworkTestPackageControlWindow : EditorWindow
    {
        private const int MaximumOutputLines = 2000;
        private const string MenuPath = "Tools/Caffeinated Network Testing/Package Control";

        private readonly List<string> _outputLines = new List<string>();

        private NetworkTestPackageControlService _service;
        private NetworkTestSupportReport _report;
        private Vector2 _statusScroll;
        private Vector2 _outputScroll;
        private string _lastMessage;
        private MessageType _lastMessageType = MessageType.Info;
        private string _initializationFailure;

        [MenuItem(MenuPath, priority = 1900)]
        private static void OpenWindow()
        {
            NetworkTestPackageControlWindow window =
                GetWindow<NetworkTestPackageControlWindow>();
            window.titleContent = new GUIContent("PurrNet Tests");
            window.minSize = new Vector2(620f, 520f);
            window.Show();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("PurrNet Tests");
            minSize = new Vector2(620f, 520f);
            EditorApplication.update += HandleEditorUpdate;

            try
            {
                _service = NetworkTestPackageControlService.CreateDefault();
                RefreshStatus();
            }
            catch (InvalidOperationException exception)
            {
                _initializationFailure = exception.Message;
                Debug.LogError(
                    $"[NetworkTestPackageControlWindow.OnEnable] Failed to initialize package controls: {exception.Message}");
            }
            catch (ArgumentException exception)
            {
                _initializationFailure = exception.Message;
                Debug.LogError(
                    $"[NetworkTestPackageControlWindow.OnEnable] Package paths are invalid: {exception.Message}");
            }
        }

        private void OnDisable()
        {
            EditorApplication.update -= HandleEditorUpdate;
            if (_service == null)
                return;

            _service.Dispose();
            _service = null;
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField(
                "Caffeinated Network Testing Package Control",
                EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Inspect this project, install the packaged agent skill, and run the observable multi-process suite.",
                EditorStyles.wordWrappedLabel);
            EditorGUILayout.Space(6f);

            if (!string.IsNullOrWhiteSpace(_initializationFailure))
            {
                EditorGUILayout.HelpBox(
                    "Package controls could not initialize: " + _initializationFailure,
                    MessageType.Error);
                return;
            }

            DrawStatusSection();
            EditorGUILayout.Space(8f);
            DrawActionsSection();
            EditorGUILayout.Space(8f);
            DrawOutputSection();
        }

        private void DrawStatusSection()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Prerequisites and support envelope", EditorStyles.boldLabel);
            if (GUILayout.Button("Refresh", GUILayout.Width(90f)))
                RefreshStatus();
            EditorGUILayout.EndHorizontal();

            if (_report == null)
            {
                EditorGUILayout.HelpBox("Status has not been inspected yet.", MessageType.Warning);
                return;
            }

            _statusScroll = EditorGUILayout.BeginScrollView(
                _statusScroll,
                GUILayout.MinHeight(170f),
                GUILayout.MaxHeight(250f));
            for (int i = 0; i < _report.Statuses.Count; i++)
            {
                NetworkTestSupportStatus status = _report.Statuses[i];
                MessageType messageType = GetMessageType(status.Level);
                EditorGUILayout.HelpBox(
                    $"{status.Name}: {status.Detail}",
                    messageType);
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawActionsSection()
        {
            EditorGUILayout.LabelField("Project actions", EditorStyles.boldLabel);
            bool operationRunning = _service.IsOperationRunning;

            using (new EditorGUI.DisabledScope(
                       operationRunning || _report == null || _report.ProjectManifestFound))
            {
                if (GUILayout.Button("Create ProjectSettings/PurrNetNetworkTests.json"))
                    ShowActionResult(_service.CreateProjectManifest());
            }

            using (new EditorGUI.DisabledScope(
                       operationRunning || _report == null || !_report.CanInstallSkill))
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Install / Update Agent Skill"))
                    ShowActionResult(_service.InstallOrUpdateSkill(false));

                if (GUILayout.Button("Stage Incoming Skill for Review"))
                    ShowActionResult(_service.InstallOrUpdateSkill(true));
                EditorGUILayout.EndHorizontal();
            }

            using (new EditorGUI.DisabledScope(
                       operationRunning || _report == null || !_report.CanLaunchSuite))
            {
                if (GUILayout.Button("Run Complete Interactive Suite + Live Viewer"))
                    ShowActionResult(_service.LaunchInteractiveSuite());
            }

            using (new EditorGUI.DisabledScope(!operationRunning))
            {
                if (GUILayout.Button("Cancel Active Operation"))
                {
                    _service.CancelOperation();
                    _lastMessage = "Cancellation requested for the active package operation.";
                    _lastMessageType = MessageType.Warning;
                }
            }

            if (!string.IsNullOrWhiteSpace(_lastMessage))
                EditorGUILayout.HelpBox(_lastMessage, _lastMessageType);

            EditorGUILayout.HelpBox(
                "Closing this window or recompiling scripts cancels an active operation owned by the window.",
                MessageType.Info);
        }

        private void DrawOutputSection()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("External process output", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(_outputLines.Count == 0))
            {
                if (GUILayout.Button("Clear", GUILayout.Width(90f)))
                    _outputLines.Clear();
            }
            EditorGUILayout.EndHorizontal();

            _outputScroll = EditorGUILayout.BeginScrollView(
                _outputScroll,
                GUILayout.ExpandHeight(true));
            string output = _outputLines.Count == 0
                ? "No external operation output yet."
                : string.Join(Environment.NewLine, _outputLines);
            EditorGUILayout.SelectableLabel(
                output,
                EditorStyles.textArea,
                GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
        }

        private void HandleEditorUpdate()
        {
            if (_service == null)
                return;

            NetworkTestProcessPollResult poll = _service.PollOperation();
            bool receivedOutput = poll.OutputLines.Count > 0;
            for (int i = 0; i < poll.OutputLines.Count; i++)
            {
                _outputLines.Add(poll.OutputLines[i]);
            }

            TrimOutput();
            if (poll.Completed)
            {
                bool succeeded = poll.ExitCode == 0;
                _lastMessage = succeeded
                    ? $"{poll.OperationName} completed successfully."
                    : $"{poll.OperationName} failed with exit code {poll.ExitCode}. Review the output below.";
                _lastMessageType = succeeded ? MessageType.Info : MessageType.Error;
                RefreshStatus();
            }

            if (receivedOutput || poll.Completed)
                Repaint();
        }

        private void RefreshStatus()
        {
            if (_service == null)
                return;

            _report = _service.Inspect();
            Repaint();
        }

        private void ShowActionResult(NetworkTestPackageActionResult result)
        {
            _lastMessage = result.Message;
            _lastMessageType = result.Succeeded ? MessageType.Info : MessageType.Error;
            if (result.Succeeded)
                RefreshStatus();
        }

        private void TrimOutput()
        {
            if (_outputLines.Count <= MaximumOutputLines)
                return;

            int removeCount = _outputLines.Count - MaximumOutputLines;
            _outputLines.RemoveRange(0, removeCount);
        }

        private static MessageType GetMessageType(NetworkTestSupportLevel level)
        {
            switch (level)
            {
                case NetworkTestSupportLevel.Supported:
                    return MessageType.Info;
                case NetworkTestSupportLevel.Warning:
                    return MessageType.Warning;
                default:
                    return MessageType.Error;
            }
        }
    }
}
