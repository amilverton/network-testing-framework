using Caffeinated.NetworkTesting;
using ConsumerProject.Core;
using PurrNet;

namespace ConsumerProject.NetworkTests
{
    /// <summary>
    /// Proves sender-derived mutation authority, rejection without mutation, and replication.
    /// </summary>
    [NetworkTestScenario("Consumer.ObserverMutationRejected")]
    public sealed class ConsumerObserverMutationRejectedScenario : NetworkTestScenario
    {
        private readonly SyncVar<int> _counter = new SyncVar<int>();
        private readonly ConsumerCounterRules _counterRules = new ConsumerCounterRules();

        private PlayerID? _ownerPlayer;
        private PlayerID? _observerPlayer;
        private bool _mutationSequenceStarted;
        private bool _observerRejectionConfirmed;
        private bool _ownerAcknowledged;
        private bool _observerAcknowledged;
        private bool _requestedUnauthorizedMutation;
        private bool _requestedAuthorizedMutation;
        private bool _observedObserverRejection;
        private int _acceptedMutationCount;
        private int _rejectedMutationCount;
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
                Session.AddMilestone("mutation-state-created");
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
                _mutationSequenceStarted)
            {
                return;
            }

            _mutationSequenceStarted = true;
            Session.AddMilestone("mutation-participants-confirmed");
            BeginObserverMutation(_observerPlayer.Value);
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

            Session.AddMilestone("initial-mutation-state-observed");
            Session.PublishReady();
        }

        [TargetRpc]
        private void BeginObserverMutation(PlayerID target)
        {
            if (isServer)
                return;

            if (Session.Role != NetworkTestRole.ObserverClient)
            {
                Session.Fail(
                    $"Unauthorized mutation intent was assigned to unexpected role '{Session.Role}'.");
                return;
            }

            _requestedUnauthorizedMutation = true;
            Session.AddMilestone("observer-mutation-requested");
            RequestMutation(ConsumerCounterRules.RequiredIncrement);
            RequestObserverMutationBarrier(ConsumerCounterRules.RequiredIncrement);
        }

        [ServerRpc(requireOwnership: false)]
        private void RequestMutation(int increment, RPCInfo info = default)
        {
            if (!isServer)
                return;

            if (!TryGetRegisteredRole(info.sender, out NetworkTestRole senderRole))
            {
                Session.Fail($"Mutation request arrived from unregistered player '{info.sender}'.");
                return;
            }

            if (senderRole == NetworkTestRole.ObserverClient)
            {
                if (increment != ConsumerCounterRules.RequiredIncrement ||
                    _acceptedMutationCount != 0 ||
                    _rejectedMutationCount != 0 ||
                    _observerRejectionConfirmed ||
                    _counter.value != ConsumerCounterRules.InitialValue)
                {
                    Session.Fail(
                        $"Observer mutation reached an invalid rejection boundary: " +
                        $"increment={increment}, accepted={_acceptedMutationCount}, " +
                        $"rejected={_rejectedMutationCount}, counter={_counter.value}.");
                    return;
                }

                _rejectedMutationCount++;
                Session.AddMilestone("observer-mutation-rejected");
                return;
            }

            if (senderRole != NetworkTestRole.OwnerClient ||
                !_observerRejectionConfirmed ||
                _acceptedMutationCount != 0 ||
                _rejectedMutationCount != 1 ||
                _counter.value != ConsumerCounterRules.InitialValue)
            {
                Session.Fail(
                    $"Owner mutation reached an invalid authorization boundary: " +
                    $"role={senderRole}, accepted={_acceptedMutationCount}, " +
                    $"rejected={_rejectedMutationCount}, counter={_counter.value}.");
                return;
            }

            ConsumerCounterIncrementResult result =
                _counterRules.TryIncrement(_counter.value, increment);
            if (!result.Succeeded)
            {
                Session.Fail($"Project counter rule rejected the owner request: {result.Failure}");
                return;
            }

            _acceptedMutationCount++;
            _counter.value = result.UpdatedValue;
            Session.AddMilestone("owner-mutation-accepted");

            if (_counter.value != ConsumerCounterRules.FinalValue)
            {
                Session.Fail(
                    $"Server authoritative counter was {_counter.value}; " +
                    $"expected {ConsumerCounterRules.FinalValue}.");
                return;
            }

            Session.AddMilestone("server-observed-authoritative-state");
        }

        [ServerRpc(requireOwnership: false)]
        private void RequestObserverMutationBarrier(int increment, RPCInfo info = default)
        {
            if (!isServer)
                return;

            if (!_observerPlayer.HasValue ||
                info.sender != _observerPlayer.Value ||
                increment != ConsumerCounterRules.RequiredIncrement ||
                _acceptedMutationCount != 0 ||
                _rejectedMutationCount != 1 ||
                _counter.value != ConsumerCounterRules.InitialValue)
            {
                Session.Fail(
                    $"Observer rejection barrier failed for player '{info.sender}': " +
                    $"accepted={_acceptedMutationCount}, rejected={_rejectedMutationCount}, " +
                    $"counter={_counter.value}.");
                return;
            }

            Session.AddMilestone("observer-rejection-state-verified");
            ConfirmObserverMutationRejected(info.sender, _counter.value);
        }

        [TargetRpc]
        private void ConfirmObserverMutationRejected(PlayerID target, int authoritativeCounter)
        {
            if (isServer)
                return;

            if (Session.Role != NetworkTestRole.ObserverClient ||
                authoritativeCounter != ConsumerCounterRules.InitialValue ||
                _counter.value != ConsumerCounterRules.InitialValue ||
                _counterChangeCallbacks != 0)
            {
                Session.Fail(
                    $"Observer did not see an unchanged counter at rejection: " +
                    $"role={Session.Role}, authoritative={authoritativeCounter}, " +
                    $"local={_counter.value}, callbacks={_counterChangeCallbacks}.");
                return;
            }

            _observedObserverRejection = true;
            Session.AddMilestone("observer-rejection-confirmed");
            RequestObserverRejectionConfirmed(authoritativeCounter);
        }

        [ServerRpc(requireOwnership: false)]
        private void RequestObserverRejectionConfirmed(int counter, RPCInfo info = default)
        {
            if (!isServer)
                return;

            if (!_observerPlayer.HasValue ||
                info.sender != _observerPlayer.Value ||
                _observerRejectionConfirmed ||
                counter != ConsumerCounterRules.InitialValue ||
                _counter.value != ConsumerCounterRules.InitialValue ||
                _acceptedMutationCount != 0 ||
                _rejectedMutationCount != 1)
            {
                Session.Fail(
                    $"Observer rejection acknowledgement was invalid for player '{info.sender}'.");
                return;
            }

            _observerRejectionConfirmed = true;
            Session.AddMilestone("observer-rejection-client-confirmed");
            BeginOwnerMutation(_ownerPlayer.Value);
        }

        [TargetRpc]
        private void BeginOwnerMutation(PlayerID target)
        {
            if (isServer)
                return;

            if (Session.Role != NetworkTestRole.OwnerClient ||
                _counter.value != ConsumerCounterRules.InitialValue ||
                _counterChangeCallbacks != 0)
            {
                Session.Fail(
                    $"Authorized mutation intent reached invalid owner state: " +
                    $"role={Session.Role}, counter={_counter.value}, " +
                    $"callbacks={_counterChangeCallbacks}.");
                return;
            }

            _requestedAuthorizedMutation = true;
            Session.AddMilestone("owner-mutation-requested");
            RequestMutation(ConsumerCounterRules.RequiredIncrement);
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

            Session.AddMilestone("replicated-authoritative-state-observed");
            RequestMutationObserved(counter);
        }

        [ServerRpc(requireOwnership: false)]
        private void RequestMutationObserved(int counter, RPCInfo info = default)
        {
            if (!isServer)
                return;

            if (counter != ConsumerCounterRules.FinalValue ||
                _counter.value != ConsumerCounterRules.FinalValue ||
                _acceptedMutationCount != 1 ||
                _rejectedMutationCount != 1 ||
                !TryGetRegisteredRole(info.sender, out NetworkTestRole role))
            {
                Session.Fail(
                    $"Invalid authoritative-state acknowledgement from player '{info.sender}' " +
                    $"with counter {counter}.");
                return;
            }

            if (role == NetworkTestRole.OwnerClient)
            {
                if (_ownerAcknowledged)
                {
                    Session.Fail("OwnerClient acknowledged the authoritative state more than once.");
                    return;
                }

                _ownerAcknowledged = true;
            }
            else
            {
                if (_observerAcknowledged)
                {
                    Session.Fail("ObserverClient acknowledged the authoritative state more than once.");
                    return;
                }

                _observerAcknowledged = true;
            }

            if (!_ownerAcknowledged || !_observerAcknowledged)
                return;

            Session.AddMilestone("all-roles-observed-authoritative-state");
            Session.RecordAssertion("server-derived-mutation-authority-from-rpc-sender");
            Session.RecordAssertion("server-rejected-observer-mutation-without-state-change");
            Session.RecordAssertion("server-accepted-owner-mutation-exactly-once");
            Session.RecordAssertion("server-confirmed-three-role-authoritative-state");
            Session.SetEvidence("acceptedMutationCount", _acceptedMutationCount);
            Session.SetEvidence("rejectedMutationCount", _rejectedMutationCount);
            Session.SetEvidence("authoritativeCounter", _counter.value);
            Session.SetEvidence("ownerAcknowledged", _ownerAcknowledged);
            Session.SetEvidence("observerAcknowledged", _observerAcknowledged);
            RecordSharedFacts();
            FinishMutation(_counter.value);
            Session.Pass(1);
        }

        [ObserversRpc(runLocally: false)]
        private void FinishMutation(int authoritativeCounter)
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

            Session.AddMilestone("mutation-completion-observed");
            if (Session.Role == NetworkTestRole.OwnerClient)
            {
                if (!_requestedAuthorizedMutation ||
                    _requestedUnauthorizedMutation ||
                    _observedObserverRejection)
                {
                    Session.Fail("OwnerClient mutation intent evidence was inconsistent.");
                    return;
                }

                Session.RecordAssertion("owner-sent-authorized-mutation-intent");
                Session.RecordAssertion("owner-observed-replicated-authoritative-state");
                Session.RecordAssertion("owner-observed-single-authorized-transition");
            }
            else if (Session.Role == NetworkTestRole.ObserverClient)
            {
                if (!_requestedUnauthorizedMutation ||
                    _requestedAuthorizedMutation ||
                    !_observedObserverRejection)
                {
                    Session.Fail("ObserverClient mutation rejection evidence was inconsistent.");
                    return;
                }

                Session.RecordAssertion("observer-sent-unauthorized-mutation-intent");
                Session.RecordAssertion("observer-observed-rejection-before-owner-mutation");
                Session.RecordAssertion("observer-observed-only-authorized-replicated-transition");
            }
            else
            {
                Session.Fail($"Mutation completion reached unexpected role '{Session.Role}'.");
                return;
            }

            Session.SetEvidence("attemptedUnauthorizedMutation", _requestedUnauthorizedMutation);
            Session.SetEvidence("attemptedAuthorizedMutation", _requestedAuthorizedMutation);
            Session.SetEvidence("observerRejectionConfirmed", _observedObserverRejection);
            Session.SetEvidence("observedCounter", _counter.value);
            Session.SetEvidence("counterChangeCallbacks", _counterChangeCallbacks);
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
            Session.SetFact("mutation.initial", ConsumerCounterRules.InitialValue);
            Session.SetFact("mutation.increment", ConsumerCounterRules.RequiredIncrement);
            Session.SetFact("mutation.final", ConsumerCounterRules.FinalValue);
            Session.SetFact("mutation.authorizedRole", NetworkTestRole.OwnerClient.ToString());
            Session.SetFact("mutation.unauthorizedRole", NetworkTestRole.ObserverClient.ToString());
            Session.SetFact("mutation.acceptedCount", 1);
            Session.SetFact("mutation.rejectedCount", 1);
            Session.SetFact("project.prefabProviderPreserved", true);
        }
    }
}
