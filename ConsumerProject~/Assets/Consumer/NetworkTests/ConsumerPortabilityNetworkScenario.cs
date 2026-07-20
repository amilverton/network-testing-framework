using Amilverton.PurrNetTesting;
using ConsumerProject.Core;
using PurrNet;
using UnityEngine;

namespace ConsumerProject.NetworkTests
{
    /// <summary>
    /// Proves project assembly discovery, RPC authority, core rules, and replication.
    /// </summary>
    [NetworkTestScenario("Consumer.PortabilityCounter")]
    public sealed class ConsumerPortabilityNetworkScenario : NetworkTestScenario
    {
        private readonly SyncVar<int> _counter = new SyncVar<int>();
        private readonly ConsumerCounterRules _counterRules = new ConsumerCounterRules();

        private PlayerID? _ownerPlayer;
        private PlayerID? _observerPlayer;
        private bool _incrementDispatched;
        private bool _ownerAcknowledged;
        private bool _observerAcknowledged;
        private int _acceptedIncrementAmount;
        private int _counterChangeCallbacks;

        protected override void OnScenarioSpawned(bool asServer)
        {
            if (asServer)
            {
                if (Session.Role != NetworkTestRole.Server)
                {
                    Session.Fail($"Server fixture spawned in unexpected role '{Session.Role}'.");
                    return;
                }

                _counter.value = ConsumerCounterRules.InitialValue;
                Session.AddMilestone("counter-created");
                Session.PublishReady();
                return;
            }

            _counter.onChanged += HandleCounterChanged;
            RequestRegisterRole(Session.Role);
        }

        protected override void OnScenarioDespawned(bool asServer)
        {
            if (!asServer)
                _counter.onChanged -= HandleCounterChanged;
        }

        [ServerRpc(requireOwnership: false)]
        private void RequestRegisterRole(NetworkTestRole requestedRole, RPCInfo info = default)
        {
            if (!isServer)
                return;

            if (!TryRegisterRole(
                    requestedRole,
                    info.sender,
                    out bool newlyRegistered,
                    out string failure))
            {
                Session.Fail(failure);
                return;
            }

            if (newlyRegistered)
                ConfirmRole(info.sender, requestedRole);

            if (!_ownerPlayer.HasValue ||
                !_observerPlayer.HasValue ||
                _incrementDispatched)
            {
                return;
            }

            _incrementDispatched = true;
            Session.AddMilestone("participants-confirmed");
            BeginIncrement(_ownerPlayer.Value);
        }

        [TargetRpc]
        private void ConfirmRole(PlayerID target, NetworkTestRole confirmedRole)
        {
            if (isServer)
                return;

            if (Session.Role != confirmedRole)
            {
                Session.Fail(
                    $"Local role '{Session.Role}' did not match server-confirmed role '{confirmedRole}'.");
                return;
            }

            Session.AddMilestone("role-assigned");
            if (_counter.value != ConsumerCounterRules.InitialValue)
            {
                Session.Fail(
                    $"Role '{Session.Role}' observed counter {_counter.value} before readiness; " +
                    $"expected {ConsumerCounterRules.InitialValue}.");
                return;
            }

            Session.AddMilestone("initial-counter-observed");
            Session.PublishReady();
        }

        [TargetRpc]
        private void BeginIncrement(PlayerID target)
        {
            if (isServer)
                return;

            if (Session.Role != NetworkTestRole.OwnerClient)
            {
                Session.Fail($"Increment intent was assigned to unexpected role '{Session.Role}'.");
                return;
            }

            Session.AddMilestone("increment-requested");
            RequestIncrement(ConsumerCounterRules.RequiredIncrement);
        }

        [ServerRpc(requireOwnership: false)]
        private void RequestIncrement(int increment, RPCInfo info = default)
        {
            if (!isServer)
                return;

            if (!_ownerPlayer.HasValue || info.sender != _ownerPlayer.Value)
            {
                Session.Fail($"Increment request arrived from unauthorized player '{info.sender}'.");
                return;
            }

            ConsumerCounterIncrementResult result =
                _counterRules.TryIncrement(_counter.value, increment);
            if (!result.Succeeded)
            {
                Session.Fail($"Project counter rule rejected the owner request: {result.Failure}");
                return;
            }

            _acceptedIncrementAmount = increment;
            _counter.value = result.UpdatedValue;
            Session.AddMilestone("increment-accepted");
        }

        private void HandleCounterChanged(int counter)
        {
            if (counter == ConsumerCounterRules.InitialValue)
                return;

            _counterChangeCallbacks++;
            if (counter != ConsumerCounterRules.FinalValue || _counterChangeCallbacks != 1)
            {
                Session.Fail(
                    $"Role '{Session.Role}' observed invalid counter transition " +
                    $"value={counter}, callbacks={_counterChangeCallbacks}.");
                return;
            }

            Session.AddMilestone("replicated-counter-observed");
            RequestCounterObserved(counter);
        }

        [ServerRpc(requireOwnership: false)]
        private void RequestCounterObserved(int counter, RPCInfo info = default)
        {
            if (!isServer)
                return;

            if (counter != ConsumerCounterRules.FinalValue ||
                !TryGetRegisteredRole(info.sender, out NetworkTestRole role))
            {
                Session.Fail(
                    $"Invalid counter acknowledgement from player '{info.sender}' with value {counter}.");
                return;
            }

            if (role == NetworkTestRole.OwnerClient)
            {
                if (_ownerAcknowledged)
                {
                    Session.Fail("OwnerClient acknowledged the final counter more than once.");
                    return;
                }

                _ownerAcknowledged = true;
            }
            else
            {
                if (_observerAcknowledged)
                {
                    Session.Fail("ObserverClient acknowledged the final counter more than once.");
                    return;
                }

                _observerAcknowledged = true;
            }

            if (!_ownerAcknowledged || !_observerAcknowledged)
                return;

            Session.AddMilestone("all-clients-observed-counter");
            Session.RecordAssertion("server-derived-owner-request-from-rpc-sender");
            Session.RecordAssertion("server-applied-project-counter-rule");
            Session.RecordAssertion("server-confirmed-both-clients-observed-counter");
            Session.SetEvidence("acceptedIncrementAmount", _acceptedIncrementAmount);
            Session.SetEvidence("acknowledgedClients", 2);
            RecordSharedFacts();
            FinishCounter(ConsumerCounterRules.FinalValue);
            Session.Pass(1);
        }

        [ObserversRpc(runLocally: false)]
        private void FinishCounter(int authoritativeCounter)
        {
            if (isServer)
                return;

            if (_counter.value != authoritativeCounter ||
                authoritativeCounter != ConsumerCounterRules.FinalValue ||
                _counterChangeCallbacks != 1)
            {
                Session.Fail(
                    $"Role '{Session.Role}' finished with counter {_counter.value}, " +
                    $"authoritative counter {authoritativeCounter}, and " +
                    $"{_counterChangeCallbacks} callback(s).");
                return;
            }

            Session.AddMilestone("counter-completion-observed");
            Session.RecordAssertion("client-observed-replicated-project-counter");
            Session.SetEvidence("observedCounter", _counter.value);
            Session.SetEvidence("counterChangeCallbacks", _counterChangeCallbacks);
            Session.SetEvidence(
                "requestedIncrement",
                Session.Role == NetworkTestRole.OwnerClient);
            RecordSharedFacts();
            Session.Pass(1);
        }

        private bool TryRegisterRole(
            NetworkTestRole requestedRole,
            PlayerID player,
            out bool newlyRegistered,
            out string failure)
        {
            newlyRegistered = false;

            if (requestedRole != NetworkTestRole.OwnerClient &&
                requestedRole != NetworkTestRole.ObserverClient)
            {
                failure = $"Player '{player}' requested invalid test role '{requestedRole}'.";
                return false;
            }

            if ((_ownerPlayer.HasValue && _ownerPlayer.Value == player) ||
                (_observerPlayer.HasValue && _observerPlayer.Value == player))
            {
                NetworkTestRole existingRole = _ownerPlayer.HasValue && _ownerPlayer.Value == player
                    ? NetworkTestRole.OwnerClient
                    : NetworkTestRole.ObserverClient;
                if (existingRole != requestedRole)
                {
                    failure =
                        $"Player '{player}' attempted to change role from '{existingRole}' " +
                        $"to '{requestedRole}'.";
                    return false;
                }

                failure = null;
                return true;
            }

            if (requestedRole == NetworkTestRole.OwnerClient)
            {
                if (_ownerPlayer.HasValue)
                {
                    failure = $"OwnerClient is already registered as player '{_ownerPlayer.Value}'.";
                    return false;
                }

                _ownerPlayer = player;
            }
            else
            {
                if (_observerPlayer.HasValue)
                {
                    failure =
                        $"ObserverClient is already registered as player '{_observerPlayer.Value}'.";
                    return false;
                }

                _observerPlayer = player;
            }

            newlyRegistered = true;
            failure = null;
            return true;
        }

        private bool TryGetRegisteredRole(PlayerID player, out NetworkTestRole role)
        {
            if (_ownerPlayer.HasValue && _ownerPlayer.Value == player)
            {
                role = NetworkTestRole.OwnerClient;
                return true;
            }

            if (_observerPlayer.HasValue && _observerPlayer.Value == player)
            {
                role = NetworkTestRole.ObserverClient;
                return true;
            }

            role = NetworkTestRole.Server;
            return false;
        }

        private void RecordSharedFacts()
        {
            Session.SetFact("counter.initial", ConsumerCounterRules.InitialValue);
            Session.SetFact("counter.increment", ConsumerCounterRules.RequiredIncrement);
            Session.SetFact("counter.final", ConsumerCounterRules.FinalValue);
            Session.SetFact("counter.requesterRole", NetworkTestRole.OwnerClient.ToString());
            Session.SetFact("project.prefabProviderPreserved", true);
        }
    }
}
