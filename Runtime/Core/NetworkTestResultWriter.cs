using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace Amilverton.PurrNetTesting
{
    /// <summary>
    /// Writes protocol JSON to a temporary sibling before publishing it atomically.
    /// </summary>
    public sealed class NetworkTestResultWriter
    {
        private static readonly UTF8Encoding Utf8WithoutByteOrderMark = new UTF8Encoding(false);

        public NetworkTestWriteResult Write(string path, object value)
        {
            if (string.IsNullOrWhiteSpace(path))
                return NetworkTestWriteResult.Failed("Output path cannot be empty.");

            if (value == null)
                return NetworkTestWriteResult.Failed("Output value cannot be null.");

            string fullPath = Path.GetFullPath(path);
            string directoryPath = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(directoryPath))
                return NetworkTestWriteResult.Failed($"Output path '{fullPath}' has no parent directory.");

            string temporaryPath = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");

            try
            {
                Directory.CreateDirectory(directoryPath);
                string json = JsonConvert.SerializeObject(value, Formatting.Indented);
                File.WriteAllText(temporaryPath, json, Utf8WithoutByteOrderMark);

                if (File.Exists(fullPath))
                    File.Replace(temporaryPath, fullPath, null);
                else
                    File.Move(temporaryPath, fullPath);

                return NetworkTestWriteResult.Passed();
            }
            catch (Exception exception)
            {
                TryDeleteTemporaryFile(temporaryPath);
                return NetworkTestWriteResult.Failed($"Failed to publish '{fullPath}': {exception.Message}");
            }
        }

        private static void TryDeleteTemporaryFile(string temporaryPath)
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
                // The original write failure remains the actionable error.
            }
        }
    }

    /// <summary>
    /// Describes whether a protocol file was published successfully.
    /// </summary>
    public readonly struct NetworkTestWriteResult
    {
        private NetworkTestWriteResult(bool succeeded, string failure)
        {
            Succeeded = succeeded;
            Failure = failure;
        }

        public bool Succeeded { get; }
        public string Failure { get; }

        public static NetworkTestWriteResult Passed()
        {
            return new NetworkTestWriteResult(true, null);
        }

        public static NetworkTestWriteResult Failed(string failure)
        {
            return new NetworkTestWriteResult(false, failure);
        }
    }
}
