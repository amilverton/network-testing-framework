using System;
using PurrNet;
using PurrNet.Transports;
using UnityEngine;

namespace Caffeinated.NetworkTesting
{
    /// <summary>
    /// Configures either the generated fallback root or one constrained project-authored root.
    /// </summary>
    internal static class NetworkTestNetworkRootConfigurator
    {
        public static NetworkTestNetworkRootConfigurationResult TryConfigure(
            GameObject networkRoot,
            NetworkTestNetworkRootMode mode)
        {
            if (networkRoot == null)
            {
                return NetworkTestNetworkRootConfigurationResult.Failed(
                    "Inactive PurrNet network root is not configured in the bootstrap scene.");
            }

            if (networkRoot.activeSelf)
            {
                return NetworkTestNetworkRootConfigurationResult.Failed(
                    "PurrNet network root must remain inactive until runtime rules are configured.");
            }

            if (mode == NetworkTestNetworkRootMode.GeneratedFallback)
                return TryConfigureFallback(networkRoot);

            if (mode == NetworkTestNetworkRootMode.ProjectAuthored)
                return TryConfigureAuthored(networkRoot);

            return NetworkTestNetworkRootConfigurationResult.Failed(
                $"Network root mode '{mode}' is unsupported.");
        }

        private static NetworkTestNetworkRootConfigurationResult TryConfigureFallback(GameObject networkRoot)
        {
            UDPTransport udpTransport;
            NetworkManager networkManager;
            try
            {
                udpTransport = networkRoot.AddComponent<UDPTransport>();
                networkManager = networkRoot.AddComponent<NetworkManager>();
                networkManager.startServerFlags = (StartFlags)0;
                networkManager.startClientFlags = (StartFlags)0;
                networkManager.transport = udpTransport;
            }
            catch (Exception exception)
            {
                return NetworkTestNetworkRootConfigurationResult.Failed(
                    $"Failed to create the generated PurrNet fallback root: {exception.Message}");
            }

            if (!PurrNetNetworkManagerConfigurator.TryApplyDefaultRules(networkManager, out string failure))
                return NetworkTestNetworkRootConfigurationResult.Failed(failure);

            return NetworkTestNetworkRootConfigurationResult.Passed(
                networkManager,
                udpTransport,
                null,
                null);
        }

        private static NetworkTestNetworkRootConfigurationResult TryConfigureAuthored(GameObject networkRoot)
        {
            NetworkTestBootstrapHook[] hooks =
                networkRoot.GetComponentsInChildren<NetworkTestBootstrapHook>(true);
            if (hooks.Length > 1)
            {
                return NetworkTestNetworkRootConfigurationResult.Failed(
                    $"Project-authored network root supports at most one NetworkTestBootstrapHook. " +
                    $"Found {hooks.Length}.");
            }

            NetworkTestBootstrapHook hook = hooks.Length == 1 ? hooks[0] : null;
            if (hook != null && !hook.TryPrepareNetworkRoot(networkRoot, out string prepareFailure))
                return NetworkTestNetworkRootConfigurationResult.Failed(prepareFailure);

            NetworkManager[] networkManagers = networkRoot.GetComponentsInChildren<NetworkManager>(true);
            if (networkManagers.Length != 1)
            {
                return NetworkTestNetworkRootConfigurationResult.Failed(
                    $"Project-authored network root must contain exactly one NetworkManager. " +
                    $"Found {networkManagers.Length}.");
            }

            GenericTransport[] transports = networkRoot.GetComponentsInChildren<GenericTransport>(true);
            UDPTransport[] udpTransports = networkRoot.GetComponentsInChildren<UDPTransport>(true);
            if (transports.Length != 1 || udpTransports.Length != 1)
            {
                return NetworkTestNetworkRootConfigurationResult.Failed(
                    $"Project-authored network root must contain exactly one UDPTransport and no other " +
                    $"transport. Found {udpTransports.Length} UDP transport(s) and {transports.Length} total.");
            }

            NetworkManager networkManager = networkManagers[0];
            UDPTransport udpTransport = udpTransports[0];
            if (networkManager.transport != udpTransport)
            {
                return NetworkTestNetworkRootConfigurationResult.Failed(
                    "Project-authored NetworkManager must reference its single UDPTransport.");
            }

            if (networkManager.networkRules == null)
            {
                return NetworkTestNetworkRootConfigurationResult.Failed(
                    "Project-authored NetworkManager must retain serialized NetworkRules.");
            }

            if (NetworkManager.main != null && NetworkManager.main != networkManager)
            {
                return NetworkTestNetworkRootConfigurationResult.Failed(
                    "Another PurrNet NetworkManager is already registered as NetworkManager.main.");
            }

            if (!PurrNetNetworkManagerConfigurator.TryValidateAuthoredProviderEnvelopeBeforeActivation(
                    networkManager,
                    out string providerFailure))
            {
                return NetworkTestNetworkRootConfigurationResult.Failed(providerFailure);
            }

            networkManager.startServerFlags = (StartFlags)0;
            networkManager.startClientFlags = (StartFlags)0;

            return NetworkTestNetworkRootConfigurationResult.Passed(
                networkManager,
                udpTransport,
                hook,
                networkManager.networkRules);
        }
    }

    /// <summary>
    /// Carries validated network-root components without partially mutating the bootstrap.
    /// </summary>
    internal readonly struct NetworkTestNetworkRootConfigurationResult
    {
        private NetworkTestNetworkRootConfigurationResult(
            bool succeeded,
            NetworkManager networkManager,
            UDPTransport udpTransport,
            NetworkTestBootstrapHook hook,
            NetworkRules authoredRules,
            string failure)
        {
            Succeeded = succeeded;
            NetworkManager = networkManager;
            UdpTransport = udpTransport;
            Hook = hook;
            AuthoredRules = authoredRules;
            Failure = failure;
        }

        public bool Succeeded { get; }
        public NetworkManager NetworkManager { get; }
        public UDPTransport UdpTransport { get; }
        public NetworkTestBootstrapHook Hook { get; }
        public NetworkRules AuthoredRules { get; }
        public string Failure { get; }

        public static NetworkTestNetworkRootConfigurationResult Passed(
            NetworkManager networkManager,
            UDPTransport udpTransport,
            NetworkTestBootstrapHook hook,
            NetworkRules authoredRules)
        {
            return new NetworkTestNetworkRootConfigurationResult(
                true,
                networkManager,
                udpTransport,
                hook,
                authoredRules,
                null);
        }

        public static NetworkTestNetworkRootConfigurationResult Failed(string failure)
        {
            return new NetworkTestNetworkRootConfigurationResult(
                false,
                null,
                null,
                null,
                null,
                failure);
        }
    }
}
