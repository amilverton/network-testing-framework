using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Caffeinated.NetworkTesting.Editor.PackageControl
{
    internal interface INetworkTestProcessRunner : IDisposable
    {
        bool IsRunning { get; }
        NetworkTestPackageActionResult TryStart(NetworkTestProcessRequest request);
        NetworkTestProcessPollResult Poll();
        void Cancel();
    }

    internal sealed class NetworkTestProcessRunner : INetworkTestProcessRunner
    {
        private readonly ConcurrentQueue<string> _outputLines =
            new ConcurrentQueue<string>();

        private Process _process;
        private string _operationName;

        public bool IsRunning => _process != null;

        public NetworkTestPackageActionResult TryStart(NetworkTestProcessRequest request)
        {
            if (request == null)
            {
                return new NetworkTestPackageActionResult(
                    false,
                    "[TryStart] Process request cannot be null.");
            }

            if (IsRunning)
            {
                return new NetworkTestPackageActionResult(
                    false,
                    $"[TryStart] '{_operationName}' is already running. Cancel or wait for it before starting another operation.");
            }

            if (string.IsNullOrWhiteSpace(request.ExecutablePath) ||
                !File.Exists(request.ExecutablePath))
            {
                return new NetworkTestPackageActionResult(
                    false,
                    $"[TryStart] Executable '{request.ExecutablePath}' does not exist.");
            }

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = request.ExecutablePath,
                Arguments = WindowsCommandLine.JoinArguments(request.Arguments),
                WorkingDirectory = request.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            Process process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = false
            };
            process.OutputDataReceived += HandleOutputDataReceived;
            process.ErrorDataReceived += HandleErrorDataReceived;

            try
            {
                if (!process.Start())
                {
                    process.Dispose();
                    return new NetworkTestPackageActionResult(
                        false,
                        $"[TryStart] Operating system did not start '{request.OperationName}'.");
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch (Win32Exception exception)
            {
                TerminateFailedProcess(process);
                return new NetworkTestPackageActionResult(
                    false,
                    $"[TryStart] Failed to start '{request.OperationName}': {exception.Message}");
            }
            catch (InvalidOperationException exception)
            {
                TerminateFailedProcess(process);
                return new NetworkTestPackageActionResult(
                    false,
                    $"[TryStart] Process configuration for '{request.OperationName}' was invalid: {exception.Message}");
            }
            catch (IOException exception)
            {
                TerminateFailedProcess(process);
                return new NetworkTestPackageActionResult(
                    false,
                    $"[TryStart] Failed to capture output for '{request.OperationName}': {exception.Message}");
            }

            _process = process;
            _operationName = request.OperationName;
            _outputLines.Enqueue($"Started '{_operationName}' as process {_process.Id}.");
            return new NetworkTestPackageActionResult(
                true,
                $"Started {_operationName}. Output will appear below.");
        }

        public NetworkTestProcessPollResult Poll()
        {
            List<string> lines = DrainOutput();
            if (_process == null)
            {
                return new NetworkTestProcessPollResult(
                    lines.ToArray(),
                    false,
                    0,
                    null);
            }

            bool hasExited;
            try
            {
                hasExited = _process.HasExited;
            }
            catch (InvalidOperationException exception)
            {
                lines.Add($"Process observation failed: {exception.Message}");
                string failedOperation = _operationName;
                DisposeProcess();
                return new NetworkTestProcessPollResult(
                    lines.ToArray(),
                    true,
                    -1,
                    failedOperation);
            }

            if (!hasExited)
            {
                return new NetworkTestProcessPollResult(
                    lines.ToArray(),
                    false,
                    0,
                    _operationName);
            }

            _process.WaitForExit();
            lines.AddRange(DrainOutput());
            int exitCode = _process.ExitCode;
            string completedOperation = _operationName;
            DisposeProcess();
            return new NetworkTestProcessPollResult(
                lines.ToArray(),
                true,
                exitCode,
                completedOperation);
        }

        public void Cancel()
        {
            if (_process == null)
                return;

            int processId = _process.Id;
            string operationName = _operationName;
            try
            {
                if (!_process.HasExited)
                    KillProcessTree(processId);

                if (!_process.WaitForExit(3000) && !_process.HasExited)
                    _process.Kill();
            }
            catch (InvalidOperationException)
            {
                // The process exited between the guards and cancellation request.
            }
            catch (Win32Exception exception)
            {
                _outputLines.Enqueue(
                    $"Cancellation warning for process {processId}: {exception.Message}");
            }

            _outputLines.Enqueue($"Cancelled '{operationName}' (process {processId}).");
            DisposeProcess();
        }

        public void Dispose()
        {
            Cancel();
        }

        private void HandleOutputDataReceived(object sender, DataReceivedEventArgs arguments)
        {
            if (arguments.Data != null)
                _outputLines.Enqueue(arguments.Data);
        }

        private void HandleErrorDataReceived(object sender, DataReceivedEventArgs arguments)
        {
            if (arguments.Data != null)
                _outputLines.Enqueue("[stderr] " + arguments.Data);
        }

        private List<string> DrainOutput()
        {
            List<string> lines = new List<string>();
            while (_outputLines.TryDequeue(out string line))
            {
                lines.Add(line);
            }

            return lines;
        }

        private static void KillProcessTree(int processId)
        {
            string windowsPath = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string taskKillPath = Path.Combine(windowsPath, "System32", "taskkill.exe");
            if (!File.Exists(taskKillPath))
                return;

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = taskKillPath,
                Arguments = $"/PID {processId} /T /F",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (Process taskKill = Process.Start(startInfo))
            {
                taskKill?.WaitForExit(3000);
            }
        }

        private static void TerminateFailedProcess(Process process)
        {
            try
            {
                if (process.Id > 0 && !process.HasExited)
                    process.Kill();
            }
            catch (InvalidOperationException)
            {
                // The process never started or exited during cleanup.
            }
            catch (Win32Exception)
            {
                // The caller still receives the original start/capture failure.
            }
            finally
            {
                process.Dispose();
            }
        }

        private void DisposeProcess()
        {
            if (_process == null)
                return;

            _process.OutputDataReceived -= HandleOutputDataReceived;
            _process.ErrorDataReceived -= HandleErrorDataReceived;
            _process.Dispose();
            _process = null;
            _operationName = null;
        }
    }

    internal static class WindowsCommandLine
    {
        public static string JoinArguments(IReadOnlyList<string> arguments)
        {
            StringBuilder commandLine = new StringBuilder();
            for (int i = 0; i < arguments.Count; i++)
            {
                if (i > 0)
                    commandLine.Append(' ');

                commandLine.Append(QuoteArgument(arguments[i] ?? string.Empty));
            }

            return commandLine.ToString();
        }

        public static string QuoteArgument(string argument)
        {
            StringBuilder quoted = new StringBuilder(argument.Length + 2);
            quoted.Append('"');
            int backslashCount = 0;
            for (int i = 0; i < argument.Length; i++)
            {
                char character = argument[i];
                if (character == '\\')
                {
                    backslashCount++;
                    continue;
                }

                if (character == '"')
                {
                    quoted.Append('\\', backslashCount * 2 + 1);
                    quoted.Append('"');
                    backslashCount = 0;
                    continue;
                }

                quoted.Append('\\', backslashCount);
                backslashCount = 0;
                quoted.Append(character);
            }

            quoted.Append('\\', backslashCount * 2);
            quoted.Append('"');
            return quoted.ToString();
        }
    }
}
