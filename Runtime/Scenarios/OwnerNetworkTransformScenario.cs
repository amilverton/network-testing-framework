using System.Collections;
using PurrNet;
using UnityEngine;

namespace Amilverton.PurrNetTesting
{
    /// <summary>
    /// Proves owner-authoritative NetworkTransform convergence and non-owner spoof isolation.
    /// </summary>
    [NetworkTestScenario(
        "Harness.OwnerNetworkTransform",
        NetworkTestPrefabFeatures.NetworkTransform)]
    public sealed class OwnerNetworkTransformScenario : NetworkTestScenario
    {
        private const float PositionTolerance = 0.05f;
        private const float LocalWaitSeconds = 8f;

        private static readonly Vector3 InitialPosition = Vector3.zero;
        private static readonly Vector3 TargetPosition = new Vector3(8f, 0f, 3f);
        private static readonly Vector3 SpoofPosition = new Vector3(50f, 0f, -50f);

        private readonly TwoClientRoleRegistry _roles = new TwoClientRoleRegistry();

        private NetworkTransform _networkTransform;
        private bool _ownerReady;
        private bool _observerReady;
        private bool _moveStarted;
        private bool _serverConvergenceStarted;
        private bool _ownerConverged;
        private bool _observerConverged;
        private bool _spoofStarted;

        protected override void OnScenarioSpawned(bool asServer)
        {
            _networkTransform = GetComponentInChildren<NetworkTransform>();
            if (_networkTransform == null)
            {
                Session.Fail("Generated NetworkTransform component is missing from the scenario prefab.");
                return;
            }

            if (asServer)
            {
                _networkTransform.transform.position = InitialPosition;
                Session.AddMilestone("network-transform-fixture-created");
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

            if (!newlyRegistered)
                return;

            if (requestedRole == NetworkTestRole.OwnerClient)
            {
                _networkTransform.GiveOwnership(info.sender);
                Session.AddMilestone("network-transform-ownership-granted");
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

            StartCoroutine(WaitForRoleReady(confirmedRole));
        }

        private IEnumerator WaitForRoleReady(NetworkTestRole role)
        {
            float deadline = Time.realtimeSinceStartup + LocalWaitSeconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                bool ownershipReady = role != NetworkTestRole.OwnerClient || _networkTransform.isOwner;
                if (ownershipReady && IsNear(_networkTransform.transform.position, InitialPosition))
                {
                    Session.AddMilestone(role == NetworkTestRole.OwnerClient
                        ? "owner-transform-authority-observed"
                        : "observer-initial-transform-observed");
                    Session.PublishReady();
                    RequestClientReady(role);
                    yield break;
                }

                yield return null;
            }

            Session.Fail(
                $"Role '{role}' did not observe its initial NetworkTransform state/authority within {LocalWaitSeconds} seconds.");
        }

        [ServerRpc(requireOwnership: false)]
        private void RequestClientReady(NetworkTestRole role, RPCInfo info = default)
        {
            if (!isServer)
                return;

            if (!_roles.TryGetRole(info.sender, out NetworkTestRole registeredRole) || registeredRole != role)
            {
                Session.Fail($"Transform readiness came from mismatched role '{role}' and player '{info.sender}'.");
                return;
            }

            if (role == NetworkTestRole.OwnerClient)
                _ownerReady = true;
            else
                _observerReady = true;

            if (!_ownerReady || !_observerReady || _moveStarted)
                return;

            _moveStarted = true;
            Session.AddMilestone("transform-clients-ready");
            BeginOwnerMove(_roles.OwnerPlayer.Value);
        }

        [TargetRpc]
        private void BeginOwnerMove(PlayerID target)
        {
            if (isServer)
                return;

            if (Session.Role != NetworkTestRole.OwnerClient || !_networkTransform.isOwner)
            {
                Session.Fail("NetworkTransform movement command reached a client without owner authority.");
                return;
            }

            _networkTransform.transform.position = TargetPosition;
            _networkTransform.ForceSync();
            Session.AddMilestone("owner-moved-network-transform");
            RequestOwnerMoveIssued(ToMilliUnits(_networkTransform.transform.position));
        }

        [ServerRpc(requireOwnership: false)]
        private void RequestOwnerMoveIssued(string ownerPosition, RPCInfo info = default)
        {
            if (!isServer)
                return;

            if (!_roles.OwnerPlayer.HasValue || info.sender != _roles.OwnerPlayer.Value ||
                ownerPosition != ToMilliUnits(TargetPosition) || _serverConvergenceStarted)
            {
                Session.Fail($"Owner movement issue acknowledgement was invalid: '{ownerPosition}'.");
                return;
            }

            _serverConvergenceStarted = true;
            StartCoroutine(WaitForServerConvergence());
        }

        private IEnumerator WaitForServerConvergence()
        {
            float deadline = Time.realtimeSinceStartup + LocalWaitSeconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (IsNear(_networkTransform.transform.position, TargetPosition))
                {
                    Session.AddMilestone("server-observed-owner-transform");
                    RequestClientConvergenceCheck(ToMilliUnits(TargetPosition));
                    yield break;
                }

                yield return null;
            }

            Session.Fail(
                $"Dedicated server did not converge to owner position {TargetPosition} within {LocalWaitSeconds} seconds. " +
                $"Observed {_networkTransform.transform.position}.");
        }

        [ObserversRpc(runLocally: false)]
        private void RequestClientConvergenceCheck(string expectedPosition)
        {
            if (isServer)
                return;

            if (expectedPosition != ToMilliUnits(TargetPosition))
            {
                Session.Fail("Transform convergence check contained an unexpected target position.");
                return;
            }

            StartCoroutine(WaitForClientConvergence());
        }

        private IEnumerator WaitForClientConvergence()
        {
            float deadline = Time.realtimeSinceStartup + LocalWaitSeconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (IsNear(_networkTransform.transform.position, TargetPosition))
                {
                    Session.AddMilestone("client-transform-converged");
                    RequestClientConverged(ToMilliUnits(_networkTransform.transform.position));
                    yield break;
                }

                yield return null;
            }

            Session.Fail(
                $"Role '{Session.Role}' did not converge to NetworkTransform target {TargetPosition}; " +
                $"observed {_networkTransform.transform.position}.");
        }

        [ServerRpc(requireOwnership: false)]
        private void RequestClientConverged(string observedPosition, RPCInfo info = default)
        {
            if (!isServer)
                return;

            if (observedPosition != ToMilliUnits(TargetPosition) ||
                !_roles.TryGetRole(info.sender, out NetworkTestRole role))
            {
                Session.Fail($"Transform convergence acknowledgement from '{info.sender}' was invalid.");
                return;
            }

            if (role == NetworkTestRole.OwnerClient)
                _ownerConverged = true;
            else
                _observerConverged = true;

            if (!_ownerConverged || !_observerConverged || _spoofStarted)
                return;

            _spoofStarted = true;
            Session.AddMilestone("all-roles-converged-before-spoof");
            BeginObserverSpoof(_roles.ObserverPlayer.Value);
        }

        [TargetRpc]
        private void BeginObserverSpoof(PlayerID target)
        {
            if (isServer)
                return;

            if (Session.Role != NetworkTestRole.ObserverClient || _networkTransform.isOwner)
            {
                Session.Fail("Transform spoof command reached a client with unexpected authority.");
                return;
            }

            _networkTransform.transform.position = SpoofPosition;
            _networkTransform.ForceSync();
            if (!IsNear(_networkTransform.transform.position, SpoofPosition))
            {
                Session.Fail("Observer could not apply the local transform spoof used by the test.");
                return;
            }

            Session.AddMilestone("observer-local-spoof-applied");
            RequestObserverSpoofBarrier(ToMilliUnits(SpoofPosition));
        }

        [ServerRpc(requireOwnership: false)]
        private void RequestObserverSpoofBarrier(string spoofPosition, RPCInfo info = default)
        {
            if (!isServer)
                return;

            if (!_roles.ObserverPlayer.HasValue || info.sender != _roles.ObserverPlayer.Value ||
                spoofPosition != ToMilliUnits(SpoofPosition))
            {
                Session.Fail("Observer spoof barrier arrived from an unexpected sender or position.");
                return;
            }

            if (!IsNear(_networkTransform.transform.position, TargetPosition))
            {
                Session.Fail(
                    $"Observer-local spoof became authoritative. Server observed {_networkTransform.transform.position}, " +
                    $"expected {TargetPosition}.");
                return;
            }

            Session.AddMilestone("observer-spoof-isolated-from-server");
            _networkTransform.ForceSync(_roles.ObserverPlayer.Value);
            VerifyObserverCorrection(_roles.ObserverPlayer.Value);
        }

        [TargetRpc]
        private void VerifyObserverCorrection(PlayerID target)
        {
            if (isServer)
                return;

            StartCoroutine(WaitForObserverCorrection());
        }

        private IEnumerator WaitForObserverCorrection()
        {
            float deadline = Time.realtimeSinceStartup + LocalWaitSeconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (IsNear(_networkTransform.transform.position, TargetPosition))
                {
                    Session.AddMilestone("observer-resynchronized-after-local-spoof");
                    RequestObserverCorrected(ToMilliUnits(_networkTransform.transform.position));
                    yield break;
                }

                yield return null;
            }

            Session.Fail(
                $"Observer did not resynchronize after local spoof; observed {_networkTransform.transform.position}.");
        }

        [ServerRpc(requireOwnership: false)]
        private void RequestObserverCorrected(string observedPosition, RPCInfo info = default)
        {
            if (!isServer)
                return;

            if (!_roles.ObserverPlayer.HasValue || info.sender != _roles.ObserverPlayer.Value ||
                observedPosition != ToMilliUnits(TargetPosition) || !IsNear(_networkTransform.transform.position, TargetPosition))
            {
                Session.Fail("Observer correction acknowledgement did not match authoritative transform state.");
                return;
            }

            Session.AddMilestone("transform-spoof-test-complete");
            Session.RecordAssertion("server-converged-to-owner-network-transform");
            Session.RecordAssertion("observer-local-spoof-never-became-authoritative");
            Session.SetEvidence("authoritativePositionMilli", ToMilliUnits(_networkTransform.transform.position));
            RecordSharedFacts();
            FinishTransformTest(ToMilliUnits(TargetPosition));
            Session.Pass(1);
        }

        [ObserversRpc(runLocally: false)]
        private void FinishTransformTest(string authoritativePosition)
        {
            if (isServer)
                return;

            if (authoritativePosition != ToMilliUnits(TargetPosition) || !IsNear(_networkTransform.transform.position, TargetPosition))
            {
                Session.Fail(
                    $"Role '{Session.Role}' did not finish at authoritative position {TargetPosition}; " +
                    $"observed {_networkTransform.transform.position}.");
                return;
            }

            Session.AddMilestone("transform-completion-observed");
            Session.RecordAssertion("client-converged-to-owner-authored-position");
            if (Session.Role == NetworkTestRole.ObserverClient)
                Session.RecordAssertion("observer-resynchronized-after-local-spoof");
            Session.SetEvidence("locallyObservedPositionMilli", ToMilliUnits(_networkTransform.transform.position));
            Session.SetEvidence("isTransformOwner", _networkTransform.isOwner);
            RecordSharedFacts();
            Session.Pass(1);
        }

        private void RecordSharedFacts()
        {
            Session.SetFact("transform.positionMilli", ToMilliUnits(TargetPosition));
            Session.SetFact("transform.ownerRole", NetworkTestRole.OwnerClient.ToString());
            Session.SetFact("transform.observerSpoofAuthoritative", false);
            Session.SetFact("transform.toleranceMilli", Mathf.RoundToInt(PositionTolerance * 1000f));
        }

        private static bool IsNear(Vector3 value, Vector3 expected)
        {
            return Vector3.Distance(value, expected) <= PositionTolerance;
        }

        private static string ToMilliUnits(Vector3 position)
        {
            return $"{Mathf.RoundToInt(position.x * 1000f)}," +
                   $"{Mathf.RoundToInt(position.y * 1000f)}," +
                   $"{Mathf.RoundToInt(position.z * 1000f)}";
        }
    }
}
