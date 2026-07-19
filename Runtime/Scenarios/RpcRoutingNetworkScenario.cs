using PurrNet;
using UnityEngine;

namespace Amilverton.PurrNetTesting
{
    /// <summary>
    /// Proves exact TargetRpc and ObserversRpc audience inclusion and exclusion.
    /// </summary>
    [NetworkTestScenario("Harness.RpcRouting")]
    public sealed class RpcRoutingNetworkScenario : NetworkTestScenario
    {
        private readonly TwoClientRoleRegistry _roles = new TwoClientRoleRegistry();

        private bool _routingDispatched;
        private bool _ownerAcknowledged;
        private bool _observerAcknowledged;
        private int _targetReceipts;
        private int _observerReceipts;
        private string _targetToken;
        private string _observerToken;

        protected override void OnScenarioSpawned(bool asServer)
        {
            if (asServer)
            {
                if (Session.Role != NetworkTestRole.Server)
                {
                    Session.Fail($"Server fixture spawned in unexpected role '{Session.Role}'.");
                    return;
                }

                _targetToken = Session.RunId + ":target";
                _observerToken = Session.RunId + ":observers";
                Session.AddMilestone("routing-fixture-created");
                Session.PublishReady();
                return;
            }

            RequestRegisterRole(Session.Role);
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

            if (!_roles.AllRegistered || _routingDispatched)
                return;

            _routingDispatched = true;
            Session.AddMilestone("routing-rpcs-dispatched");
            ReceiveTargetToken(_roles.OwnerPlayer.Value, _targetToken);
            ReceiveObserverToken(_observerToken);
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

            Session.AddMilestone("role-assigned");
            Session.PublishReady();
        }

        [TargetRpc(runLocally: false)]
        private void ReceiveTargetToken(PlayerID target, string token)
        {
            if (isServer)
                return;

            if (token != Session.RunId + ":target")
            {
                Session.Fail("TargetRpc delivered an unexpected run token.");
                return;
            }

            _targetReceipts++;
            Session.AddMilestone("target-rpc-received");
        }

        [ObserversRpc(runLocally: false)]
        private void ReceiveObserverToken(string token)
        {
            if (isServer)
                return;

            if (token != Session.RunId + ":observers")
            {
                Session.Fail("ObserversRpc delivered an unexpected run token.");
                return;
            }

            _observerReceipts++;
            Session.AddMilestone("observers-rpc-received");
            RequestRoutingObserved(_targetReceipts, _observerReceipts, token);
        }

        [ServerRpc(requireOwnership: false)]
        private void RequestRoutingObserved(
            int targetReceipts,
            int observerReceipts,
            string observerToken,
            RPCInfo info = default)
        {
            if (!isServer)
                return;

            if (!_roles.TryGetRole(info.sender, out NetworkTestRole role))
            {
                Session.Fail($"Routing acknowledgement arrived from unregistered player '{info.sender}'.");
                return;
            }

            int expectedTargetReceipts = role == NetworkTestRole.OwnerClient ? 1 : 0;
            if (targetReceipts != expectedTargetReceipts || observerReceipts != 1 || observerToken != _observerToken)
            {
                Session.Fail(
                    $"Role '{role}' reported an invalid routing vector " +
                    $"target={targetReceipts}, observers={observerReceipts}.");
                return;
            }

            if (role == NetworkTestRole.OwnerClient)
            {
                if (_ownerAcknowledged)
                {
                    Session.Fail("OwnerClient acknowledged the routing RPCs more than once.");
                    return;
                }

                _ownerAcknowledged = true;
            }
            else
            {
                if (_observerAcknowledged)
                {
                    Session.Fail("ObserverClient acknowledged the routing RPCs more than once.");
                    return;
                }

                _observerAcknowledged = true;
            }

            if (!_ownerAcknowledged || !_observerAcknowledged)
                return;

            Session.AddMilestone("exact-routing-vectors-confirmed");
            Session.RecordAssertion("server-confirmed-exact-rpc-audiences");
            Session.SetEvidence("serverLocalTargetReceipts", _targetReceipts);
            Session.SetEvidence("serverLocalObserverReceipts", _observerReceipts);
            RecordSharedFacts();
            FinishRouting();
            Session.Pass(1);
        }

        [ObserversRpc(runLocally: false)]
        private void FinishRouting()
        {
            if (isServer)
                return;

            int expectedTargetReceipts = Session.Role == NetworkTestRole.OwnerClient ? 1 : 0;
            if (_targetReceipts != expectedTargetReceipts || _observerReceipts != 1)
            {
                Session.Fail(
                    $"Role '{Session.Role}' finished with routing vector " +
                    $"target={_targetReceipts}, observers={_observerReceipts}.");
                return;
            }

            Session.AddMilestone("routing-completion-observed");
            Session.RecordAssertion("client-confirmed-exact-rpc-audience");
            Session.SetEvidence("targetReceipts", _targetReceipts);
            Session.SetEvidence("observerReceipts", _observerReceipts);
            RecordSharedFacts();
            Session.Pass(1);
        }

        private void RecordSharedFacts()
        {
            Session.SetFact("owner.targetReceipts", 1);
            Session.SetFact("observer.targetReceipts", 0);
            Session.SetFact("owner.observersReceipts", 1);
            Session.SetFact("observer.observersReceipts", 1);
            Session.SetFact("server.localRpcHandlersRan", false);
        }
    }
}
