using PurrNet;
using PurrNet.Transports;
using UnityEngine;

namespace Caffeinated.NetworkTesting
{
    /// <summary>
    /// Provides one synchronous project-owned setup and cleanup boundary around a network-test role.
    /// </summary>
    public abstract class NetworkTestBootstrapHook : MonoBehaviour
    {
        [SerializeField] private bool provisionUdpNetworkRoot;
        [SerializeField] private NetworkPrefabs networkPrefabs;
        [SerializeField] private NetworkRules networkRules;

        public bool ProvisionsUdpNetworkRoot => provisionUdpNetworkRoot;
        public NetworkPrefabs ConfiguredNetworkPrefabs => networkPrefabs;
        public NetworkRules ConfiguredNetworkRules => networkRules;

        /// <summary>
        /// Request deterministic runtime provisioning for PurrNet installations whose package scripts
        /// cannot be serialized directly into a project prefab.
        /// </summary>
        public void ConfigureUdpNetworkRootForBuild(
            NetworkPrefabs configuredNetworkPrefabs,
            NetworkRules configuredNetworkRules)
        {
            if (configuredNetworkPrefabs == null)
                throw new System.ArgumentNullException(nameof(configuredNetworkPrefabs));

            if (configuredNetworkRules == null)
                throw new System.ArgumentNullException(nameof(configuredNetworkRules));

            provisionUdpNetworkRoot = true;
            networkPrefabs = configuredNetworkPrefabs;
            networkRules = configuredNetworkRules;
        }

        internal bool TryPrepareNetworkRoot(GameObject networkRoot, out string failure)
        {
            if (!provisionUdpNetworkRoot)
            {
                failure = null;
                return true;
            }

            if (networkRoot == null)
            {
                failure = "Cannot provision a null project-authored network root.";
                return false;
            }

            NetworkManager[] existingManagers =
                networkRoot.GetComponentsInChildren<NetworkManager>(true);
            GenericTransport[] existingTransports =
                networkRoot.GetComponentsInChildren<GenericTransport>(true);
            if (existingManagers.Length != 0 || existingTransports.Length != 0)
            {
                failure =
                    "A hook-provisioned project root must not also serialize a NetworkManager or transport. " +
                    $"Found {existingManagers.Length} manager(s) and {existingTransports.Length} transport(s).";
                return false;
            }

            UDPTransport udpTransport;
            NetworkManager networkManager;
            try
            {
                udpTransport = networkRoot.AddComponent<UDPTransport>();
                networkManager = networkRoot.AddComponent<NetworkManager>();
            }
            catch (System.Exception exception)
            {
                failure = $"Failed to provision the authored UDP network root: {exception.Message}";
                return false;
            }

            return PurrNetNetworkManagerConfigurator.TryApplyAuthoredConfiguration(
                networkManager,
                udpTransport,
                networkPrefabs,
                networkRules,
                out failure);
        }

        /// <summary>
        /// Configure deterministic project services after PurrNet initialization and before role start.
        /// </summary>
        public virtual void OnPreNetworkStart(
            NetworkTestBootstrap bootstrap,
            NetworkManager networkManager)
        {
        }

        /// <summary>
        /// Release project-owned role state after PurrNet reaches the disconnected state.
        /// </summary>
        public virtual void OnPostNetworkStop(
            NetworkTestBootstrap bootstrap,
            NetworkManager networkManager)
        {
        }
    }
}
