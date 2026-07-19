using System.Collections.Generic;
using PurrNet;

namespace Amilverton.PurrNetTesting
{
    /// <summary>
    /// Proves ordered SyncList delta callbacks and identical final collection state.
    /// </summary>
    [NetworkTestScenario("Harness.SyncListOrder")]
    public sealed class SyncListOrderNetworkScenario : NetworkTestScenario
    {
        private const string ExpectedTrace = "add:A:0>add:B:1>set:A:C:0>remove:B:1";

        private readonly SyncList<string> _items = new SyncList<string>();
        private readonly TwoClientRoleRegistry _roles = new TwoClientRoleRegistry();
        private readonly List<string> _callbackTrace = new List<string>(4);

        private bool _ownerReady;
        private bool _observerReady;
        private bool _mutationsStarted;
        private bool _serverTraceComplete;
        private bool _ownerTraceComplete;
        private bool _observerTraceComplete;

        protected override void OnScenarioSpawned(bool asServer)
        {
            _items.onChanged += HandleListChanged;

            if (asServer)
            {
                if (_items.Count != 0)
                {
                    Session.Fail("Server SyncList was not empty at fixture creation.");
                    return;
                }

                Session.AddMilestone("empty-synclist-created");
                Session.PublishReady();
                return;
            }

            RequestRegisterRole(Session.Role);
        }

        protected override void OnScenarioDespawned(bool asServer)
        {
            _items.onChanged -= HandleListChanged;
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
        }

        [TargetRpc]
        private void ConfirmRole(PlayerID target, NetworkTestRole confirmedRole)
        {
            if (isServer)
                return;

            if (Session.Role != confirmedRole || _items.Count != 0)
            {
                Session.Fail(
                    $"Role '{Session.Role}' did not confirm with an empty initial SyncList.");
                return;
            }

            Session.AddMilestone("empty-synclist-observed");
            Session.PublishReady();
            RequestClientReady(confirmedRole);
        }

        [ServerRpc(requireOwnership: false)]
        private void RequestClientReady(NetworkTestRole role, RPCInfo info = default)
        {
            if (!isServer)
                return;

            if (!_roles.TryGetRole(info.sender, out NetworkTestRole registeredRole) || registeredRole != role)
            {
                Session.Fail("SyncList readiness arrived from a mismatched client role.");
                return;
            }

            if (role == NetworkTestRole.OwnerClient)
                _ownerReady = true;
            else
                _observerReady = true;

            if (!_ownerReady || !_observerReady || _mutationsStarted)
                return;

            _mutationsStarted = true;
            Session.AddMilestone("synclist-subscribers-ready");
            _items.Add("A");
            _items.Add("B");
            _items[0] = "C";
            _items.RemoveAt(1);
        }

        private void HandleListChanged(SyncListChange<string> change)
        {
            if (_callbackTrace.Count >= 4)
            {
                Session.Fail($"Role '{Session.Role}' observed more than four SyncList callbacks.");
                return;
            }

            _callbackTrace.Add(FormatChange(change));
            if (_callbackTrace.Count < 4)
                return;

            string trace = string.Join(">", _callbackTrace);
            if (trace != ExpectedTrace || _items.Count != 1 || _items[0] != "C")
            {
                Session.Fail(
                    $"Role '{Session.Role}' observed invalid SyncList trace '{trace}' or final list state.");
                return;
            }

            Session.AddMilestone("exact-synclist-trace-observed");
            if (isServer)
            {
                _serverTraceComplete = true;
                TryFinish();
                return;
            }

            RequestTraceObserved(trace, _callbackTrace.Count);
        }

        [ServerRpc(requireOwnership: false)]
        private void RequestTraceObserved(string trace, int callbackCount, RPCInfo info = default)
        {
            if (!isServer)
                return;

            if (trace != ExpectedTrace || callbackCount != 4 ||
                !_roles.TryGetRole(info.sender, out NetworkTestRole role))
            {
                Session.Fail($"SyncList trace acknowledgement from '{info.sender}' was invalid: '{trace}'.");
                return;
            }

            if (role == NetworkTestRole.OwnerClient)
                _ownerTraceComplete = true;
            else
                _observerTraceComplete = true;

            TryFinish();
        }

        private void TryFinish()
        {
            if (!_serverTraceComplete || !_ownerTraceComplete || !_observerTraceComplete)
                return;

            Session.AddMilestone("all-roles-confirmed-synclist-order");
            Session.RecordAssertion("server-observed-exact-four-callback-trace");
            Session.RecordAssertion("server-confirmed-both-client-callback-traces");
            Session.SetEvidence("callbackTrace", ExpectedTrace);
            Session.SetEvidence("callbackCount", 4);
            RecordSharedFacts();
            FinishSyncList();
            Session.Pass(4);
        }

        [ObserversRpc(runLocally: false)]
        private void FinishSyncList()
        {
            if (isServer)
                return;

            string trace = string.Join(">", _callbackTrace);
            if (trace != ExpectedTrace || _items.Count != 1 || _items[0] != "C")
            {
                Session.Fail($"Role '{Session.Role}' did not retain the final SyncList state and trace.");
                return;
            }

            Session.AddMilestone("synclist-completion-observed");
            Session.RecordAssertion("client-observed-exact-four-callback-trace");
            Session.RecordAssertion("client-retained-final-synclist-value");
            Session.SetEvidence("callbackTrace", trace);
            Session.SetEvidence("callbackCount", _callbackTrace.Count);
            RecordSharedFacts();
            Session.Pass(4);
        }

        private void RecordSharedFacts()
        {
            Session.SetFact("syncList.callbackCount", 4);
            Session.SetFact("syncList.callbackTrace", ExpectedTrace);
            Session.SetFact("syncList.finalCount", 1);
            Session.SetFact("syncList.finalValue0", "C");
        }

        private static string FormatChange(SyncListChange<string> change)
        {
            switch (change.operation)
            {
                case SyncListOperation.Added:
                    return $"add:{change.value}:{change.index}";
                case SyncListOperation.Set:
                    return $"set:{change.oldValue}:{change.value}:{change.index}";
                case SyncListOperation.Removed:
                    return $"remove:{change.oldValue}:{change.index}";
                default:
                    return $"unexpected:{change.operation}:{change.index}";
            }
        }
    }
}
