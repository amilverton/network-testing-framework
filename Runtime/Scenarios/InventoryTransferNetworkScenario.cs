using System;
using PurrNet;
using PurrNet.Packing;
using UnityEngine;

namespace Amilverton.PurrNetTesting
{
    /// <summary>
    /// Replicated inventory facts used by the built-in authority scenario.
    /// </summary>
    [Serializable]
    public struct InventoryTransferTestState : IPackedAuto
    {
        public int PlayerWoodAmount;
        public int ContainerWoodAmount;
        public int Revision;
    }

    /// <summary>
    /// Proves accepted owner intent, observer replication, and rejected non-owner intent.
    /// </summary>
    [NetworkTestScenario("Harness.InventoryTransfer")]
    public sealed class InventoryTransferNetworkScenario : NetworkTestScenario
    {
        private const string WoodItemId = "wood";
        private const int TransferAmount = 3;
        private const int TargetSlot = 0;

        private readonly SyncVar<InventoryTransferTestState> _state =
            new SyncVar<InventoryTransferTestState>();

        private PlayerID? _ownerPlayer;
        private PlayerID? _observerPlayer;
        private bool _rolesConfirmed;
        private bool _acceptedTransferComplete;
        private bool _ownerObservedAcceptedState;
        private bool _observerObservedAcceptedState;
        private bool _acceptedStateReportedByClient;

        protected override void OnScenarioSpawned(bool asServer)
        {
            if (asServer)
            {
                StartServerScenario();
                return;
            }

            StartClientScenario();
        }

        protected override void OnScenarioDespawned(bool asServer)
        {
            if (asServer)
                return;

            _state.onChanged -= HandleStateChanged;
        }

        private void StartServerScenario()
        {
            if (Session.Role != NetworkTestRole.Server)
            {
                Session.Fail($"Server fixture spawned in unexpected role '{Session.Role}'.");
                return;
            }

            _state.value = new InventoryTransferTestState
            {
                PlayerWoodAmount = TransferAmount,
                ContainerWoodAmount = 0,
                Revision = 0
            };

            Session.AddMilestone("initial-state-created");
            Session.PublishReady();
        }

        private void StartClientScenario()
        {
            if (Session.Role == NetworkTestRole.Server)
            {
                Session.Fail("A client-side fixture cannot use the Server process role.");
                return;
            }

            _state.onChanged += HandleStateChanged;
            RequestRegisterRole(Session.Role);
        }

        [ServerRpc(requireOwnership: false)]
        private void RequestRegisterRole(NetworkTestRole requestedRole, RPCInfo info = default)
        {
            if (!isServer)
                return;

            if (requestedRole == NetworkTestRole.Server)
            {
                Session.Fail($"Player '{info.sender}' attempted to register as the Server role.");
                return;
            }

            if (_ownerPlayer.HasValue && _ownerPlayer.Value == info.sender && requestedRole != NetworkTestRole.OwnerClient)
            {
                Session.Fail($"Player '{info.sender}' attempted to change its registered test role.");
                return;
            }

            if (_observerPlayer.HasValue && _observerPlayer.Value == info.sender && requestedRole != NetworkTestRole.ObserverClient)
            {
                Session.Fail($"Player '{info.sender}' attempted to change its registered test role.");
                return;
            }

            if (requestedRole == NetworkTestRole.OwnerClient)
            {
                if (_ownerPlayer.HasValue && _ownerPlayer.Value != info.sender)
                {
                    Session.Fail("More than one client attempted to register as OwnerClient.");
                    return;
                }

                _ownerPlayer = info.sender;
            }
            else
            {
                if (_observerPlayer.HasValue && _observerPlayer.Value != info.sender)
                {
                    Session.Fail("More than one client attempted to register as ObserverClient.");
                    return;
                }

                _observerPlayer = info.sender;
            }

            ConfirmRole(info.sender, requestedRole);
            ConfirmRolesWhenReady();
        }

        private void ConfirmRolesWhenReady()
        {
            if (_rolesConfirmed)
                return;

            if (!_ownerPlayer.HasValue || !_observerPlayer.HasValue)
                return;

            if (_ownerPlayer.Value == _observerPlayer.Value)
            {
                Session.Fail("OwnerClient and ObserverClient resolved to the same PurrNet player.");
                return;
            }

            _rolesConfirmed = true;
            Session.AddMilestone("client-roles-confirmed");

            BeginAcceptedTransfer(_ownerPlayer.Value);
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

            InventoryTransferTestState state = _state.value;
            if (!IsInitialState(state))
            {
                Session.Fail(
                    $"Role '{Session.Role}' did not receive the initial inventory state. " +
                    $"Player={state.PlayerWoodAmount}, container={state.ContainerWoodAmount}, revision={state.Revision}.");
                return;
            }

            Session.AddMilestone("role-assigned");
            Session.AddMilestone("initial-state-observed");
            Session.PublishReady();
        }

        [TargetRpc]
        private void BeginAcceptedTransfer(PlayerID target)
        {
            if (isServer)
                return;

            if (Session.Role != NetworkTestRole.OwnerClient)
            {
                Session.Fail($"Accepted transfer was assigned to unexpected role '{Session.Role}'.");
                return;
            }

            Session.AddMilestone("owner-request-sent");
            RequestGiveItem(WoodItemId, TransferAmount, TargetSlot);
        }

        [ServerRpc(requireOwnership: false)]
        private void RequestGiveItem(string itemId, int amount, int targetSlot, RPCInfo info = default)
        {
            if (!isServer)
                return;

            if (!_ownerPlayer.HasValue || !_observerPlayer.HasValue)
            {
                Session.Fail("A transfer request arrived before both client roles were registered.");
                return;
            }

            if (info.sender == _observerPlayer.Value)
            {
                RejectObserverRequest(itemId, amount, targetSlot);
                return;
            }

            if (info.sender != _ownerPlayer.Value)
            {
                Session.Fail($"Transfer request arrived from unregistered player '{info.sender}'.");
                return;
            }

            if (_acceptedTransferComplete)
            {
                Session.Fail("OwnerClient submitted the accepted transfer more than once.");
                return;
            }

            if (!HasExpectedRequest(itemId, amount, targetSlot))
            {
                Session.Fail(
                    $"OwnerClient submitted invalid transfer data: item='{itemId}', amount={amount}, slot={targetSlot}.");
                return;
            }

            InventoryTransferTestState current = _state.value;
            if (!IsInitialState(current))
            {
                Session.Fail(
                    $"Authoritative inventory was not in its initial state before transfer. " +
                    $"Player={current.PlayerWoodAmount}, container={current.ContainerWoodAmount}, revision={current.Revision}.");
                return;
            }

            InventoryTransferTestState updated = new InventoryTransferTestState
            {
                PlayerWoodAmount = 0,
                ContainerWoodAmount = TransferAmount,
                Revision = current.Revision + 1
            };

            _state.value = updated;
            _acceptedTransferComplete = true;
            Session.AddMilestone("owner-request-accepted");
        }

        private void RejectObserverRequest(string itemId, int amount, int targetSlot)
        {
            if (!_acceptedTransferComplete)
            {
                Session.Fail("ObserverClient attempted the protected request before the accepted transfer completed.");
                return;
            }

            if (!HasExpectedRequest(itemId, amount, targetSlot))
            {
                Session.Fail(
                    $"ObserverClient submitted unexpected transfer data: item='{itemId}', amount={amount}, slot={targetSlot}.");
                return;
            }

            InventoryTransferTestState unchanged = _state.value;
            if (!IsTransferredState(unchanged))
            {
                Session.Fail(
                    $"Authoritative state changed before the rejected request was checked. " +
                    $"Player={unchanged.PlayerWoodAmount}, container={unchanged.ContainerWoodAmount}, revision={unchanged.Revision}.");
                return;
            }

            Session.AddMilestone("observer-request-rejected");
            Session.RecordAssertion("server-accepted-owner-transfer-once");
            Session.RecordAssertion("server-rejected-observer-transfer-without-mutation");
            Session.SetEvidence("acceptedMutationCount", 1);
            Session.SetEvidence("rejectedMutationCount", 1);
            ReportUnauthorizedRequestRejected(unchanged);
            RecordFinalFacts(unchanged);
            Session.Pass(unchanged.Revision);
        }

        private void HandleStateChanged(InventoryTransferTestState state)
        {
            if (_acceptedStateReportedByClient)
                return;

            if (state.Revision < 1)
                return;

            if (!IsTransferredState(state))
            {
                Session.Fail(
                    $"Role '{Session.Role}' observed invalid replicated state. " +
                    $"Player={state.PlayerWoodAmount}, container={state.ContainerWoodAmount}, revision={state.Revision}.");
                return;
            }

            _acceptedStateReportedByClient = true;
            Session.AddMilestone("replication-observed");
            RequestAcceptedStateObserved(state.Revision);
        }

        [ServerRpc(requireOwnership: false)]
        private void RequestAcceptedStateObserved(int revision, RPCInfo info = default)
        {
            if (!isServer)
                return;

            if (!_acceptedTransferComplete || revision != _state.value.Revision)
            {
                Session.Fail(
                    $"Player '{info.sender}' acknowledged unexpected revision {revision}; authoritative revision is {_state.value.Revision}.");
                return;
            }

            if (_ownerPlayer.HasValue && info.sender == _ownerPlayer.Value)
                _ownerObservedAcceptedState = true;
            else if (_observerPlayer.HasValue && info.sender == _observerPlayer.Value)
                _observerObservedAcceptedState = true;
            else
            {
                Session.Fail($"Replication acknowledgement arrived from unregistered player '{info.sender}'.");
                return;
            }

            if (!_ownerObservedAcceptedState || !_observerObservedAcceptedState)
                return;

            Session.AddMilestone("all-clients-observed-replication");
            BeginUnauthorizedTransfer(_observerPlayer.Value);
        }

        [TargetRpc]
        private void BeginUnauthorizedTransfer(PlayerID target)
        {
            if (isServer)
                return;

            if (Session.Role != NetworkTestRole.ObserverClient)
            {
                Session.Fail($"Unauthorized transfer was assigned to unexpected role '{Session.Role}'.");
                return;
            }

            Session.AddMilestone("observer-request-sent");
            RequestGiveItem(WoodItemId, TransferAmount, TargetSlot);
        }

        [ObserversRpc]
        private void ReportUnauthorizedRequestRejected(InventoryTransferTestState authoritativeState)
        {
            if (isServer)
                return;

            InventoryTransferTestState observed = _state.value;
            if (!StatesMatch(observed, authoritativeState) || !IsTransferredState(observed))
            {
                Session.Fail(
                    $"Role '{Session.Role}' did not retain the replicated state after rejection. " +
                    $"Player={observed.PlayerWoodAmount}, container={observed.ContainerWoodAmount}, revision={observed.Revision}.");
                return;
            }

            Session.AddMilestone("rejection-observed");
            Session.RecordAssertion("client-observed-authoritative-transfer");
            Session.RecordAssertion("client-observed-rejection-state");
            Session.SetEvidence("acceptedStateObservationCount", 1);
            RecordFinalFacts(observed);
            Session.Pass(observed.Revision);
        }

        private void RecordFinalFacts(InventoryTransferTestState state)
        {
            Session.SetFact("containerSlot0.itemId", WoodItemId);
            Session.SetFact("containerSlot0.amount", state.ContainerWoodAmount);
            Session.SetFact("playerSlot0.itemId", string.Empty);
            Session.SetFact("playerSlot0.amount", state.PlayerWoodAmount);
            Session.SetFact("rejectedRequestDidNotChangeRevision", state.Revision == 1);
        }

        private static bool HasExpectedRequest(string itemId, int amount, int targetSlot)
        {
            return string.Equals(itemId, WoodItemId, StringComparison.Ordinal) &&
                   amount == TransferAmount &&
                   targetSlot == TargetSlot;
        }

        private static bool IsInitialState(InventoryTransferTestState state)
        {
            return state.PlayerWoodAmount == TransferAmount &&
                   state.ContainerWoodAmount == 0 &&
                   state.Revision == 0;
        }

        private static bool IsTransferredState(InventoryTransferTestState state)
        {
            return state.PlayerWoodAmount == 0 &&
                   state.ContainerWoodAmount == TransferAmount &&
                   state.Revision == 1;
        }

        private static bool StatesMatch(InventoryTransferTestState left, InventoryTransferTestState right)
        {
            return left.PlayerWoodAmount == right.PlayerWoodAmount &&
                   left.ContainerWoodAmount == right.ContainerWoodAmount &&
                   left.Revision == right.Revision;
        }
    }
}
