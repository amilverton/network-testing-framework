namespace Amilverton.PurrNetTesting
{
    /// <summary>
    /// Announces that one role has completed its assertions and is ready for coordinated shutdown.
    /// </summary>
    internal sealed class NetworkTestCompletionReport
    {
        public int SchemaVersion { get; set; }
        public string RunId { get; set; }
        public string ScenarioId { get; set; }
        public string Role { get; set; }
        public int StateRevision { get; set; }
    }

    /// <summary>
    /// Authorizes one completed role to disconnect after every role reaches the barrier.
    /// </summary>
    internal sealed class NetworkTestStopSignal
    {
        public int SchemaVersion { get; set; }
        public string RunId { get; set; }
        public string ScenarioId { get; set; }
        public string Role { get; set; }
    }
}
