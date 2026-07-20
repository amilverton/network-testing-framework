using PurrNet;

namespace Caffeinated.NetworkTesting
{
    /// <summary>
    /// Keeps the coordinator's process roles bound to server-observed PurrNet senders.
    /// </summary>
    internal sealed class TwoClientRoleRegistry
    {
        public PlayerID? OwnerPlayer { get; private set; }
        public PlayerID? ObserverPlayer { get; private set; }
        public bool AllRegistered => OwnerPlayer.HasValue && ObserverPlayer.HasValue;

        public bool TryRegister(
            NetworkTestRole requestedRole,
            PlayerID sender,
            out bool newlyRegistered,
            out string failure)
        {
            newlyRegistered = false;

            if (requestedRole == NetworkTestRole.Server)
            {
                failure = $"Player '{sender}' attempted to register as the Server role.";
                return false;
            }

            if (OwnerPlayer.HasValue && OwnerPlayer.Value == sender)
                return ValidateExistingRole(requestedRole, NetworkTestRole.OwnerClient, out failure);

            if (ObserverPlayer.HasValue && ObserverPlayer.Value == sender)
                return ValidateExistingRole(requestedRole, NetworkTestRole.ObserverClient, out failure);

            if (requestedRole == NetworkTestRole.OwnerClient)
            {
                if (OwnerPlayer.HasValue)
                {
                    failure = "More than one client attempted to register as OwnerClient.";
                    return false;
                }

                OwnerPlayer = sender;
            }
            else
            {
                if (ObserverPlayer.HasValue)
                {
                    failure = "More than one client attempted to register as ObserverClient.";
                    return false;
                }

                ObserverPlayer = sender;
            }

            if (OwnerPlayer.HasValue && ObserverPlayer.HasValue && OwnerPlayer.Value == ObserverPlayer.Value)
            {
                failure = "OwnerClient and ObserverClient resolved to the same PurrNet player.";
                return false;
            }

            newlyRegistered = true;
            failure = null;
            return true;
        }

        public bool TryGetRole(PlayerID sender, out NetworkTestRole role)
        {
            if (OwnerPlayer.HasValue && OwnerPlayer.Value == sender)
            {
                role = NetworkTestRole.OwnerClient;
                return true;
            }

            if (ObserverPlayer.HasValue && ObserverPlayer.Value == sender)
            {
                role = NetworkTestRole.ObserverClient;
                return true;
            }

            role = NetworkTestRole.Server;
            return false;
        }

        private static bool ValidateExistingRole(
            NetworkTestRole requestedRole,
            NetworkTestRole registeredRole,
            out string failure)
        {
            if (requestedRole != registeredRole)
            {
                failure = $"A registered {registeredRole} attempted to change its test role to {requestedRole}.";
                return false;
            }

            failure = null;
            return true;
        }
    }
}
