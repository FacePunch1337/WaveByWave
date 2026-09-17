using System.Collections.Generic;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

namespace WaveByWave.Networking
{
    /// <summary>
    /// Server-authoritative rigidbody that can be pushed by every player. Remote owners
    /// relay their KCC contact as a validated push intent because their presentation-only
    /// player copy has no physics collider on the server.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject), typeof(NetworkTransform), typeof(NetworkRigidbody))]
    [RequireComponent(typeof(Rigidbody), typeof(Collider))]
    public sealed class NetworkPhysicsObject : NetworkBehaviour
    {
        [SerializeField] private Rigidbody body;
        [SerializeField] private Collider interactionCollider;
        [Tooltip("Optional child containing only the rendered mesh. It receives a small client-side visual lead while pushing.")]
        [SerializeField] private Transform visualRoot;
        [Tooltip("Client-only solid proxy placed on the visual root. The authoritative collider remains server-only.")]
        [SerializeField] private Collider predictionCollider;

        [Header("Server physics")]
        [SerializeField, Min(0.1f)] private float mass = 5f;
        [SerializeField, Min(0f)] private float linearDamping = 0.35f;
        [SerializeField, Min(0f)] private float angularDamping = 0.8f;
        [SerializeField, Min(0.1f)] private float maximumLinearSpeed = 7f;
        [SerializeField, Min(0.1f)] private float maximumAngularSpeed = 8f;

        [Header("Remote player pushing")]
        [SerializeField, Min(0.1f)] private float maximumPushDistance = 3f;
        [SerializeField, Min(0.1f)] private float maximumReportedPushSpeed = 7f;
        [SerializeField, Min(0f)] private float pushForce = 80f;
        [SerializeField, Min(0.02f)] private float pushRequestInterval = 0.05f;
        [SerializeField, Min(0.05f)] private float pushIntentLifetime = 0.12f;

        [Header("Local visual anticipation")]
        [SerializeField, Min(0f)] private float maximumVisualLead = 0.5f;
        [SerializeField, Min(0f)] private float visualReconciliationSharpness = 10f;

        private readonly Dictionary<ulong, double> _lastPushTimeByClient = new();
        private readonly Dictionary<ulong, PushIntent> _pushIntentByClient = new();
        private readonly List<ulong> _expiredPushClients = new();
        private Vector3 _visualLeadWorld;
        private Vector3 _predictedVelocityWorld;
        private Vector3 _visualBaseLocalPosition;
        private Quaternion _visualBaseLocalRotation;
        private Vector3 _lastAuthoritativePosition;
        private float _nextLocalPushTime;
        private float _localPushActiveUntil;

        private void Awake()
        {
            body ??= GetComponent<Rigidbody>();
            interactionCollider ??= GetComponent<Collider>();
            if (visualRoot != null)
            {
                _visualBaseLocalPosition = visualRoot.localPosition;
                _visualBaseLocalRotation = visualRoot.localRotation;
                predictionCollider ??= visualRoot.GetComponent<Collider>();
            }

            ConfigureBody();
        }

        private void ConfigureBody()
        {
            if (body == null)
                return;

            body.mass = mass;
            body.linearDamping = linearDamping;
            body.angularDamping = angularDamping;
            body.maxLinearVelocity = maximumLinearSpeed;
            body.maxAngularVelocity = maximumAngularSpeed;
            body.useGravity = true;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            body.interpolation = RigidbodyInterpolation.Interpolate;
        }

        public override void OnNetworkSpawn()
        {
            ConfigureBody();
            body.isKinematic = !IsServer;
            if (interactionCollider != null)
                interactionCollider.enabled = IsServer;
            if (predictionCollider != null)
                predictionCollider.enabled = !IsServer;
            if (IsServer)
                body.WakeUp();

            _visualLeadWorld = Vector3.zero;
            _predictedVelocityWorld = Vector3.zero;
            _lastAuthoritativePosition = transform.position;
            if (visualRoot != null)
            {
                visualRoot.localPosition = _visualBaseLocalPosition;
                visualRoot.localRotation = _visualBaseLocalRotation;
            }
        }

        private void FixedUpdate()
        {
            if (!IsSpawned || !IsServer || body == null || body.isKinematic)
                return;

            ApplyServerPushIntents();
            body.linearVelocity = Vector3.ClampMagnitude(body.linearVelocity, maximumLinearSpeed);
            body.angularVelocity = Vector3.ClampMagnitude(body.angularVelocity, maximumAngularSpeed);
        }

        /// <summary>
        /// Called by the locally owned KCC when its movement sweep meets this object.
        /// Host and remote owners use this same path; KCC's own rigidbody impulse is disabled.
        /// </summary>
        public void RequestPush(Vector3 pushDirection, float approachSpeed)
        {
            if (!IsSpawned || Time.unscaledTime < _nextLocalPushTime)
                return;

            pushDirection = Vector3.ProjectOnPlane(pushDirection, Vector3.up);
            if (pushDirection.sqrMagnitude < 0.0001f || approachSpeed <= 0.01f)
                return;

            pushDirection.Normalize();
            approachSpeed = Mathf.Clamp(approachSpeed, 0f, maximumReportedPushSpeed);
            _nextLocalPushTime = Time.unscaledTime + pushRequestInterval;
            _localPushActiveUntil = Time.unscaledTime + pushIntentLifetime;

            if (!IsServer && visualRoot != null && maximumVisualLead > 0f)
            {
                // Mirror the exact translational delta-velocity the server applies. The
                // predicted child owns both rendering and collision, so the local KCC does
                // not fight a delayed authoritative collider while the acknowledgement is in flight.
                var normalizedEffort = Mathf.Clamp01(approachSpeed / maximumReportedPushSpeed);
                var predictedImpulse = pushDirection * pushForce * pushRequestInterval * normalizedEffort;
                _predictedVelocityWorld += predictedImpulse / Mathf.Max(0.1f, mass);
                _predictedVelocityWorld = Vector3.ClampMagnitude(
                    _predictedVelocityWorld,
                    maximumLinearSpeed);
            }

            if (IsServer)
                TrySetPushIntent(NetworkManager.LocalClientId, pushDirection, approachSpeed);
            else
                RequestPushServerRpc(pushDirection, approachSpeed);
        }

        [ServerRpc(RequireOwnership = false, Delivery = RpcDelivery.Unreliable)]
        private void RequestPushServerRpc(
            Vector3 reportedDirection,
            float reportedSpeed,
            ServerRpcParams rpcParams = default)
        {
            TrySetPushIntent(rpcParams.Receive.SenderClientId, reportedDirection, reportedSpeed);
        }

        private void TrySetPushIntent(ulong senderClientId, Vector3 reportedDirection, float reportedSpeed)
        {
            if (body == null || body.isKinematic || interactionCollider == null ||
                NetworkManager == null ||
                !NetworkManager.ConnectedClients.TryGetValue(
                    senderClientId,
                    out var client) ||
                client.PlayerObject == null)
                return;

            var now = NetworkManager.ServerTime.Time;
            if (_lastPushTimeByClient.TryGetValue(senderClientId, out var lastPushTime) &&
                now - lastPushTime < pushRequestInterval * 0.8f)
                return;

            var playerPosition = client.PlayerObject.transform.position;
            var closestPoint = interactionCollider.ClosestPoint(playerPosition);
            if ((closestPoint - playerPosition).sqrMagnitude > maximumPushDistance * maximumPushDistance)
                return;

            var direction = Vector3.ProjectOnPlane(reportedDirection, Vector3.up);
            var towardObject = Vector3.ProjectOnPlane(body.worldCenterOfMass - playerPosition, Vector3.up);
            if (direction.sqrMagnitude < 0.0001f || towardObject.sqrMagnitude < 0.0001f)
                return;
            direction.Normalize();
            towardObject.Normalize();
            if (Vector3.Dot(direction, towardObject) < 0.1f)
                return;

            // The server chooses the authoritative contact point and only accepts bounded speed.
            var speed = Mathf.Clamp(reportedSpeed, 0f, maximumReportedPushSpeed);
            if (speed <= 0.01f)
                return;

            var normalizedEffort = Mathf.Clamp01(speed / maximumReportedPushSpeed);
            _pushIntentByClient[senderClientId] = new PushIntent(
                direction,
                normalizedEffort,
                now + pushIntentLifetime);
            body.WakeUp();
            _lastPushTimeByClient[senderClientId] = now;
        }

        private void ApplyServerPushIntents()
        {
            if (NetworkManager == null || interactionCollider == null)
                return;

            var now = NetworkManager.ServerTime.Time;
            _expiredPushClients.Clear();
            foreach (var pair in _pushIntentByClient)
            {
                if (pair.Value.ExpiresAt < now ||
                    !NetworkManager.ConnectedClients.TryGetValue(pair.Key, out var client) ||
                    client.PlayerObject == null)
                {
                    _expiredPushClients.Add(pair.Key);
                    continue;
                }

                var playerPosition = client.PlayerObject.transform.position;
                var closestPoint = interactionCollider.ClosestPoint(playerPosition);
                if ((closestPoint - playerPosition).sqrMagnitude >
                    maximumPushDistance * maximumPushDistance)
                {
                    _expiredPushClients.Add(pair.Key);
                    continue;
                }

                body.AddForceAtPosition(
                    pair.Value.Direction * pushForce * pair.Value.Effort,
                    closestPoint,
                    ForceMode.Force);
            }

            foreach (var clientId in _expiredPushClients)
                _pushIntentByClient.Remove(clientId);
        }

        private void LateUpdate()
        {
            if (visualRoot == null || !IsSpawned || IsServer)
                return;

            var deltaTime = Time.unscaledDeltaTime;
            var authoritativeDelta = transform.position - _lastAuthoritativePosition;
            _lastAuthoritativePosition = transform.position;
            if (_visualLeadWorld.sqrMagnitude > 0.000001f ||
                _predictedVelocityWorld.sqrMagnitude > 0.000001f)
            {
                // Keep the predicted world pose stable as delayed server motion arrives.
                // That motion consumes the lead instead of being added a second time.
                _visualLeadWorld -= authoritativeDelta;
            }

            _visualLeadWorld += _predictedVelocityWorld * deltaTime;
            _predictedVelocityWorld *= Mathf.Exp(-linearDamping * deltaTime);
            _visualLeadWorld = Vector3.ClampMagnitude(_visualLeadWorld, maximumVisualLead);

            if (Time.unscaledTime > _localPushActiveUntil)
            {
                var reconciliation = 1f - Mathf.Exp(-visualReconciliationSharpness * deltaTime);
                var authoritativeVelocity = deltaTime > 0.0001f
                    ? authoritativeDelta / deltaTime
                    : Vector3.zero;
                _predictedVelocityWorld = Vector3.Lerp(
                    _predictedVelocityWorld,
                    authoritativeVelocity,
                    reconciliation);
                _visualLeadWorld = Vector3.Lerp(
                    _visualLeadWorld,
                    Vector3.zero,
                    reconciliation);
            }
            if (_visualLeadWorld.sqrMagnitude < 0.000001f)
                _visualLeadWorld = Vector3.zero;
            if (_predictedVelocityWorld.sqrMagnitude < 0.000001f)
                _predictedVelocityWorld = Vector3.zero;

            visualRoot.localPosition = _visualBaseLocalPosition +
                                       transform.InverseTransformVector(_visualLeadWorld);
            visualRoot.localRotation = _visualBaseLocalRotation;
        }

        public override void OnNetworkDespawn()
        {
            _lastPushTimeByClient.Clear();
            _pushIntentByClient.Clear();
            _expiredPushClients.Clear();
            _visualLeadWorld = Vector3.zero;
            _predictedVelocityWorld = Vector3.zero;
            if (interactionCollider != null)
                interactionCollider.enabled = true;
            if (predictionCollider != null)
                predictionCollider.enabled = false;
            if (visualRoot != null)
            {
                visualRoot.localPosition = _visualBaseLocalPosition;
                visualRoot.localRotation = _visualBaseLocalRotation;
            }

            base.OnNetworkDespawn();
        }

        private readonly struct PushIntent
        {
            public readonly Vector3 Direction;
            public readonly float Effort;
            public readonly double ExpiresAt;

            public PushIntent(Vector3 direction, float effort, double expiresAt)
            {
                Direction = direction;
                Effort = effort;
                ExpiresAt = expiresAt;
            }
        }
    }
}
