using System.Collections.Generic;
using StylizedWater3;
using Unity.Netcode;
using Unity.Mathematics;
using UnityEngine;
using WaveByWave.Player;
using WaveByWave.Collision;
using WaveByWave.Items;

namespace WaveByWave.Ships
{
    [DefaultExecutionOrder(-100)]
    [RequireComponent(typeof(NetworkObject), typeof(Rigidbody), typeof(MovingPlatform))]
    [RequireComponent(typeof(KinematicShipCollision))]
    public sealed class NetworkShipController : NetworkBehaviour
    {
        public const ulong NoHelmsman = ulong.MaxValue;

        private static readonly List<NetworkShipController> ServerShipRegistry = new();
        internal static IReadOnlyList<NetworkShipController> ServerShips => ServerShipRegistry;
        internal Vector3 SimulationPosition => body.position;
        internal Vector3 ContactBoundsCenter => _geometryCollision != null
            ? _geometryCollision.HullCenter : Vector3.zero;
        internal Vector3 ContactBoundsHalfExtents => _geometryCollision != null
            ? _geometryCollision.HullHalfSize : Vector3.one;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetServerShipRegistry() => ServerShipRegistry.Clear();

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
        private readonly NetworkVariable<float> _speedUpgradeBonus = new();
        private readonly NetworkVariable<float> _turnUpgradeBonus = new();
        private readonly NetworkVariable<bool> _anchorLowered = new(true);
        private readonly NetworkVariable<bool> _anchorDropping = new();
        private readonly NetworkVariable<float> _anchorRaiseProgress = new();
        private readonly NetworkVariable<float> _anchorCapstanAngle = new();
        private readonly NetworkVariable<int> _anchorPushingCount = new();
        private NetworkList<ulong> _anchorOperators;
        private AnchorPushInput[] _anchorPushInputs;
        private readonly Dictionary<ulong, AnchorLowerHold> _anchorLowerHolds = new();
        private readonly List<ulong> _expiredAnchorHolds = new();
        private const double AnchorInputTimeout = 0.4;
        private double _anchorDropStarted;
        private float _anchorDropStartProgress;
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
        private ShipFlooding _flooding;
        private EquipmentWaterQuery _shipWaterQuery;
        private EquipmentWaterQuery.CachedSurface _shipWaterSource;
        private WaveProfile _authoredWaterProfile;
        private float _smoothedWaterHeight;
        private Vector3 _smoothedWaterNormal = Vector3.up;
        private bool _waterSampleInitialized;
        private readonly Dictionary<int, RigidTransform> _nearbyEnemyCollisionPoses = new();
        private uint _nearbyEnemyCollisionRevision;
        private Vector3 _collisionStartPosition;
        private Quaternion _collisionStartRotation;
        private float _previousHeading;
        private bool _collisionTargetResolved;
        private Vector3 _resolvedTargetPosition;
        private Quaternion _resolvedTargetRotation;

        private struct AnchorPushInput
        {
            public bool Pushing;
            public double LastReceived;
        }

        private sealed class AnchorLowerHold
        {
            public double Started;
            public double LastReceived;
            public bool CompletionRequested;
        }

        public ulong HelmsmanClientId => _helmsmanClientId.Value;
        public float HelmAngle => _helmAngle.Value;
        public float HelmHalfRange => Mathf.Max(0.5f, helmTotalRotation * 0.5f);
        public float CurrentSpeed => _replicatedSpeed.Value;
        public float CurrentTurnRate => _replicatedTurnRate.Value;
        public bool AnchorLowered => _anchorLowered.Value;
        public bool AnchorDropping => _anchorDropping.Value;
        public bool CanBeginAnchorDrop => !AnchorDropping && AnchorRaiseProgress >= 1f && AnchorHoldingCount == 0;
        public int AnchorHoldingCount
        {
            get
            {
                var count = 0;
                if (_anchorOperators != null)
                    for (var i = 0; i < _anchorOperators.Count; i++)
                        if (_anchorOperators[i] != NoHelmsman)
                            count++;
                return count;
            }
        }
        public ShipAnchor Anchor => anchor;
        public float AnchorRaiseProgress => _anchorRaiseProgress.Value;
        public float AnchorCapstanAngle => _anchorCapstanAngle.Value;
        public int AnchorPushingCount => _anchorPushingCount.Value;
        public bool IsClientOperatingNavigationStation(ulong clientId) =>
            _helmsmanClientId.Value == clientId || _sailOperatorClientId.Value == clientId ||
            _mastOperatorClientId.Value == clientId || GetAnchorHandleForClient(clientId) >= 0;
        private bool IsClientAtCannon(ulong clientId) => TryGetComponent<ShipCannonBattery>(out var battery) &&
            battery.GetOperatorCannon(clientId) >= 0;
        public void ApplySailingUpgradeServer(ShipUpgradeStat stat, float bonus)
        {
            if (!IsServer || float.IsNaN(bonus) || float.IsInfinity(bonus)) return;
            if (stat == ShipUpgradeStat.Speed)
                _speedUpgradeBonus.Value = Mathf.Clamp(_speedUpgradeBonus.Value + bonus, 0f, 2f);
            else if (stat == ShipUpgradeStat.Maneuverability)
                _turnUpgradeBonus.Value = Mathf.Clamp(_turnUpgradeBonus.Value + bonus, 0f, 2f);
        }
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
            _anchorOperators = new NetworkList<ulong>();
            body ??= GetComponent<Rigidbody>();
            waterAlignment ??= GetComponent<AlignToWater>();
            _authoredWaterProfile = waterAlignment != null ? waterAlignment.heightInterface.waveProfile : null;
            collisionHull ??= GetComponent<BoxCollider>();
            helm ??= GetComponentInChildren<ShipHelm>(true);
            wind ??= GetComponent<NetworkWindController>();
            anchor ??= GetComponentInChildren<ShipAnchor>(true);
            sailControl ??= GetComponentInChildren<ShipSailControl>(true);
            mastControl ??= GetComponentInChildren<ShipMastControl>(true);
            _movingPlatform = GetComponent<MovingPlatform>();
            _geometryCollision = GetComponent<KinematicShipCollision>();
            _flooding = GetComponent<ShipFlooding>();
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
            // This vessel samples water from its spawn-time snapshot, never by dynamic physics.
            // Reassert after all NGO spawn callbacks so no networking component can change it.
            body.isKinematic = true;
            body.useGravity = false;
            body.interpolation = RigidbodyInterpolation.None;
            body.collisionDetectionMode = CollisionDetectionMode.Discrete;

            if (waterAlignment != null)
            {
                // Its authored footprint/offset/smoothing remain the configuration,
                // but the hull samples a frozen water snapshot instead of live material.
                waterAlignment.enabled = false;
                waterAlignment.externalMotionControl = false;
            }

            if (IsServer)
            {
                _shipWaterQuery = new EquipmentWaterQuery(_authoredWaterProfile);
                _shipWaterSource = _shipWaterQuery.CacheSurface(body.position, _authoredWaterProfile);
                _waterSampleInitialized = false;
                if (!ServerShipRegistry.Contains(this)) ServerShipRegistry.Add(this);
                _anchorOperators.Clear();
                _anchorPushInputs = new AnchorPushInput[anchor != null ? anchor.HandleCount : 0];
                for (var i = 0; i < _anchorPushInputs.Length; i++)
                    _anchorOperators.Add(NoHelmsman);
                _anchorLowerHolds.Clear();
                _anchorLowered.Value = true;
                _anchorDropping.Value = false;
                _anchorRaiseProgress.Value = 0f;
                _anchorCapstanAngle.Value = 0f;
                _anchorPushingCount.Value = 0;
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
            ServerShipRegistry.Remove(this);
            _anchorLowerHolds.Clear();
            _anchorPushInputs = null;
            if (NetworkManager != null)
                NetworkManager.OnClientDisconnectCallback -= OnClientDisconnected;

            if (waterAlignment != null)
                waterAlignment.externalMotionControl = false;
            _shipWaterQuery?.Dispose();
            _shipWaterQuery = null;
            _shipWaterSource = null;
            _movingPlatform?.DisableKccMover();
            _geometryCollision?.Release();
            DestroyPlanarMotionTarget();
        }

        private void FixedUpdate()
        {
            if (!IsSpawned || !IsServer)
                return;

            if (_flooding != null && _flooding.IsSinking)
            { _linearVelocity = Vector3.zero; _angularVelocity = Vector3.zero; return; }

            if (_shipWaterSource == null)
                _shipWaterSource = _shipWaterQuery?.CacheSurface(body.position, _authoredWaterProfile);

            UpdateAnchorOperation();

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
            var speedFactor = 1f + _speedUpgradeBonus.Value;
            var speedLimit = Mathf.Max(0.1f, maximumSpeed * speedFactor);
            var cruiseSpeed = Mathf.Clamp(baseCruiseSpeed * speedFactor, 0.1f, speedLimit);
            var targetSpeed = Mathf.Min(speedLimit, cruiseSpeed * deployment + windSpeedBonus * speedFactor * sailLoad);
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
            var targetYaw = rudder * turnSpeed * (1f + _turnUpgradeBonus.Value) * rudderAuthority * Mathf.Deg2Rad;
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
            if (_flooding != null && _flooding.TrySinkingPose(out position, out rotation))
                return true;
            if (IsSpawned && IsServer && waterAlignment != null && _shipWaterSource != null)
            {
                if (_collisionTargetResolved)
                {
                    position = _resolvedTargetPosition;
                    rotation = _resolvedTargetRotation;
                    return true;
                }

                var deltaTime = Mathf.Max(Time.fixedDeltaTime, 0.0001f);
                var yaw = Quaternion.Euler(0f, _heading, 0f);
                if (_shipWaterQuery.TrySurface(_planarPosition, waterAlignment.surfaceSize,
                    yaw, waterAlignment.rollAmount, _shipWaterSource,
                    out var waterHeight, out var waterNormal))
                {
                    if (!_waterSampleInitialized)
                    {
                        _smoothedWaterHeight = waterHeight;
                        _smoothedWaterNormal = waterNormal;
                        _waterSampleInitialized = true;
                    }
                    else
                    {
                        var smoothing = waterAlignment.smoothing;
                        var blend = smoothing > 0f ? Mathf.Clamp01(deltaTime / smoothing) : 1f;
                        _smoothedWaterHeight = Mathf.Lerp(_smoothedWaterHeight, waterHeight, blend);
                        _smoothedWaterNormal = Vector3.Lerp(_smoothedWaterNormal, waterNormal, blend).normalized;
                    }
                    var waterTarget = _planarPosition;
                    waterTarget.y = _smoothedWaterHeight + waterAlignment.heightOffset;
                    var waterRotation = Quaternion.FromToRotation(Vector3.up, _smoothedWaterNormal) * yaw;
                    ApplyBuoyancy(waterTarget, waterRotation, deltaTime);
                }
                // Buoyancy, propulsion, helm and contacts share one velocity state.
                // The cached water sample is an equilibrium, never a second pose writer.
                var fleet = WaveByWave.Enemies.DotsEnemyShipRuntime.Instance;
                // An anchored hull is a fixed obstacle to the fleet. Enemy ships
                // still cast against its real colliders and must resolve the contact.
                var hasFleet = !_anchorLowered.Value && fleet != null && fleet.CanSimulate;
                if (hasFleet)
                {
                    var hullRadius = ContactBoundsCenter.magnitude + ContactBoundsHalfExtents.magnitude;
                    var enemyRadius = fleet.ContactBoundsCenter.magnitude + fleet.ContactBoundsHalfExtents.magnitude;
                    var travel = _linearVelocity.magnitude * deltaTime +
                        _angularVelocity.magnitude * deltaTime * hullRadius;
                    if (fleet.FillCollisionPosesNear(_collisionStartPosition,
                        hullRadius + enemyRadius + travel + collisionSkin + 2f,
                        _nearbyEnemyCollisionPoses)) _nearbyEnemyCollisionRevision++;
                }
                else if (_nearbyEnemyCollisionPoses.Count > 0)
                {
                    _nearbyEnemyCollisionPoses.Clear();
                    _nearbyEnemyCollisionRevision++;
                }
                var fleetReady = _geometryCollision.SetFleetObstacles(
                    hasFleet ? fleet.ContactBoundsCenter : Vector3.zero,
                    hasFleet ? fleet.ContactBoundsHalfExtents : Vector3.zero,
                    hasFleet ? _nearbyEnemyCollisionPoses : null, _nearbyEnemyCollisionRevision);
                var incomingVelocity = _linearVelocity;
                var resolved = fleetReady
                    ? _geometryCollision.ResolveMotion(_collisionStartPosition,
                        _collisionStartRotation, _linearVelocity, _angularVelocity, deltaTime)
                    : new KinematicShipCollision.Motion(_collisionStartPosition,
                        _collisionStartRotation, true, Vector3.zero, _collisionStartPosition);
                position = resolved.Position;
                rotation = resolved.Rotation;
                _linearVelocity = resolved.Velocity;
                _angularVelocity = resolved.AngularVelocity;
                if (_anchorLowered.Value &&
                    Vector3.ProjectOnPlane(incomingVelocity, Vector3.up).sqrMagnitude < 0.0025f)
                {
                    position.x = _collisionStartPosition.x;
                    position.z = _collisionStartPosition.z;
                    rotation = Quaternion.FromToRotation(Vector3.up, resolved.Rotation * Vector3.up) *
                        Quaternion.Euler(0f, _collisionStartRotation.eulerAngles.y, 0f);
                    _linearVelocity.x = 0f;
                    _linearVelocity.z = 0f;
                    _angularVelocity.y = 0f;
                }
                if (hasFleet && _geometryCollision.TryGetFleetContact(out var enemyId, out var contactNormal))
                {
                    var planarNormal = Vector3.ProjectOnPlane(contactNormal, Vector3.up).normalized;
                    var impactSpeed = Mathf.Max(0f, -Vector3.Dot(
                        Vector3.ProjectOnPlane(incomingVelocity, Vector3.up), planarNormal));
                    if (impactSpeed > 0.01f)
                        fleet.ApplyPlayerContactPush(enemyId, -planarNormal * Mathf.Min(
                            fleet.Definition.MaximumContactPushSpeed,
                            impactSpeed * fleet.Definition.ContactPushStrength));
                }

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

        internal void ApplyEnemyContactPush(Vector3 velocity, float maximumPushSpeed)
        {
            if (!IsServer || _anchorLowered.Value) return;
            velocity.y = 0f;
            var vertical = _linearVelocity.y;
            var planar = Vector3.ProjectOnPlane(_linearVelocity, Vector3.up) + velocity;
            planar = Vector3.ClampMagnitude(planar,
                Mathf.Max(maximumSpeed, maximumPushSpeed));
            _linearVelocity = planar + Vector3.up * vertical;
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
            ServerShipRegistry.Remove(this);
            _shipWaterQuery?.Dispose();
            DestroyPlanarMotionTarget();
            base.OnDestroy();
        }

        [ServerRpc(RequireOwnership = false)]
        public void RequestHelmServerRpc(ServerRpcParams rpcParams = default)
        {
            var sender = rpcParams.Receive.SenderClientId;
            if (IsClientAtCannon(sender)) return;
            if (GetAnchorHandleForClient(sender) >= 0)
                return;
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
        public void RequestAnchorHandleServerRpc(ServerRpcParams rpcParams = default)
        {
            var sender = rpcParams.Receive.SenderClientId;
            if (IsClientAtCannon(sender)) { AnchorHandleResultClientRpc(sender, -1); return; }
            var selected = GetAnchorHandleForClient(sender);
            if (selected < 0 && anchor != null &&
                _helmsmanClientId.Value != sender && _sailOperatorClientId.Value != sender &&
                _mastOperatorClientId.Value != sender &&
                IsPlayerNearInteraction(sender, anchor.transform, 5f) &&
                TryGetPlayerInteractionPosition(sender, out var playerPosition))
            {
                var nearestDistance = float.PositiveInfinity;
                for (var i = 0; i < _anchorOperators.Count; i++)
                {
                    var station = anchor.GetHandleStation(i);
                    if (_anchorOperators[i] != NoHelmsman || station == null)
                        continue;
                    var distance = (station.position - playerPosition).sqrMagnitude;
                    if (distance < nearestDistance)
                    {
                        selected = i;
                        nearestDistance = distance;
                    }
                }
                if (selected >= 0)
                {
                    _anchorOperators[selected] = sender;
                    _anchorPushInputs[selected] = default;
                }
            }

            if (selected >= 0)
            {
                _anchorDropping.Value = false;
                // Grabbing the capstan cancels an outstanding release hold,
                // including a completion still waiting for the next fixed step.
                _anchorLowerHolds.Clear();
            }
            AnchorHandleResultClientRpc(sender, selected);
        }

        [ClientRpc]
        private void AnchorHandleResultClientRpc(ulong clientId, int handleIndex)
        {
            if (NetworkManager.LocalClientId != clientId)
                return;
            NetworkManager.LocalClient.PlayerObject?.GetComponent<NetworkPlayerController>()
                ?.HandleAnchorHandleResult(anchor, handleIndex);
        }

        public int GetAnchorHandleForClient(ulong clientId)
        {
            if (_anchorOperators == null)
                return -1;
            for (var i = 0; i < _anchorOperators.Count; i++)
                if (_anchorOperators[i] == clientId)
                    return i;
            return -1;
        }

        [ServerRpc(RequireOwnership = false)]
        public void ReleaseAnchorHandleServerRpc(ServerRpcParams rpcParams = default) =>
            ReleaseAnchorHandle(rpcParams.Receive.SenderClientId);

        private void ReleaseAnchorHandle(ulong clientId)
        {
            var index = GetAnchorHandleForClient(clientId);
            if (index < 0)
                return;
            _anchorOperators[index] = NoHelmsman;
            if (_anchorPushInputs != null)
                _anchorPushInputs[index] = default;
        }

        [ServerRpc(RequireOwnership = false, Delivery = RpcDelivery.Unreliable)]
        public void SubmitAnchorPushServerRpc(bool pushing, ServerRpcParams rpcParams = default)
        {
            var index = GetAnchorHandleForClient(rpcParams.Receive.SenderClientId);
            if (index < 0 || _anchorPushInputs == null)
                return;
            _anchorPushInputs[index] = new AnchorPushInput
            {
                Pushing = pushing,
                LastReceived = Time.unscaledTimeAsDouble
            };
        }

        [ServerRpc(RequireOwnership = false)]
        public void BeginAnchorLowerHoldServerRpc(ServerRpcParams rpcParams = default)
        {
            var sender = rpcParams.Receive.SenderClientId;
            if (anchor == null || !CanBeginAnchorDrop || _anchorLowerHolds.ContainsKey(sender) ||
                !IsPlayerNearInteraction(sender, anchor.transform, 5f))
                return;
            _anchorLowerHolds[sender] = new AnchorLowerHold
            {
                Started = Time.unscaledTimeAsDouble,
                LastReceived = Time.unscaledTimeAsDouble
            };
        }

        [ServerRpc(RequireOwnership = false, Delivery = RpcDelivery.Unreliable)]
        public void RefreshAnchorLowerHoldServerRpc(ServerRpcParams rpcParams = default)
        {
            if (_anchorLowerHolds.TryGetValue(rpcParams.Receive.SenderClientId, out var hold))
                hold.LastReceived = Time.unscaledTimeAsDouble;
        }

        [ServerRpc(RequireOwnership = false)]
        public void CompleteAnchorLowerHoldServerRpc(ServerRpcParams rpcParams = default)
        {
            if (_anchorLowerHolds.TryGetValue(rpcParams.Receive.SenderClientId, out var hold))
            {
                hold.CompletionRequested = true;
                hold.LastReceived = Time.unscaledTimeAsDouble;
            }
        }

        [ServerRpc(RequireOwnership = false)]
        public void CancelAnchorLowerHoldServerRpc(ServerRpcParams rpcParams = default)
        {
            var sender = rpcParams.Receive.SenderClientId;
            // Filling the bar commits the action. A release on the following
            // frame must not undo a completed hold while its RPC is in flight.
            if (_anchorLowerHolds.TryGetValue(sender, out var hold) && !hold.CompletionRequested)
                _anchorLowerHolds.Remove(sender);
        }

        private void UpdateAnchorOperation()
        {
            if (anchor == null || _anchorPushInputs == null)
                return;
            var now = Time.unscaledTimeAsDouble;
            var pushingCount = 0;
            var holdingCount = 0;
            for (var i = 0; i < _anchorOperators.Count; i++)
            {
                var operatorId = _anchorOperators[i];
                if (operatorId == NoHelmsman)
                    continue;
                if (!IsPlayerNearInteraction(operatorId, anchor.transform, 5f))
                {
                    ReleaseAnchorHandle(operatorId);
                    continue;
                }
                holdingCount++;
                var input = _anchorPushInputs[i];
                if (!AnchorDropping && AnchorRaiseProgress < 1f && input.Pushing &&
                    now - input.LastReceived <= AnchorInputTimeout)
                    pushingCount++;
            }
            _anchorPushingCount.Value = pushingCount;
            if (holdingCount > 0 && pushingCount == holdingCount)
            {
                SetAnchorRaiseProgress(Mathf.Min(1f, AnchorRaiseProgress +
                    Time.fixedDeltaTime * pushingCount / anchor.SoloRaiseDuration));
                if (_anchorRaiseProgress.Value >= 1f)
                {
                    _anchorLowered.Value = false;
                    _anchorPushingCount.Value = 0;
                }
            }

            // Grabbing any free handle arrests a falling anchor at its current
            // height. Letting go of all handles at a partial height resumes the
            // fall, whether it was caught or raised from the seabed.
            if (!AnchorDropping && holdingCount == 0 && AnchorRaiseProgress > 0f && AnchorRaiseProgress < 1f)
                StartAnchorDrop();
            if (AnchorDropping)
            {
                var elapsed = System.Math.Max(0d, Time.fixedTimeAsDouble - _anchorDropStarted);
                var progress = elapsed + 0.000001d >= _anchorDropStartProgress * anchor.DropDuration
                    ? 0f
                    : _anchorDropStartProgress - (float)(elapsed / anchor.DropDuration);
                SetAnchorRaiseProgress(progress);
                if (AnchorRaiseProgress <= 0f)
                    _anchorDropping.Value = false;
            }

            _expiredAnchorHolds.Clear();
            var lowerAnchor = false;
            foreach (var entry in _anchorLowerHolds)
            {
                var hold = entry.Value;
                if (!CanBeginAnchorDrop || now - hold.LastReceived > AnchorInputTimeout ||
                    !IsPlayerNearInteraction(entry.Key, anchor.transform, 5f))
                {
                    _expiredAnchorHolds.Add(entry.Key);
                    continue;
                }
                // A deadline alone never drops the anchor. Only a client that kept
                // E held until its bar filled sends this confirmation. An early
                // release cannot lower it, even when cancellation is delayed.
                if (hold.CompletionRequested && now - hold.Started >= anchor.LowerHoldDuration)
                    lowerAnchor = true;
            }
            foreach (var clientId in _expiredAnchorHolds)
                _anchorLowerHolds.Remove(clientId);
            if (lowerAnchor)
            {
                // Only an unoccupied capstan can free-spin. A new interaction
                // can catch the shaft without resetting its position.
                _anchorPushingCount.Value = 0;
                StartAnchorDrop();
                _anchorLowerHolds.Clear();
            }
        }

        private void SetAnchorRaiseProgress(float progress)
        {
            _anchorCapstanAngle.Value += (progress - AnchorRaiseProgress) * anchor.RaisingRotationDegrees;
            _anchorRaiseProgress.Value = progress;
            // A dropped anchor brakes at the bottom; an anchored ship is freed
            // when raising finishes. A partial catch preserves that state.
            if (progress <= 0f)
                _anchorLowered.Value = true;
            else if (progress >= 1f)
                _anchorLowered.Value = false;
        }

        private void StartAnchorDrop()
        {
            _anchorDropStarted = Time.fixedTimeAsDouble;
            _anchorDropStartProgress = AnchorRaiseProgress;
            _anchorDropping.Value = true;
        }

        [ServerRpc(RequireOwnership = false)]
        public void RequestSailControlServerRpc(ServerRpcParams rpcParams = default)
        {
            var sender = rpcParams.Receive.SenderClientId;
            if (IsClientAtCannon(sender)) return;
            if (GetAnchorHandleForClient(sender) >= 0)
                return;
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
            if (IsClientAtCannon(sender)) return;
            if (GetAnchorHandleForClient(sender) >= 0)
                return;
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
            if (interactionPoint == null || !TryGetPlayerInteractionPosition(clientId, out var position))
                return false;

            return (position - interactionPoint.position).sqrMagnitude <= range * range;
        }

        private bool TryGetPlayerInteractionPosition(ulong clientId, out Vector3 position)
        {
            position = default;
            if (!NetworkManager.ConnectedClients.TryGetValue(clientId, out var client) || client.PlayerObject == null)
                return false;
            var player = client.PlayerObject.GetComponent<NetworkPlayerController>();
            if (player != null && player.TryGetPositionOnPlatform(NetworkObject, out position))
                return true;
            position = client.PlayerObject.transform.position;
            return true;
        }

        private void OnClientDisconnected(ulong clientId)
        {
            ReleaseAnchorHandle(clientId);
            _anchorLowerHolds.Remove(clientId);
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
