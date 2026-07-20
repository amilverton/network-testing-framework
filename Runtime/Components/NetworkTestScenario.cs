using PurrNet;
using UnityEngine;

namespace Caffeinated.NetworkTesting
{
    /// <summary>
    /// Base component for a code-driven network fixture generated into the test Player.
    /// </summary>
    public abstract class NetworkTestScenario : NetworkIdentity
    {
        protected NetworkTestBootstrap Session => NetworkTestBootstrap.Current;

        protected sealed override void OnSpawned(bool asServer)
        {
            NetworkTestBootstrap session = Session;
            if (session == null)
            {
                Debug.LogError("[OnSpawned] No active NetworkTestBootstrap is available for the scenario.");
                return;
            }

            OnScenarioSpawned(asServer);
        }

        protected sealed override void OnDespawned(bool asServer)
        {
            OnScenarioDespawned(asServer);
        }

        /// <summary>
        /// Start role-specific fixture work after PurrNet has spawned this identity.
        /// </summary>
        protected abstract void OnScenarioSpawned(bool asServer);

        /// <summary>
        /// Release role-specific subscriptions owned by this fixture.
        /// </summary>
        protected virtual void OnScenarioDespawned(bool asServer)
        {
        }
    }
}
