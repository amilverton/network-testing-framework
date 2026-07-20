using System;
using PurrNet;
using PurrNet.Packing;

namespace Caffeinated.NetworkTesting
{
    [Serializable]
    public struct CrossPlayerDamageTestState : IPackedAuto
    {
        public int OwnerHealth;
        public int Revision;
    }

    /// <summary>
    /// Proves sender-derived damage authority, health replication, and replay rejection.
    /// </summary>
    [NetworkTestScenario("Harness.CrossPlayerDamage")]
    public sealed class CrossPlayerDamageNetworkScenario : NetworkTestScenario
    {
        private const int InitialHealth = 100;
        private const int DamageAmount = 25;
        private const string AttackNonce = "observer-hit-1";

        private readonly SyncVar<CrossPlayerDamageTestState> _state =
            new SyncVar<CrossPlayerDamageTestState>();
        private readonly TwoClientRoleRegistry _roles = new TwoClientRoleRegistry();

        private bool _attackStarted;
        private bool _ownerObservedDamage;
        private bool _observerObservedDamage;
        private bool _acceptedNonce;
        private int _acceptedMutationCount;
        private int _rejectedMutationCount;
        private int _clientChangeCount;

        protected override void OnScenarioSpawned(bool asServer)
        {
            if (asServer)
            {
                _state.value = new CrossPlayerDamageTestState
                {
                    OwnerHealth = InitialHealth,
                    Revision = 0
                };
                Session.AddMilestone("initial-health-created");
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

            if (newlyRegistered)
                ConfirmRole(info.sender, requestedRole);

            if (!_roles.AllRegistered || _attackStarted)
                return;

            _attackStarted = true;
            Session.AddMilestone("damage-participants-confirmed");
            BeginValidDamage(_roles.ObserverPlayer.Value);
        }

        [TargetRpc]
        private void ConfirmRole(PlayerID target, NetworkTestRole confirmedRole)
        {
            if (isServer)
                return;

            if (Session.Role != confirmedRole || !IsInitialState(_state.value))
            {
                Session.Fail(
                    $"Role confirmation or initial health was invalid for '{Session.Role}': " +
                    $"health={_state.value.OwnerHealth}, revision={_state.value.Revision}.");
                return;
            }

            Session.AddMilestone("initial-health-observed");
            Session.PublishReady();
        }

        [TargetRpc]
        private void BeginValidDamage(PlayerID target)
        {
            if (isServer)
                return;

            if (Session.Role != NetworkTestRole.ObserverClient)
            {
                Session.Fail($"Damage intent was assigned to unexpected role '{Session.Role}'.");
                return;
            }

            Session.AddMilestone("valid-damage-intent-sent");
            RequestDamage(AttackNonce, DamageAmount);
        }

        [ServerRpc(requireOwnership: false)]
        private void RequestDamage(string nonce, int amount, RPCInfo info = default)
        {
            if (!isServer)
                return;

            if (!_roles.ObserverPlayer.HasValue || info.sender != _roles.ObserverPlayer.Value)
            {
                Session.Fail($"Damage intent arrived from unauthorized player '{info.sender}'.");
                return;
            }

            if (nonce == AttackNonce && _acceptedNonce)
            {
                _rejectedMutationCount++;
                Session.AddMilestone("replayed-damage-rejected");
                return;
            }

            if (_acceptedNonce || nonce != AttackNonce || amount != DamageAmount || !IsInitialState(_state.value))
            {
                Session.Fail(
                    $"Damage request was invalid: nonce='{nonce}', amount={amount}, " +
                    $"health={_state.value.OwnerHealth}, revision={_state.value.Revision}.");
                return;
            }

            _acceptedNonce = true;
            _acceptedMutationCount++;
            _state.value = new CrossPlayerDamageTestState
            {
                OwnerHealth = InitialHealth - DamageAmount,
                Revision = 1
            };
            Session.AddMilestone("damage-committed-by-server");
        }

        private void HandleStateChanged(CrossPlayerDamageTestState state)
        {
            if (state.Revision < 1)
                return;

            _clientChangeCount++;
            if (!IsDamagedState(state) || _clientChangeCount != 1)
            {
                Session.Fail(
                    $"Role '{Session.Role}' observed invalid damage transition count={_clientChangeCount}, " +
                    $"health={state.OwnerHealth}, revision={state.Revision}.");
                return;
            }

            Session.AddMilestone("replicated-health-observed");
            RequestDamageObserved(state.Revision);
        }

        [ServerRpc(requireOwnership: false)]
        private void RequestDamageObserved(int revision, RPCInfo info = default)
        {
            if (!isServer)
                return;

            if (revision != 1 || !IsDamagedState(_state.value) || !_roles.TryGetRole(info.sender, out NetworkTestRole role))
            {
                Session.Fail($"Invalid damage acknowledgement from player '{info.sender}' at revision {revision}.");
                return;
            }

            if (role == NetworkTestRole.OwnerClient)
                _ownerObservedDamage = true;
            else
                _observerObservedDamage = true;

            if (!_ownerObservedDamage || !_observerObservedDamage)
                return;

            Session.AddMilestone("all-clients-observed-health");
            BeginReplayAttempt(_roles.ObserverPlayer.Value);
        }

        [TargetRpc]
        private void BeginReplayAttempt(PlayerID target)
        {
            if (isServer)
                return;

            Session.AddMilestone("replay-attempt-sent");
            RequestDamage(AttackNonce, DamageAmount);
            RequestReplayBarrier(AttackNonce);
        }

        [ServerRpc(requireOwnership: false)]
        private void RequestReplayBarrier(string nonce, RPCInfo info = default)
        {
            if (!isServer)
                return;

            if (!_roles.ObserverPlayer.HasValue || info.sender != _roles.ObserverPlayer.Value || nonce != AttackNonce)
            {
                Session.Fail("Damage replay barrier came from an unexpected sender or nonce.");
                return;
            }

            if (_acceptedMutationCount != 1 || _rejectedMutationCount != 1 || !IsDamagedState(_state.value))
            {
                Session.Fail(
                    $"Damage replay changed authority state: accepted={_acceptedMutationCount}, " +
                    $"rejected={_rejectedMutationCount}, health={_state.value.OwnerHealth}, revision={_state.value.Revision}.");
                return;
            }

            Session.AddMilestone("replay-barrier-confirmed");
            Session.RecordAssertion("server-derived-observer-attacker-from-rpc-sender");
            Session.RecordAssertion("server-committed-damage-exactly-once");
            Session.RecordAssertion("server-rejected-replayed-nonce-without-mutation");
            Session.SetEvidence("acceptedMutationCount", _acceptedMutationCount);
            Session.SetEvidence("rejectedMutationCount", _rejectedMutationCount);
            RecordSharedFacts();
            FinishDamage(_state.value);
            Session.Pass(1);
        }

        [ObserversRpc(runLocally: false)]
        private void FinishDamage(CrossPlayerDamageTestState authoritativeState)
        {
            if (isServer)
                return;

            if (!StatesMatch(_state.value, authoritativeState) || !IsDamagedState(_state.value) || _clientChangeCount != 1)
            {
                Session.Fail(
                    $"Role '{Session.Role}' did not retain the authoritative health after replay rejection.");
                return;
            }

            Session.AddMilestone("damage-completion-observed");
            Session.RecordAssertion("client-read-replicated-health-transition-once");
            Session.RecordAssertion("client-retained-health-after-replay-rejection");
            Session.SetEvidence("healthChangeCallbacks", _clientChangeCount);
            Session.SetEvidence("locallyObservedOwnerHealth", _state.value.OwnerHealth);
            RecordSharedFacts();
            Session.Pass(1);
        }

        private void RecordSharedFacts()
        {
            Session.SetFact("owner.health", InitialHealth - DamageAmount);
            Session.SetFact("damage.amount", DamageAmount);
            Session.SetFact("damage.acceptedMutations", 1);
            Session.SetFact("damage.rejectedReplays", 1);
            Session.SetFact("damage.attackerRole", NetworkTestRole.ObserverClient.ToString());
        }

        private static bool IsInitialState(CrossPlayerDamageTestState state)
        {
            return state.OwnerHealth == InitialHealth && state.Revision == 0;
        }

        private static bool IsDamagedState(CrossPlayerDamageTestState state)
        {
            return state.OwnerHealth == InitialHealth - DamageAmount && state.Revision == 1;
        }

        private static bool StatesMatch(CrossPlayerDamageTestState left, CrossPlayerDamageTestState right)
        {
            return left.OwnerHealth == right.OwnerHealth && left.Revision == right.Revision;
        }
    }
}
