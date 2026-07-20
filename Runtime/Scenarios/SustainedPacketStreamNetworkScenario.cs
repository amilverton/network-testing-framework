using System;
using System.Collections;
using PurrNet;
using PurrNet.Packing;
using UnityEngine;

namespace Caffeinated.NetworkTesting
{
    [Serializable]
    public struct SustainedPacketStreamState : IPackedAuto
    {
        public int Sequence;
        public int Value;
        public int Checksum;
    }

    /// <summary>
    /// Proves sustained, ordered request and broadcast traffic plus final SyncVar convergence.
    /// </summary>
    [NetworkTestScenario("Harness.SustainedPacketStream")]
    public sealed class SustainedPacketStreamNetworkScenario : NetworkTestScenario
    {
        private const int PacketCount = 20;
        private const int IntervalMilliseconds = 250;
        private const int NominalDurationMilliseconds = PacketCount * IntervalMilliseconds;
        private const int ExpectedFinalValue = 63109;
        private const int ExpectedChecksum = 957570;
        private const float MinimumObservedDurationSeconds = 4.25f;
        private const float MaximumObservedDurationSeconds = 8f;

        private readonly SyncVar<SustainedPacketStreamState> _state =
            new SyncVar<SustainedPacketStreamState>();
        private readonly TwoClientRoleRegistry _roles = new TwoClientRoleRegistry();

        private bool _ownerReady;
        private bool _observerReady;
        private bool _streamStarted;
        private bool _ownerConfirmed;
        private bool _observerConfirmed;
        private bool _clientAcknowledged;
        private bool _sawFinalReplicatedState;
        private int _sentPacketCount;
        private int _acceptedPacketCount;
        private int _receivedPacketCount;
        private int _receivedChecksum;
        private int _lastReplicatedSequence;
        private float _firstPacketReceivedAt;
        private float _observedStreamDurationSeconds;

        protected override void OnScenarioSpawned(bool asServer)
        {
            if (asServer)
            {
                _state.value = new SustainedPacketStreamState
                {
                    Sequence = 0,
                    Value = 0,
                    Checksum = 0
                };
                Session.AddMilestone("empty-packet-stream-created");
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
        }

        [TargetRpc]
        private void ConfirmRole(PlayerID target, NetworkTestRole confirmedRole)
        {
            if (isServer)
                return;

            if (Session.Role != confirmedRole || !IsInitialState(_state.value))
            {
                Session.Fail(
                    $"Role '{Session.Role}' did not confirm from the empty packet-stream state.");
                return;
            }

            Session.AddMilestone("initial-packet-state-observed");
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
                Session.Fail("Packet-stream readiness arrived from a mismatched client role.");
                return;
            }

            if (role == NetworkTestRole.OwnerClient)
            {
                if (_ownerReady)
                {
                    Session.Fail("OwnerClient published packet-stream readiness more than once.");
                    return;
                }

                _ownerReady = true;
            }
            else
            {
                if (_observerReady)
                {
                    Session.Fail("ObserverClient published packet-stream readiness more than once.");
                    return;
                }

                _observerReady = true;
            }

            if (!_ownerReady || !_observerReady || _streamStarted)
                return;

            _streamStarted = true;
            Session.AddMilestone("packet-stream-participants-ready");
            BeginPacketStream(_roles.OwnerPlayer.Value);
        }

        [TargetRpc]
        private void BeginPacketStream(PlayerID target)
        {
            if (isServer)
                return;

            if (Session.Role != NetworkTestRole.OwnerClient || !IsInitialState(_state.value) || _streamStarted)
            {
                Session.Fail($"Packet stream began in an invalid state for role '{Session.Role}'.");
                return;
            }

            _streamStarted = true;
            Session.AddMilestone("packet-stream-sending-started");
            StartCoroutine(SendPacketStream());
        }

        private IEnumerator SendPacketStream()
        {
            WaitForSecondsRealtime interval = new WaitForSecondsRealtime(IntervalMilliseconds / 1000f);
            for (int sequence = 1; sequence <= PacketCount; sequence++)
            {
                yield return interval;

                int payload = GeneratePayload(sequence);
                _sentPacketCount++;
                Debug.Log(
                    $"[PacketStream:{Session.Role}] Sending sequence={sequence}, value={payload}.");
                RequestStreamPacket(sequence, payload);
            }
        }

        [ServerRpc(requireOwnership: false)]
        private void RequestStreamPacket(int sequence, int payload, RPCInfo info = default)
        {
            if (!isServer)
                return;

            int expectedSequence = _acceptedPacketCount + 1;
            if (!_roles.OwnerPlayer.HasValue || info.sender != _roles.OwnerPlayer.Value ||
                sequence != expectedSequence || payload != GeneratePayload(sequence) ||
                _state.value.Sequence != sequence - 1)
            {
                Session.Fail(
                    $"Invalid packet-stream request from '{info.sender}': sequence={sequence}, " +
                    $"expected={expectedSequence}, value={payload}.");
                return;
            }

            if (sequence == 1)
            {
                _firstPacketReceivedAt = Time.realtimeSinceStartup;
                Session.AddMilestone("packet-stream-first-packet-accepted");
            }

            _acceptedPacketCount++;
            int checksum = _state.value.Checksum + payload;
            _state.value = new SustainedPacketStreamState
            {
                Sequence = sequence,
                Value = payload,
                Checksum = checksum
            };

            Debug.Log(
                $"[PacketStream:{Session.Role}] Accepted sequence={sequence}, value={payload}, checksum={checksum}.");
            ReceiveStreamPacket(sequence, payload);

            if (sequence != PacketCount)
                return;

            _observedStreamDurationSeconds = Time.realtimeSinceStartup - _firstPacketReceivedAt;
            if (_observedStreamDurationSeconds < MinimumObservedDurationSeconds ||
                _observedStreamDurationSeconds > MaximumObservedDurationSeconds ||
                !IsFinalState(_state.value))
            {
                Session.Fail(
                    $"Packet stream duration/state was invalid: duration={_observedStreamDurationSeconds:F3}s, " +
                    $"sequence={_state.value.Sequence}, value={_state.value.Value}, checksum={_state.value.Checksum}.");
                return;
            }

            Debug.Log(
                $"[PacketStream:{Session.Role}] Completed {PacketCount} packets over " +
                $"{_observedStreamDurationSeconds:F3}s.");
            Session.AddMilestone("packet-stream-last-packet-accepted");
        }

        [ObserversRpc(runLocally: false)]
        private void ReceiveStreamPacket(int sequence, int payload)
        {
            if (isServer)
                return;

            int expectedSequence = _receivedPacketCount + 1;
            if (sequence != expectedSequence || payload != GeneratePayload(sequence))
            {
                Session.Fail(
                    $"Role '{Session.Role}' received an invalid packet: sequence={sequence}, " +
                    $"expected={expectedSequence}, value={payload}.");
                return;
            }

            _receivedPacketCount++;
            _receivedChecksum += payload;
            Debug.Log(
                $"[PacketStream:{Session.Role}] Received sequence={sequence}, value={payload}, " +
                $"checksum={_receivedChecksum}.");

            if (sequence == 1)
                Session.AddMilestone("packet-stream-first-packet-received");

            if (sequence == PacketCount)
                Session.AddMilestone("packet-stream-last-packet-received");

            TryAcknowledgeClientStream();
        }

        private void HandleStateChanged(SustainedPacketStreamState state)
        {
            if (state.Sequence == 0)
                return;

            if (state.Sequence <= _lastReplicatedSequence || state.Sequence > PacketCount ||
                state.Value != GeneratePayload(state.Sequence) ||
                state.Checksum != CalculateChecksumThrough(state.Sequence))
            {
                Session.Fail(
                    $"Role '{Session.Role}' observed invalid replicated packet state: " +
                    $"sequence={state.Sequence}, value={state.Value}, checksum={state.Checksum}.");
                return;
            }

            _lastReplicatedSequence = state.Sequence;
            if (state.Sequence == PacketCount)
                _sawFinalReplicatedState = true;

            TryAcknowledgeClientStream();
        }

        private void TryAcknowledgeClientStream()
        {
            if (_clientAcknowledged || _receivedPacketCount != PacketCount ||
                _receivedChecksum != ExpectedChecksum || !_sawFinalReplicatedState ||
                !IsFinalState(_state.value))
            {
                return;
            }

            _clientAcknowledged = true;
            RequestStreamObserved(_receivedPacketCount, _receivedChecksum, _state.value.Value);
        }

        [ServerRpc(requireOwnership: false)]
        private void RequestStreamObserved(
            int receivedPacketCount,
            int checksum,
            int finalValue,
            RPCInfo info = default)
        {
            if (!isServer)
                return;

            if (!_roles.TryGetRole(info.sender, out NetworkTestRole role) ||
                receivedPacketCount != PacketCount || checksum != ExpectedChecksum ||
                finalValue != ExpectedFinalValue || !IsFinalState(_state.value))
            {
                Session.Fail($"Packet-stream acknowledgement from '{info.sender}' was invalid.");
                return;
            }

            if (role == NetworkTestRole.OwnerClient)
            {
                if (_ownerConfirmed)
                {
                    Session.Fail("OwnerClient confirmed the packet stream more than once.");
                    return;
                }

                _ownerConfirmed = true;
            }
            else
            {
                if (_observerConfirmed)
                {
                    Session.Fail("ObserverClient confirmed the packet stream more than once.");
                    return;
                }

                _observerConfirmed = true;
            }

            if (!_ownerConfirmed || !_observerConfirmed)
                return;

            Session.AddMilestone("all-clients-confirmed-packet-stream");
            Session.RecordAssertion("server-accepted-exact-ordered-packet-stream");
            Session.RecordAssertion("server-observed-sustained-packet-cadence");
            Session.RecordAssertion("server-confirmed-both-clients-received-every-packet");
            Session.SetEvidence("acceptedPacketCount", _acceptedPacketCount);
            Session.SetEvidence("payloadChecksum", _state.value.Checksum);
            Session.SetEvidence("finalValue", _state.value.Value);
            Session.SetEvidence("cadenceWindowValid", true);
            RecordSharedFacts();
            FinishPacketStream(_state.value);
            Session.Pass(PacketCount);
        }

        [ObserversRpc(runLocally: false)]
        private void FinishPacketStream(SustainedPacketStreamState authoritativeState)
        {
            if (isServer)
                return;

            if (!StatesMatch(_state.value, authoritativeState) || !IsFinalState(_state.value) ||
                _receivedPacketCount != PacketCount || _receivedChecksum != ExpectedChecksum ||
                !_sawFinalReplicatedState)
            {
                Session.Fail($"Role '{Session.Role}' did not retain the complete packet stream and final state.");
                return;
            }

            Session.AddMilestone("packet-stream-completion-observed");
            Session.RecordAssertion("client-received-exact-ordered-packet-stream");
            Session.RecordAssertion("client-converged-on-final-authoritative-value");
            if (Session.Role == NetworkTestRole.OwnerClient)
            {
                if (_sentPacketCount != PacketCount)
                {
                    Session.Fail($"OwnerClient sent {_sentPacketCount} packets; expected {PacketCount}.");
                    return;
                }

                Session.RecordAssertion("owner-sent-exact-configured-packet-count");
                Session.SetEvidence("sentPacketCount", _sentPacketCount);
            }

            Session.SetEvidence("receivedPacketCount", _receivedPacketCount);
            Session.SetEvidence("payloadChecksum", _receivedChecksum);
            Session.SetEvidence("finalValue", _state.value.Value);
            RecordSharedFacts();
            Session.Pass(PacketCount);
        }

        private void RecordSharedFacts()
        {
            Session.SetFact("packetStream.packetCount", PacketCount);
            Session.SetFact("packetStream.intervalMilliseconds", IntervalMilliseconds);
            Session.SetFact("packetStream.nominalDurationMilliseconds", NominalDurationMilliseconds);
            Session.SetFact("packetStream.finalSequence", PacketCount);
            Session.SetFact("packetStream.finalValue", ExpectedFinalValue);
            Session.SetFact("packetStream.payloadChecksum", ExpectedChecksum);
            Session.SetFact("packetStream.sourceRole", NetworkTestRole.OwnerClient.ToString());
            Session.SetFact("packetStream.payloadPattern", "deterministic-pseudorandom");
        }

        private static int GeneratePayload(int sequence)
        {
            return ((sequence * 7919) + 104729) % 100000;
        }

        private static int CalculateChecksumThrough(int sequence)
        {
            int checksum = 0;
            for (int packetSequence = 1; packetSequence <= sequence; packetSequence++)
                checksum += GeneratePayload(packetSequence);

            return checksum;
        }

        private static bool IsInitialState(SustainedPacketStreamState state)
        {
            return state.Sequence == 0 && state.Value == 0 && state.Checksum == 0;
        }

        private static bool IsFinalState(SustainedPacketStreamState state)
        {
            return state.Sequence == PacketCount &&
                   state.Value == ExpectedFinalValue &&
                   state.Checksum == ExpectedChecksum;
        }

        private static bool StatesMatch(
            SustainedPacketStreamState left,
            SustainedPacketStreamState right)
        {
            return left.Sequence == right.Sequence &&
                   left.Value == right.Value &&
                   left.Checksum == right.Checksum;
        }
    }
}
