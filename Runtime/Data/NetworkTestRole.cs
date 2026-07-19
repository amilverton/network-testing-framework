namespace Amilverton.PurrNetTesting
{
    /// <summary>
    /// Identifies one standalone process in a network test run.
    /// </summary>
    public enum NetworkTestRole : byte
    {
        Server,
        OwnerClient,
        ObserverClient
    }
}
