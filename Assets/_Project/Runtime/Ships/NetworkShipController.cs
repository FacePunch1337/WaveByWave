using StylizedWater3;
using Unity.Netcode;
using UnityEngine;
using WaveByWave.Player;
using WaveByWave.Collision;

namespace WaveByWave.Ships
{
    [DefaultExecutionOrder(-100)]
    [RequireComponent(typeof(NetworkObject), typeof(Rigidbody), typeof(MovingPlatform))]
    [RequireComponent(typeof(KinematicShipCollision))]
    public sealed class NetworkShipController : NetworkBehaviour
    {
        public const ulong NoHelmsman = ulong.MaxValue;

        [SerializeField] private Rigidbody body;
        [SerializeField, Min(0f)] private float maximumSpeed = 7f;
        [SerializeField, Min(0f)] private float acceleration = 2.5f;
        [SerializeField, Min(0f)] private float coastingDeceleration = 0.55f;
        [SerializeField, Min(0f)] private float turnSpeed = 28f;
        [SerializeField] private AlignToWater waterAlignment;

        [Header("Forward propulsion")]
        [Tooltip("Forward cruising speed with a fully deployed sail, before the wind bonus.")]
        [SerializeField, Min(0.1f)] private float baseCruiseSpeed = 2f;
        [SerializeField, Min(0f)] private float windSpeedBonus = 3f;
        [SerializeField, Min(0f)] private float windAccelerationBonus = 1.5f;
        [SerializeField, Min(0f)] private float lateralWaterDrag = 0.4f;

        [Header("Compliant buoyancy")]
        [SerializeField, Range(0.1f, 2f)] private float buoyancyFrequency = 0.8f;
        [SerializeField, Range(0.1f, 2f)] private float buoyancyTiltFrequency = 0.6f;
        [SerializeField, Range(0.5f, 2f)] private float buoyancyDamping = 1f;

        [Header("Hull collisions")]
        // Retained for old prefab/generator serialization. Motion queries use the model meshes.
        [SerializeField, HideInInspector] private BoxCollider collisionHull;
        [SerializeField] private LayerMask obstacleLayers = ~0;
        [SerializeField, Min(0.001f)] private float collisionSkin = 0.04f;

        [Header("Sailing")]
        [SerializeField] private NetworkWindController wind;
        [SerializeField] private ShipAnchor anchor;
        [SerializeField] private ShipSailControl sailControl;
        [SerializeField] private ShipMastControl mastControl;
        [SerializeField, Min(0f)] private float anchorBraking = 7f;
        [SerializeField, Min(0f)] private float sailAdjustmentSpeed = 0.4f;
        [Tooltip("Passive world-space drift along the wind with the anchor raised, including with furled sails.")]
        [SerializeField, Min(0f)] private float driftSpeed = 0.35f;
        [Tooltip("Initial sail state: 0 = fully furled, 1 = fully deployed.")]
        [SerializeField, Range(0f, 1f)] private float initialSailDeployment;

        [Header("Helm")]
        [SerializeField] private ShipHelm helm;
        [SerializeField, Min(0.5f)] private float helmInteractionRange = 5f;
        [Tooltip("Total wheel travel from the port lock to the starboard lock.")]
        [SerializeField, Min(1f)] private float helmTotalRotation = 720f;
        [SerializeField, Min(1f)] private float helmRotationSpeed = 180f;
        [Tooltip("Maximum hull yaw acceleration while the rudder is moving with the water flow.")]
        [SerializeField, Min(0.1f)] private float turnAcceleration = 3f;
        [SerializeField, Min(0.1f)] private float turnDeceleration = 5f;
        [Tooltip("Forward speed at which the rudder reaches full authority.")]
        [SerializeField, Min(0.1f)] private float fullRudderAuthoritySpeed = 2.5f;
        [Tooltip("The ship can still answer the helm at very low speed while the anchor is raised.")]
        [SerializeField, Range(0.01f, 1f)] private float minimumRudderAuthority = 0.18f;
        [Tooltip("Fraction of forward speed lost at full rudder lock.")]
        [SerializeField, Range(0f, 0.5f)] private float fullRudderSpeedPenalty = 0.12f;

        [Header("Player spawning")]
        [SerializeField] private Transform[] playerSpawnPoints;

        private readonly NetworkVariable<ulong> _helmsmanClientId = new(NoHelmsman);
        private readonly NetworkVariable<float> _helmAngle = new();
        private readonly NetworkVariable<float> _replicatedSpeed = new();
        private readonly NetworkVariable<float> _replicatedTurnRate = new();
        private readonly NetworkVariable<Vector3> _replicatedPlanarVelocity = new();
        private readonly NetworkVariable<bool> _anchorLowered = new(true);
        private readonly NetworkVariable<float> _sailDeployment = new(0f);
        private readonly NetworkVariable<ulong> _sailOperatorClientId = new(NoHelmsman);
        private readonly NetworkVariable<float> _mastAngle = new();
        private readonly NetworkVariable<ulong> _mastOperatorClientId = new(NoHelmsman);
        private float _helmInput;
        private float _sailInput;
        private float _mastInput;
        private Vector3 _linearVelocity;
        private Vector3 _angularVelocity;
        private float _heading;
        private Vector3 _planarPosition;
        private Transform _planarMotionTarget;
        private MovingPlatform _movingPlatform;
        private KinematicShipCollision _geometryCollision;
        private Vector3 _collisionStartPosition;
        private Quaternion _collisionStartRotation;
        private float _previousHeading;
        private bool _collisionTargetResolved;
        private Vector3 _resolvedTargetPosition;
        private Quaternion _resolvedTargetRotation;

        public ulong HelmsmanClientId => _helmsmanClientId.Value;
        public float HelmAngle => _helmAngle.Value;
        public float HelmHalfRange => Mathf.Max(0.5f, helmTotalRotation * 0.5f);
        public float CurrentSpeed => _replicatedSpeed.Value;
        public float CurrentTurnRate => _replicatedTurnRate.Value;
        public bool AnchorLowered => _anchorLowered.Value;
        public float SailDeployment => _sailDeployment.Value;
        public float InitialSailDeployment => initialSailDeployment;
        public float MastAngle => _mastAngle.Value;
        public Vector3 WindDirection => wind != null ? wind.Direction : Vector3.forward;
        public Vector3 SailNormal => sailControl != null ? sailControl.SailNormal : transform.forward;
        public float SailWindFill => sailControl != null
            ? sailControl.GetWindCapture(WindDirection)
            : Mathf.Abs(Vector3.Dot(WindDirection.normalized, transform.forward));

        public bool TryGetPlayerSpawnPoint(ulong clientId, out Transform spawnPoint)
        {
            spawnPoint = null;
            if (playerSpawnPoints == null || playerSpawnPoints.Length == 0)
                return false;

            var validCount = 0;
            foreach (var point in playerSpawnPoints)
            {
                if (point != null)
                    validCount++;
            }

            if (validCount == 0)
                return false;

            var requestedIndex = (int)(clientId % (ulong)validCount);
            foreach (var point in playerSpawnPoints)
            {
                if (point == null)
                    continue;

                if (requestedIndex-- == 0)
                {
                    spawnPoint = point;
                    return true;
                }
            }

            return false;
        }

        private void Awake()
        {
            body ??= GetComponent<Rigidbody>();
            waterAlignment ??= GetComponent<AlignToWater>();
            collisionHull ??= GetComponent<BoxCollider>();
            helm ??= GetComponentInChildren<ShipHelm>(true);
            wind ??= GetComponent<NetworkWindController>();
            anchor ??= GetComponentInChildren<ShipAnchor>(true);
            sailControl ??= GetComponentInChildren<ShipSailControl>(true);
            mastControl ??= GetComponentInChildren<ShipMastControl>(true);
            _movingPlatform = GetComponent<MovingPlatform>();
            _geometryCollision = GetComponent<KinematicShipCollision>();
            if (_geometryCollision == null)
                _geometryCollision = gameObject.AddComponent<KinematicShipCollision>();
            body.isKinematic = true;
            body.useGravity = false;
            body.interpolation = RigidbodyInterpolation.None;
            body.collisionDetectionMode = CollisionDetectionMode.Discrete;
            _heading = transform.eulerAngles.y;
        }

        public override void OnNetworkSpawn()
        {
            // This vessel is driven explicitly by AlignToWater, never by dynamic physics.
            // Reassert after all NGO spawn callbacks so no networking component can change it.
            body.isKinematic = true;
            body.useGravity = false;
            body.interpolation = RigidbodyInterpolation.None;
            body.collisionDetectionMode = CollisionDetectionMode.Discrete;

            if (waterAlignment != null)
            {
                waterAlignment.enabled = IsServer;
                waterAlignment.externalMotionControl = IsServer;
            }

            if (IsServer)
            {
                _heading = transform.eulerAngles.y;
                _planarPosition = transform.position;
                _helmAngle.Value = 0f;
                _sailDeployment.Value = Mathf.Clamp01(initialSailDeployment);
                _mastAngle.Value = mastControl != null ? mastControl.InitialAngle : 0f;
                _geometryCollision.Initialize(obstacleLayers, collisionSkin);
                _collisionStartPosition = body.position;
                _collisionStartRotation = body.rotation;
                _resolvedTargetPosition = body.position;
                _resolvedTargetRotation = body.rotation;
                _previousHeading = _heading;
                _linearVelocity = Vector3.zero;
                _angularVelocity = Vector3.zero;
                _collisionTargetResolved = false;
                CreatePlanarMotionTarget();
                NetworkManager.OnClientDisconnectCallback += OnClientDisconnected;
            }

            // Every instance uses KCC for fixed-step collisions. Clients sample
            // timestamped physics snapshots independently for fixed and render time,
            // with passenger motion interpolated in deck space.
            _movingPlatform?.EnableKccMover();
        }

        public override void OnNetworkDespawn()
        {
            if (NetworkManager != null)
                NetworkManager.OnClientDisconnectCallback -= OnClientDisconnected;

            if (waterAlignment != null)
                waterAlignment.externalMotionControl = false;
            _movingPlatform?.DisableKccMover();
            _geometryCollision?.Release();
            DestroyPlanarMotionTarget();
        }

        private void FixedUpdate()
        {
            if (!IsSpawned || !IsServer)
                return;

            _movingPlatform?.RestoreKccSimulationPose();
            _collisionStartPosition = body.position;
            _collisionStartRotation = body.rotation;
            _collisionTargetResolved = false;
            _previousHeading = _heading;

            var deltaTime = Time.fixedDeltaTime;
            _sailDeployment.Value = Mathf.Clamp01(_sailDeployment.Value +
                                                   _sailInput * sailAdjustmentSpeed * deltaTime);
            if (mastControl != null)
            {
                var requestedAngle = _mastAngle.Value + _mastInput * mastControl.RotationSpeed * deltaTime;
                _mastAngle.Value = mastControl.ClampAngle(requestedAngle);
            }

            _helmAngle.Value = Mathf.Clamp(
                _helmAngle.Value + _helmInput * helmRotationSpeed * deltaTime,
                -HelmHalfRange,
                HelmHalfRange);
            var rudder = Mathf.Clamp(_helmAngle.Value / HelmHalfRange, -1f, 1f);

            var windDirection = Vector3.ProjectOnPlane(WindDirection, Vector3.up).normalized;
            if (windDirection.sqrMagnitude < 0.001f)
                windDirection = Vector3.forward;

            var currentForward = Quaternion.Euler(0f, _heading, 0f) * Vector3.forward;
            var sailCapture = sailControl != null
                ? sailControl.GetWindCapture(windDirection)
                : Mathf.Abs(Vector3.Dot(windDirection, currentForward));
            var deployment = Mathf.Clamp01(_sailDeployment.Value);
            var sailLoad = deployment * Mathf.Clamp01(sailCapture);
            var speedLimit = Mathf.Max(0.1f, maximumSpeed);
            var cruiseSpeed = Mathf.Clamp(baseCruiseSpeed, 0.1f, speedLimit);
            var targetSpeed = Mathf.Min(speedLimit, cruiseSpeed * deployment + windSpeedBonus * sailLoad);
            targetSpeed *= 1f - Mathf.Abs(rudder) * fullRudderSpeedPenalty;
            var planarVelocity = Vector3.ProjectOnPlane(_linearVelocity, Vector3.up);
            if (_anchorLowered.Value)
            {
                planarVelocity = Vector3.MoveTowards(planarVelocity, Vector3.zero, anchorBraking * deltaTime);
            }
            else
            {
                // Drift is an environmental velocity in world space. Turning
                // the hull must not redirect existing momentum toward its bow.
                var driftVelocity = windDirection * driftSpeed;
                var relativeVelocity = planarVelocity - driftVelocity;
                // Passive water resistance acts with any sail state. With furled
                // sails this is the only change to horizontal momentum.
                var passiveDrag = coastingDeceleration / Mathf.Max(0.1f, cruiseSpeed);
                relativeVelocity *= Mathf.Exp(-passiveDrag * deltaTime);
                if (deployment > 0f)
                {
                    // Both base propulsion and wind assistance need exposed sail.
                    var forwardSpeed = Vector3.Dot(relativeVelocity, currentForward);
                    var driveAcceleration = acceleration * deployment + windAccelerationBonus * sailLoad;
                    var change = Mathf.Clamp(targetSpeed - forwardSpeed,
                        -coastingDeceleration * deltaTime, driveAcceleration * deltaTime);
                    relativeVelocity += currentForward * change;
                    var sideways = Vector3.ProjectOnPlane(relativeVelocity, currentForward);
                    relativeVelocity -= sideways * (1f - Mathf.Exp(-lateralWaterDrag * deployment * deltaTime));
                }
                planarVelocity = driftVelocity + relativeVelocity;
            }
            _linearVelocity.x = planarVelocity.x;
            _linearVelocity.z = planarVelocity.z;

            // The helm accelerates persistent angular momentum. Contacts may
            // change it; the next tick supplies torque rather than restoring a pose.
            var flowBasedAuthority = Mathf.Lerp(minimumRudderAuthority, 1f,
                Mathf.Clamp01(planarVelocity.magnitude / Mathf.Max(0.1f, fullRudderAuthoritySpeed)));
            var rudderAuthority = Mathf.Lerp(1f, flowBasedAuthority, deployment);
            var targetYaw = rudder * turnSpeed * rudderAuthority * Mathf.Deg2Rad;
            var yawAcceleration = Mathf.Abs(targetYaw) > Mathf.Abs(_angularVelocity.y)
                ? turnAcceleration : turnDeceleration;
            _angularVelocity.y = Mathf.MoveTowards(_angularVelocity.y, targetYaw,
                yawAcceleration * Mathf.Deg2Rad * deltaTime);
            // Sample water at the predicted horizontal position, but only the
            // unified momentum/contact solver is allowed to move the actual hull.
            _planarPosition = _collisionStartPosition + planarVelocity * deltaTime;
            if (waterAlignment != null)
            {
                waterAlignment.rotation = _heading;
                if (_planarMotionTarget != null)
                {
                    _planarMotionTarget.position = _planarPosition;
                    waterAlignment.followTarget = _planarMotionTarget;
                }
            }

        }

        public bool TryGetKccMoverTarget(out Vector3 position, out Quaternion rotation)
        {
            if (IsSpawned && IsServer && waterAlignment != null &&
                waterAlignment.HasExternalMotionTarget)
            {
                if (_collisionTargetResolved)
                {
                    position = _resolvedTargetPosition;
                    rotation = _resolvedTargetRotation;
                    return true;
                }

                var deltaTime = Mathf.Max(Time.fixedDeltaTime, 0.0001f);
                ApplyBuoyancy(waterAlignment.ExternalMotionTargetPosition,
                    waterAlignment.ExternalMotionTargetRotation, deltaTime);
                // Buoyancy, propulsion, helm and contacts share one velocity state.
                // AlignToWater is a sampled equilibrium, never a second pose writer.
                var resolved = _geometryCollision.ResolveMotion(_collisionStartPosition,
                    _collisionStartRotation, _linearVelocity, _angularVelocity, deltaTime);
                position = resolved.Position;
                rotation = resolved.Rotation;
                _linearVelocity = resolved.Velocity;
                _angularVelocity = resolved.AngularVelocity;

                var tilt = Quaternion.FromToRotation(Vector3.up, rotation * Vector3.up);
                var yawForward = Quaternion.Inverse(tilt) * (rotation * Vector3.forward);
                _heading = Mathf.Repeat(Mathf.Atan2(yawForward.x, yawForward.z) * Mathf.Rad2Deg, 360f);
                _planarPosition = position;
                waterAlignment.rotation = _heading;
                if (_planarMotionTarget != null)
                    _planarMotionTarget.position = _planarPosition;

                var acceptedVelocity = (position - _collisionStartPosition) / deltaTime;
                acceptedVelocity.y = 0f;
                _replicatedPlanarVelocity.Value = acceptedVelocity;
                _replicatedTurnRate.Value = Mathf.DeltaAngle(_previousHeading, _heading) / deltaTime;
                _replicatedSpeed.Value = acceptedVelocity.magnitude;
                _resolvedTargetPosition = position;
                _resolvedTargetRotation = rotation;
                _collisionTargetResolved = true;
                return true;
            }

            position = default;
            rotation = Quaternion.identity;
            return false;
        }

        private void ApplyBuoyancy(Vector3 waterPosition, Quaternion waterRotation, float deltaTime)
        {
            // Implicit damped springs remain stable at fixed steps and retain
            // collision displacement. The sea may restore it gradually afterward.
            var omega = 2f * Mathf.PI * buoyancyFrequency;
            var denominator = 1f + 2f * buoyancyDamping * omega * deltaTime + omega * omega * deltaTime * deltaTime;
            _linearVelocity.y = (_linearVelocity.y + omega * omega *
                (waterPosition.y - _collisionStartPosition.y) * deltaTime) / denominator;

            var currentUp = _collisionStartRotation * Vector3.up;
            var waterUp = waterRotation * Vector3.up;
            var axis = Vector3.Cross(currentUp, waterUp);
            var tiltError = axis.sqrMagnitude > 0.000001f
                ? axis.normalized * (Vector3.Angle(currentUp, waterUp) * Mathf.Deg2Rad)
                : Vector3.zero;
            omega = 2f * Mathf.PI * buoyancyTiltFrequency;
            denominator = 1f + 2f * buoyancyDamping * omega * deltaTime + omega * omega * deltaTime * deltaTime;
            _angularVelocity.x = (_angularVelocity.x + omega * omega * tiltError.x * deltaTime) / denominator;
            _angularVelocity.z = (_angularVelocity.z + omega * omega * tiltError.z * deltaTime) / denominator;
        }

        public Vector3 GetPlanarPointVelocity(Vector3 worldPoint)
        {
            var linearVelocity = _replicatedPlanarVelocity.Value;
            var angularVelocity = Vector3.up * (CurrentTurnRate * Mathf.Deg2Rad);
            var planarRadius = Vector3.ProjectOnPlane(worldPoint - transform.position, Vector3.up);
            return linearVelocity + Vector3.Cross(angularVelocity, planarRadius);
        }

        private void CreatePlanarMotionTarget()
        {
            if (_planarMotionTarget != null)
                return;

            var target = new GameObject($"{name} Planar Motion Target")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            _planarMotionTarget = target.transform;
            _planarMotionTarget.position = _planarPosition;

            if (waterAlignment != null)
                waterAlignment.followTarget = _planarMotionTarget;
        }

        private void DestroyPlanarMotionTarget()
        {
            if (waterAlignment != null && waterAlignment.followTarget == _planarMotionTarget)
                waterAlignment.followTarget = null;

            if (_planarMotionTarget != null)
                Destroy(_planarMotionTarget.gameObject);
            _planarMotionTarget = null;
        }

        public override void OnDestroy()
        {
            DestroyPlanarMotionTarget();
            base.OnDestroy();
        }

        [ServerRpc(RequireOwnership = false)]
        public void RequestHelmServerRpc(ServerRpcParams rpcParams = default)
        {
            var sender = rpcParams.Receive.SenderClientId;
            if (_helmsmanClientId.Value != NoHelmsman && _helmsmanClientId.Value != sender)
                return;

            if (!IsPlayerNearHelm(sender))
                return;

            _helmsmanClientId.Value = sender;
        }

        [ServerRpc(RequireOwnership = false, Delivery = RpcDelivery.Unreliable)]
        public void SubmitHelmInputServerRpc(float steer, ServerRpcParams rpcParams = default)
        {
            if (_helmsmanClientId.Value != rpcParams.Receive.SenderClientId)
                return;

            _helmInput = Mathf.Clamp(steer, -1f, 1f);
        }

        [ServerRpc(RequireOwnership = false)]
        public void ReleaseHelmServerRpc(ServerRpcParams rpcParams = default)
        {
            if (_helmsmanClientId.Value != rpcParams.Receive.SenderClientId)
                return;

            _helmsmanClientId.Value = NoHelmsman;
            _helmInput = 0f;
        }

        [ServerRpc(RequireOwnership = false)]
        public void ToggleAnchorServerRpc(ServerRpcParams rpcParams = default)
        {
            var interactionPoint = anchor != null ? anchor.transform : transform;
            if (!IsPlayerNearInteraction(rpcParams.Receive.SenderClientId, interactionPoint, 5f))
                return;

            _anchorLowered.Value = !_anchorLowered.Value;
        }

        [ServerRpc(RequireOwnership = false)]
        public void RequestSailControlServerRpc(ServerRpcParams rpcParams = default)
        {
            var sender = rpcParams.Receive.SenderClientId;
            if (_sailOperatorClientId.Value != NoHelmsman && _sailOperatorClientId.Value != sender)
                return;

            var interactionPoint = sailControl != null ? sailControl.transform : transform;
            if (!IsPlayerNearInteraction(sender, interactionPoint, 5f))
                return;

            _sailOperatorClientId.Value = sender;
        }

        [ServerRpc(RequireOwnership = false, Delivery = RpcDelivery.Unreliable)]
        public void SubmitSailInputServerRpc(float input, ServerRpcParams rpcParams = default)
        {
            if (_sailOperatorClientId.Value != rpcParams.Receive.SenderClientId)
                return;

            _sailInput = Mathf.Clamp(input, -1f, 1f);
        }

        [ServerRpc(RequireOwnership = false)]
        public void ReleaseSailControlServerRpc(ServerRpcParams rpcParams = default)
        {
            if (_sailOperatorClientId.Value != rpcParams.Receive.SenderClientId)
                return;

            _sailOperatorClientId.Value = NoHelmsman;
            _sailInput = 0f;
        }

        [ServerRpc(RequireOwnership = false)]
        public void RequestMastControlServerRpc(ServerRpcParams rpcParams = default)
        {
            var sender = rpcParams.Receive.SenderClientId;
            if (_mastOperatorClientId.Value != NoHelmsman && _mastOperatorClientId.Value != sender)
                return;

            var interactionPoint = mastControl != null ? mastControl.transform : transform;
            if (!IsPlayerNearInteraction(sender, interactionPoint, 5f))
                return;

            _mastOperatorClientId.Value = sender;
        }

        [ServerRpc(RequireOwnership = false, Delivery = RpcDelivery.Unreliable)]
        public void SubmitMastInputServerRpc(float input, ServerRpcParams rpcParams = default)
        {
            if (_mastOperatorClientId.Value != rpcParams.Receive.SenderClientId)
                return;

            _mastInput = Mathf.Clamp(input, -1f, 1f);
        }

        [ServerRpc(RequireOwnership = false)]
        public void ReleaseMastControlServerRpc(ServerRpcParams rpcParams = default)
        {
            if (_mastOperatorClientId.Value != rpcParams.Receive.SenderClientId)
                return;

            _mastOperatorClientId.Value = NoHelmsman;
            _mastInput = 0f;
        }

        private bool IsPlayerNearHelm(ulong clientId)
        {
            if (!NetworkManager.ConnectedClients.TryGetValue(clientId, out var client) || client.PlayerObject == null)
                return false;

            // The visual hull and helm are free to be offset from the network root. Validation
            // must therefore use the actual interaction location, not the ship origin.
            var interactionPosition = helm != null ? helm.transform.position : transform.position;
            return (client.PlayerObject.transform.position - interactionPosition).sqrMagnitude <=
                   helmInteractionRange * helmInteractionRange;
        }

        private bool IsPlayerNearInteraction(ulong clientId, Transform interactionPoint, float range)
        {
            if (!NetworkManager.ConnectedClients.TryGetValue(clientId, out var client) ||
                client.PlayerObject == null || interactionPoint == null)
                return false;

            return (client.PlayerObject.transform.position - interactionPoint.position).sqrMagnitude <= range * range;
        }

        private void OnClientDisconnected(ulong clientId)
        {
            if (_helmsmanClientId.Value == clientId)
            {
                _helmsmanClientId.Value = NoHelmsman;
                _helmInput = 0f;
            }


            if (_sailOperatorClientId.Value == clientId)
            {
                _sailOperatorClientId.Value = NoHelmsman;
                _sailInput = 0f;
            }

            if (_mastOperatorClientId.Value == clientId)
            {
                _mastOperatorClientId.Value = NoHelmsman;
                _mastInput = 0f;
            }
        }

        private void OnDrawGizmosSelected()
        {
            if (playerSpawnPoints == null)
                return;

            Gizmos.color = new Color(0.1f, 0.9f, 1f, 1f);
            foreach (var point in playerSpawnPoints)
            {
                if (point == null)
                    continue;

                Gizmos.DrawWireSphere(point.position, 0.3f);
                Gizmos.DrawRay(point.position, point.forward * 0.8f);
            }
        }
    }
}
