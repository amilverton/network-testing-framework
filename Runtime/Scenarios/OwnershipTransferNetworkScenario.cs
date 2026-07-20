using System;
using System.Collections;
using PurrNet;
using PurrNet.Packing;
using UnityEngine;

namespace Caffeinated.NetworkTesting
{
    [Serializable]
    public struct OwnershipTransferTestState : IPackedAuto
    {
        public int Value;
        public int Revision;
        public int AuthorityEpoch;
    }

    /// <summary>
    /// Proves ownership grant, handoff, new-owner authority, and former-owner revocation.
    /// </summary>
    [NetworkTestScenario("Harness.OwnershipTransfer")]
    public sealed class OwnershipTransferNetworkScenario : NetworkTestScenario
    {
        private const float OwnershipWaitSeconds = 8f;

        private readonly SyncVar<OwnershipTransferTestState> _state =
            new SyncVar<OwnershipTransferTestState>();
        private readonly TwoClientRoleRegistry _roles = new TwoClientRoleRegistry();

        private bool _ownerReady;
        private bool _observerReady;
        private bool _epochOneStarted;
        private bool _ownerSawRevisionOne;
        private bool _observerSawRevisionOne;
        private bool _epochTwoStarted;
        private bool _ownerSawRevisionTwo;
        private bool _observerSawRevisionTwo;
        private bool _formerOwnerAttemptStarted;
        private int _acceptedOwnedMutations;
        private int _unauthorizedOwnedExecutions;
        private int _clientChangeCount;

        protected override void OnScenarioSpawned(bool asServer)
        {
            if (asServer)
            {
                _state.value = new OwnershipTransferTestState
                {
                    Value = 0,
                    Revision = 0,
                    AuthorityEpoch = 0
                };
                Session.AddMilestone("ownership-state-created");
                Session.PublishReady();
                return;
            }

            _state.onChanged += HandleStateChanged;
            RequestRegisterRole(Session.Role);
        }

        protected override void OnScenarioDespawned(bool asServer)
        {
            if (!asServer)
                _state.onChanged -= HandleStateChanged;
        }

        [ServerRpc(requireOwnership: false)]
        private void RequestRegisterRole(NetworkTestRole requestedRole, RPCInfo info = default)
        {
            if (!isServer)
                return;

            if (!_roles.TryRegister(requestedRole, info.sender, out bool newlyRegistered, out string failure))
            {
                Session.Fail(failure);
                return;
            }

            if (!newlyRegistered)
                return;

            if (requestedRole == NetworkTestRole.OwnerClient)
            {
                GiveOwnership(info.sender);
                Session.AddMilestone("epoch-one-owner-granted");
            }

            ConfirmRole(info.sender, requestedRole);
        }

        [TargetRpc]
        private void ConfirmRole(PlayerID target, NetworkTestRole confirmedRole)
        {
            if (isServer)
                return;

            if (Session.Role != confirmedRole)
            {
                Session.Fail($"Coordinator role '{Session.Role}' does not match server-confirmed role '{confirmedRole}'.");
                return;
            }

            StartCoroutine(WaitForInitialRoleState(confirmedRole));
        }

        private IEnumerator WaitForInitialRoleState(NetworkTestRole role)
        {
            float deadline = Time.realtimeSinceStartup + OwnershipWaitSeconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                bool authorityReady = role != NetworkTestRole.OwnerClient || isOwner;
                if (authorityReady && IsState(_state.value, 0, 0, 0))
                {
                    Session.AddMilestone(role == NetworkTestRole.OwnerClient
                        ? "epoch-one-owner-authority-observed"
                        : "observer-initial-ownership-state-observed");
                    Session.PublishReady();
                    RequestClientReady(role);
                    yield break;
                }

                yield return null;
            }

            Session.Fail($"Role '{role}' did not observe initial ownership state within {OwnershipWaitSeconds} seconds.");
        }

        [ServerRpc(requireOwnership: false)]
        private void RequestClientReady(NetworkTestRole role, RPCInfo info = default)
        {
            if (!isServer)
                return;

            if (!_roles.TryGetRole(info.sender, out NetworkTestRole registeredRole) || registeredRole != role)
            {
                Session.Fail("Ownership readiness arrived from a mismatched client role.");
                return;
            }

            if (role == NetworkTestRole.OwnerClient)
                _ownerReady = true;
            else
                _observerReady = true;

            if (!_ownerReady || !_observerReady || _epochOneStarted)
                return;

            _epochOneStarted = true;
            BeginOwnedMutation(_roles.OwnerPlayer.Value, 1);
        }

        [TargetRpc]
        private void BeginOwnedMutation(PlayerID target, int authorityEpoch)
        {
            if (isServer)
                return;

            StartCoroutine(WaitForOwnershipAndMutate(authorityEpoch));
        }

        private IEnumerator WaitForOwnershipAndMutate(int authorityEpoch)
        {
            float deadline = Time.realtimeSinceStartup + OwnershipWaitSeconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (isOwner)
                {
                    Session.AddMilestone($"epoch-{authorityEpoch}-owned-request-sent");
                    RequestOwnedIncrement(authorityEpoch, Session.RunId + ":epoch:" + authorityEpoch);
                    yield break;
                }

                yield return null;
            }

            Session.Fail($"Role '{Session.Role}' did not receive ownership for epoch {authorityEpoch}.");
        }

        [ServerRpc(requireOwnership: true)]
        private void RequestOwnedIncrement(int authorityEpoch, string nonce, RPCInfo info = default)
        {
            if (!isServer)
                return;

            if (authorityEpoch == 3)
            {
                _unauthorizedOwnedExecutions++;
                Session.Fail("Former owner's protected epoch-three RPC executed on the server.");
                return;
            }

            PlayerID? expectedSender = authorityEpoch == 1 ? _roles.OwnerPlayer : _roles.ObserverPlayer;
            OwnershipTransferTestState current = _state.value;
            if (!expectedSender.HasValue || info.sender != expectedSender.Value ||
                nonce != Session.RunId + ":epoch:" + authorityEpoch ||
                current.Revision != authorityEpoch - 1 || _acceptedOwnedMutations != authorityEpoch - 1)
            {
                Session.Fail(
                    $"Owned mutation epoch {authorityEpoch} failed sender/nonce/state validation. " +
                    $"Sender={info.sender}, revision={current.Revision}, accepted={_acceptedOwnedMutations}.");
                return;
            }

            _acceptedOwnedMutations++;
            _state.value = new OwnershipTransferTestState
            {
                Value = current.Value + 10,
                Revision = current.Revision + 1,
                AuthorityEpoch = authorityEpoch
            };
            Session.AddMilestone($"epoch-{authorityEpoch}-owned-mutation-accepted");
        }

        private void HandleStateChanged(OwnershipTransferTestState state)
        {
            if (state.Revision < 1)
                return;

            _clientChangeCount++;
            if (!IsState(state, state.Revision * 10, state.Revision, state.Revision) ||
                state.Revision > 2 || _clientChangeCount != state.Revision)
            {
                Session.Fail(
                    $"Role '{Session.Role}' observed invalid ownership state transition " +
                    $"value={state.Value}, revision={state.Revision}, epoch={state.AuthorityEpoch}, callbacks={_clientChangeCount}.");
                return;
            }

            Session.AddMilestone($"ownership-revision-{state.Revision}-observed");
            RequestRevisionObserved(state.Revision);
        }

        [ServerRpc(requireOwnership: false)]
        private void RequestRevisionObserved(int revision, RPCInfo info = default)
        {
            if (!isServer)
                return;

            if (!_roles.TryGetRole(info.sender, out NetworkTestRole role) || revision != _state.value.Revision)
            {
                Session.Fail($"Ownership revision acknowledgement from '{info.sender}' was invalid.");
                return;
            }

            if (revision == 1)
            {
                if (role == NetworkTestRole.OwnerClient)
                    _ownerSawRevisionOne = true;
                else
                    _observerSawRevisionOne = true;

                if (_ownerSawRevisionOne && _observerSawRevisionOne && !_epochTwoStarted)
                    BeginEpochTwo();
                return;
            }

            if (revision != 2)
            {
                Session.Fail($"Unexpected ownership revision acknowledgement {revision}.");
                return;
            }

            if (role == NetworkTestRole.OwnerClient)
                _ownerSawRevisionTwo = true;
            else
                _observerSawRevisionTwo = true;

            if (_ownerSawRevisionTwo && _observerSawRevisionTwo && !_formerOwnerAttemptStarted)
            {
                _formerOwnerAttemptStarted = true;
                BeginFormerOwnerAttempt(_roles.OwnerPlayer.Value);
            }
        }

        private void BeginEpochTwo()
        {
            _epochTwoStarted = true;
            GiveOwnership(_roles.ObserverPlayer.Value);
            Session.AddMilestone("ownership-transferred-to-observer");
            BeginOwnedMutation(_roles.ObserverPlayer.Value, 2);
        }

        [TargetRpc]
        private void BeginFormerOwnerAttempt(PlayerID target)
        {
            if (isServer)
                return;

            if (Session.Role != NetworkTestRole.OwnerClient || isOwner)
            {
                Session.Fail("Former-owner rejection check started before ownership was revoked.");
                return;
            }

            Session.AddMilestone("former-owner-protected-request-attempted");
            RequestOwnedIncrement(3, Session.RunId + ":epoch:3");
            RequestFormerOwnerBarrier(Session.RunId + ":former-owner-barrier");
        }

        [ServerRpc(requireOwnership: false)]
        private void RequestFormerOwnerBarrier(string barrier, RPCInfo info = default)
        {
            if (!isServer)
                return;

            if (!_roles.OwnerPlayer.HasValue || info.sender != _roles.OwnerPlayer.Value ||
                barrier != Session.RunId + ":former-owner-barrier")
            {
                Session.Fail("Former-owner barrier came from an invalid sender or run token.");
                return;
            }

            if (_acceptedOwnedMutations != 2 || _unauthorizedOwnedExecutions != 0 || !IsState(_state.value, 20, 2, 2))
            {
                Session.Fail(
                    $"Former-owner attempt changed authority state: accepted={_acceptedOwnedMutations}, " +
                    $"unauthorizedExecutions={_unauthorizedOwnedExecutions}, revision={_state.value.Revision}.");
                return;
            }

            Session.AddMilestone("former-owner-rejection-barrier-confirmed");
            Session.RecordAssertion("server-accepted-exactly-one-mutation-per-owner-epoch");
            Session.RecordAssertion("server-did-not-execute-former-owner-protected-rpc");
            Session.SetEvidence("acceptedOwnedMutations", _acceptedOwnedMutations);
            Session.SetEvidence("unauthorizedOwnedExecutions", _unauthorizedOwnedExecutions);
            RecordSharedFacts();
            FinishOwnershipTransfer(_state.value);
            Session.Pass(2);
        }

        [ObserversRpc(runLocally: false)]
        private void FinishOwnershipTransfer(OwnershipTransferTestState authoritativeState)
        {
            if (isServer)
                return;

            if (!StatesMatch(_state.value, authoritativeState) || !IsState(_state.value, 20, 2, 2) || _clientChangeCount != 2)
            {
                Session.Fail($"Role '{Session.Role}' did not retain the final ownership-transfer state.");
                return;
            }

            bool expectedOwnership = Session.Role == NetworkTestRole.ObserverClient;
            if (isOwner != expectedOwnership)
            {
                Session.Fail($"Role '{Session.Role}' final ownership was '{isOwner}', expected '{expectedOwnership}'.");
                return;
            }

            Session.AddMilestone("ownership-transfer-completion-observed");
            Session.RecordAssertion("client-observed-two-ordered-authority-epochs");
            Session.RecordAssertion("client-observed-final-observer-ownership");
            Session.SetEvidence("stateChangeCallbacks", _clientChangeCount);
            Session.SetEvidence("isFinalOwner", isOwner);
            RecordSharedFacts();
            Session.Pass(2);
        }

        private void RecordSharedFacts()
        {
            Session.SetFact("ownership.acceptedMutations", 2);
            Session.SetFact("ownership.finalOwnerRole", NetworkTestRole.ObserverClient.ToString());
            Session.SetFact("ownership.finalValue", 20);
            Session.SetFact("ownership.formerOwnerExecuted", false);
        }

        private static bool IsState(OwnershipTransferTestState state, int value, int revision, int epoch)
        {
            return state.Value == value && state.Revision == revision && state.AuthorityEpoch == epoch;
        }

        private static bool StatesMatch(OwnershipTransferTestState left, OwnershipTransferTestState right)
        {
            return left.Value == right.Value &&
                   left.Revision == right.Revision &&
                   left.AuthorityEpoch == right.AuthorityEpoch;
        }
    }
}
