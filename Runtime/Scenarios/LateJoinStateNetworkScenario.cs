using System;
using PurrNet;
using PurrNet.Packing;

namespace Amilverton.PurrNetTesting
{
    [Serializable]
    public struct LateJoinTestState : IPackedAuto
    {
        public int Value;
        public int Revision;
    }

    /// <summary>
    /// Proves a late observer receives the committed snapshot and then a live delta.
    /// </summary>
    [NetworkTestScenario("Harness.LateJoinState")]
    public sealed class LateJoinStateNetworkScenario : NetworkTestScenario
    {
        private readonly SyncVar<LateJoinTestState> _state = new SyncVar<LateJoinTestState>();
        private readonly TwoClientRoleRegistry _roles = new TwoClientRoleRegistry();

        private bool _ownerObservedFirstRevision;
        private bool _observerConfirmed;
        private bool _secondRevisionCommitted;
        private bool _ownerObservedSecondRevision;
        private bool _observerObservedSecondRevision;
        private int _firstSemanticRevision = -1;
        private int _postConfirmationDeltaCount;

        protected override void OnScenarioSpawned(bool asServer)
        {
            if (asServer)
            {
                _state.value = new LateJoinTestState { Value = 10, Revision = 0 };
                Session.AddMilestone("revision-zero-created");
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
                ConfirmOwnerAndBegin(info.sender);
                return;
            }

            TryConfirmLateObserver();
        }

        [TargetRpc]
        private void ConfirmOwnerAndBegin(PlayerID target)
        {
            if (isServer)
                return;

            if (Session.Role != NetworkTestRole.OwnerClient || !IsRevision(_state.value, 0, 10))
            {
                Session.Fail(
                    $"OwnerClient did not begin from revision 0/value 10. " +
                    $"Observed revision={_state.value.Revision}, value={_state.value.Value}.");
                return;
            }

            _firstSemanticRevision = 0;
            Session.AddMilestone("owner-observed-revision-zero");
            RequestCommitFirstRevision();
        }

        [ServerRpc(requireOwnership: false)]
        private void RequestCommitFirstRevision(RPCInfo info = default)
        {
            if (!isServer)
                return;

            if (!_roles.OwnerPlayer.HasValue || info.sender != _roles.OwnerPlayer.Value || !IsRevision(_state.value, 0, 10))
            {
                Session.Fail("First late-join revision request came from an invalid sender or server state.");
                return;
            }

            _state.value = new LateJoinTestState { Value = 20, Revision = 1 };
            Session.AddMilestone("revision-one-committed-before-late-join");
        }

        private void HandleStateChanged(LateJoinTestState state)
        {
            if (Session.Role == NetworkTestRole.OwnerClient && state.Revision == 1)
            {
                if (!IsRevision(state, 1, 20))
                {
                    Session.Fail("OwnerClient observed an invalid first committed revision.");
                    return;
                }

                Session.AddMilestone("owner-observed-prejoin-revision");
                Session.PublishReady();
                RequestFirstRevisionObserved();
                return;
            }

            if (state.Revision != 2)
                return;

            _postConfirmationDeltaCount++;
            if (!IsRevision(state, 2, 30) || _postConfirmationDeltaCount != 1)
            {
                Session.Fail(
                    $"Role '{Session.Role}' observed invalid post-join delta count={_postConfirmationDeltaCount}, " +
                    $"revision={state.Revision}, value={state.Value}.");
                return;
            }

            Session.AddMilestone("post-join-delta-observed");
            RequestSecondRevisionObserved();
        }

        [ServerRpc(requireOwnership: false)]
        private void RequestFirstRevisionObserved(RPCInfo info = default)
        {
            if (!isServer)
                return;

            if (!_roles.OwnerPlayer.HasValue || info.sender != _roles.OwnerPlayer.Value || !IsRevision(_state.value, 1, 20))
            {
                Session.Fail("Pre-join revision acknowledgement was invalid.");
                return;
            }

            _ownerObservedFirstRevision = true;
            Session.AddMilestone("owner-prejoin-checkpoint-confirmed");
            TryConfirmLateObserver();
        }

        private void TryConfirmLateObserver()
        {
            if (_observerConfirmed || !_ownerObservedFirstRevision || !_roles.ObserverPlayer.HasValue)
                return;

            if (!IsRevision(_state.value, 1, 20))
            {
                Session.Fail("Authoritative state changed before the late observer snapshot was confirmed.");
                return;
            }

            _observerConfirmed = true;
            ConfirmLateObserver(_roles.ObserverPlayer.Value);
        }

        [TargetRpc]
        private void ConfirmLateObserver(PlayerID target)
        {
            if (isServer)
                return;

            if (Session.Role != NetworkTestRole.ObserverClient)
            {
                Session.Fail($"Late-join confirmation reached unexpected role '{Session.Role}'.");
                return;
            }

            _firstSemanticRevision = _state.value.Revision;
            if (!IsRevision(_state.value, 1, 20))
            {
                Session.Fail(
                    $"Late observer's first semantic snapshot was revision {_state.value.Revision}, " +
                    $"value {_state.value.Value}; expected revision 1/value 20.");
                return;
            }

            Session.AddMilestone("late-observer-read-revision-one-snapshot");
            Session.PublishReady();
            RequestLateSnapshotObserved(_firstSemanticRevision);
        }

        [ServerRpc(requireOwnership: false)]
        private void RequestLateSnapshotObserved(int firstRevision, RPCInfo info = default)
        {
            if (!isServer)
                return;

            if (!_roles.ObserverPlayer.HasValue || info.sender != _roles.ObserverPlayer.Value ||
                firstRevision != 1 || !IsRevision(_state.value, 1, 20) || _secondRevisionCommitted)
            {
                Session.Fail("Late observer snapshot acknowledgement was invalid or duplicated.");
                return;
            }

            _secondRevisionCommitted = true;
            Session.AddMilestone("late-snapshot-confirmed-by-server");
            _state.value = new LateJoinTestState { Value = 30, Revision = 2 };
            Session.AddMilestone("revision-two-committed-after-late-join");
        }

        [ServerRpc(requireOwnership: false)]
        private void RequestSecondRevisionObserved(RPCInfo info = default)
        {
            if (!isServer)
                return;

            if (!IsRevision(_state.value, 2, 30) || !_roles.TryGetRole(info.sender, out NetworkTestRole role))
            {
                Session.Fail($"Post-join delta acknowledgement from '{info.sender}' was invalid.");
                return;
            }

            if (role == NetworkTestRole.OwnerClient)
                _ownerObservedSecondRevision = true;
            else
                _observerObservedSecondRevision = true;

            if (!_ownerObservedSecondRevision || !_observerObservedSecondRevision)
                return;

            Session.AddMilestone("all-clients-observed-postjoin-delta");
            Session.RecordAssertion("server-committed-revision-one-before-observer-registration");
            Session.RecordAssertion("server-confirmed-late-snapshot-before-revision-two");
            RecordSharedFacts();
            Session.SetEvidence("authoritativeRevisionCommits", 2);
            FinishLateJoin(_state.value);
            Session.Pass(2);
        }

        [ObserversRpc(runLocally: false)]
        private void FinishLateJoin(LateJoinTestState authoritativeState)
        {
            if (isServer)
                return;

            if (!StatesMatch(_state.value, authoritativeState) || !IsRevision(_state.value, 2, 30))
            {
                Session.Fail($"Role '{Session.Role}' did not finish on the authoritative late-join state.");
                return;
            }

            if (Session.Role == NetworkTestRole.ObserverClient && _firstSemanticRevision != 1)
            {
                Session.Fail($"Late observer first read revision {_firstSemanticRevision}; expected 1.");
                return;
            }

            Session.AddMilestone("late-join-completion-observed");
            Session.RecordAssertion("client-read-postjoin-delta-exactly-once");
            Session.RecordAssertion(Session.Role == NetworkTestRole.ObserverClient
                ? "late-observer-first-semantic-read-was-revision-one"
                : "owner-observed-prejoin-and-postjoin-revisions");
            Session.SetEvidence("firstSemanticRevision", _firstSemanticRevision);
            Session.SetEvidence("postConfirmationDeltaCallbacks", _postConfirmationDeltaCount);
            RecordSharedFacts();
            Session.Pass(2);
        }

        private void RecordSharedFacts()
        {
            Session.SetFact("lateJoin.firstSnapshotRevision", 1);
            Session.SetFact("lateJoin.firstSnapshotValue", 20);
            Session.SetFact("lateJoin.finalRevision", 2);
            Session.SetFact("lateJoin.finalValue", 30);
        }

        private static bool IsRevision(LateJoinTestState state, int revision, int value)
        {
            return state.Revision == revision && state.Value == value;
        }

        private static bool StatesMatch(LateJoinTestState left, LateJoinTestState right)
        {
            return left.Value == right.Value && left.Revision == right.Revision;
        }
    }
}
