using StylizedWater3;
using Unity.Netcode;
using UnityEngine;
using WaveByWave.Player;

namespace WaveByWave.Ships
{
    [DefaultExecutionOrder(-100)]
    [RequireComponent(typeof(NetworkObject), typeof(Rigidbody), typeof(MovingPlatform))]
    public sealed class NetworkShipController : NetworkBehaviour
    {
        public const ulong NoHelmsman = ulong.MaxValue;

        [SerializeField] private Rigidbody body;
        [SerializeField, Min(0f)] private float maximumSpeed = 7f;
        [SerializeField, Min(0f)] private float acceleration = 2.5f;
        [SerializeField, Min(0f)] private float coastingDeceleration = 0.55f;
        [SerializeField, Min(0f)] private float turnSpeed = 28f;
        [SerializeField] private AlignToWater waterAlignment;

        [Header("Hull collisions")]
        [Tooltip("Simple root collider used for predictive collision queries against rocks and other solid obstacles.")]
        [SerializeField] private BoxCollider collisionHull;
        [SerializeField] private LayerMask obstacleLayers = ~0;
        [SerializeField, Min(0.001f)] private float collisionSkin = 0.04f;
        [SerializeField, Range(0f, 1f)] private float collisionSpeedRetention = 0.2f;
        [SerializeField, Min(0f)] private float collisionBraking = 6f;
        [SerializeField, Min(0.02f)] private float contactHoldTime = 0.15f;
        [SerializeField, Min(0f)] private float collisionRockingResponse = 4f;
        [SerializeField, Min(0f)] private float collisionHeaveResponse = 0.1f;
        [SerializeField, Min(0.01f)] private float impactSpringFrequency = 1.4f;
        [SerializeField, Range(0f, 2f)] private float impactSpringDamping = 0.8f;
        [SerializeField, Min(0f)] private float maximumImpactTilt = 6f;
        [SerializeField, Min(0f)] private float maximumImpactHeave = 0.3f;

        [Header("Sailing")]
        [SerializeField] private NetworkWindController wind;
        [SerializeField] private ShipAnchor anchor;
        [SerializeField] private ShipSailControl sailControl;
        [SerializeField] private ShipMastControl mastControl;
        [SerializeField, Min(0f)] private float anchorBraking = 7f;
        [SerializeField, Min(0f)] private float sailAdjustmentSpeed = 0.4f;
        [SerializeField, Min(0f)] private float driftSpeed = 0.45f;
        [Tooltip("Initial sail state: 0 = fully furled, 1 = fully deployed.")]
        [SerializeField, Range(0f, 1f)] private float initialSailDeployment;

        [Header("Wind response")]
        [Tooltip("Forward-speed multiplier when sailing directly against the wind. Must remain above zero.")]
        [SerializeField, Range(0.05f, 1f)] private float headwindSpeedMultiplier = 0.35f;
        [SerializeField, Min(0.05f)] private float tailwindSpeedMultiplier = 1f;
        [Tooltip("A badly trimmed sail remains usable; rotating the mast toward the wind raises this to 1.")]
        [SerializeField, Range(0.05f, 1f)] private float minimumSailTrimEfficiency = 0.55f;
        [Tooltip("Turn-rate multiplier when wind pressure on the sail opposes the requested turn.")]
        [SerializeField, Range(0.05f, 1f)] private float opposingWindTurnMultiplier = 0.4f;
        [Tooltip("Turn-rate multiplier when wind pressure on the sail supports the requested turn.")]
        [SerializeField, Range(0.05f, 1.25f)] private float supportingWindTurnMultiplier = 0.9f;

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
        private float _currentSpeed;
        private float _currentTurnRate;
        private float _heading;
        private Vector3 _planarPosition;
        private Transform _planarMotionTarget;
        private MovingPlatform _movingPlatform;
        private Vector3 _blockingContactNormal;
        private float _contactTimeRemaining;
        private Vector2 _impactTilt;
        private Vector2 _impactTiltVelocity;
        private float _impactHeave;
        private float _impactHeaveVelocity;
        private readonly RaycastHit[] _hullCastHits = new RaycastHit[32];
        private readonly Collider[] _hullOverlapHits = new Collider[32];

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
            body.isKinematic = true;
            body.useGravity = false;
            body.interpolation = RigidbodyInterpolation.None;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            _heading = transform.eulerAngles.y;
        }

        public override void OnNetworkSpawn()
        {
            // This vessel is driven explicitly by AlignToWater, never by dynamic physics.
            // Reassert after all NGO spawn callbacks so no networking component can change it.
            body.isKinematic = true;
            body.useGravity = false;
            body.interpolation = RigidbodyInterpolation.None;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

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
                ResetCollisionResponse();
                CreatePlanarMotionTarget();
                NetworkManager.OnClientDisconnectCallback += OnClientDisconnected;
            }

            // Every ship instance participates in the same KCC mover pipeline: the server feeds
            // its water-aligned authoritative target, while clients feed NGO's interpolated pose.
            _movingPlatform?.EnableKccMover();
        }

        public override void OnNetworkDespawn()
        {
            if (NetworkManager != null)
                NetworkManager.OnClientDisconnectCallback -= OnClientDisconnected;

            if (waterAlignment != null)
                waterAlignment.externalMotionControl = false;
            _movingPlatform?.DisableKccMover();
            DestroyPlanarMotionTarget();
        }

        private void FixedUpdate()
        {
            if (!IsSpawned || !IsServer)
                return;

            _movingPlatform?.RestoreKccSimulationPose();

            var deltaTime = Time.fixedDeltaTime;
            AdvanceCollisionResponse(deltaTime);
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
            var currentRight = Quaternion.Euler(0f, _heading, 0f) * Vector3.right;
            var courseToWind = Mathf.Clamp(Vector3.Dot(currentForward, windDirection), -1f, 1f);
            var downwindFactor = (courseToWind + 1f) * 0.5f;
            var courseSpeedMultiplier = Mathf.Lerp(
                headwindSpeedMultiplier,
                Mathf.Max(headwindSpeedMultiplier, tailwindSpeedMultiplier),
                downwindFactor);
            var sailCapture = sailControl != null
                ? sailControl.GetWindCapture(windDirection)
                : Mathf.Abs(Vector3.Dot(windDirection, currentForward));
            var sailTrimEfficiency = Mathf.Lerp(minimumSailTrimEfficiency, 1f, sailCapture);
            var targetSpeed = _anchorLowered.Value
                ? 0f
                : maximumSpeed * _sailDeployment.Value * courseSpeedMultiplier * sailTrimEfficiency *
                  (1f - Mathf.Abs(rudder) * fullRudderSpeedPenalty);
            var speedChangeRate = _anchorLowered.Value
                ? anchorBraking
                : targetSpeed >= _currentSpeed ? acceleration : coastingDeceleration;
            _currentSpeed = Mathf.MoveTowards(_currentSpeed, targetSpeed,
                speedChangeRate * deltaTime);

            var sailInfluence = Mathf.Lerp(0.35f, 1f, _sailDeployment.Value);
            var driftSpeedNow = _anchorLowered.Value ? 0f : driftSpeed * sailInfluence;
            var steerageSpeed = Mathf.Abs(_currentSpeed) + driftSpeedNow;
            var flowBasedRudderAuthority = Mathf.Lerp(minimumRudderAuthority, 1f,
                Mathf.Clamp01(steerageSpeed / Mathf.Max(0.1f, fullRudderAuthoritySpeed)));

            // With furled sails the helm always has its configured base response. Deploying
            // canvas introduces wind resistance and gradually makes actual water flow matter.
            var rudderAuthority = _anchorLowered.Value
                ? 0f
                : Mathf.Lerp(1f, flowBasedRudderAuthority, _sailDeployment.Value);
            // Project the wind onto the actual sail face. This is independent of the raw mast
            // angle: only the side and strength of pressure on the oriented canvas matter.
            var windPressureOnSail = sailControl != null
                ? sailControl.GetWindPressure(windDirection)
                : currentForward * Vector3.Dot(windDirection, currentForward);
            var lateralWindPressure = Mathf.Clamp(Vector3.Dot(windPressureOnSail, currentRight), -1f, 1f);
            var requestedTurnDirection = Mathf.Sign(rudder);
            var windSupportForTurn = Mathf.Clamp(lateralWindPressure * requestedTurnDirection, -1f, 1f);
            var supportFactor = (windSupportForTurn + 1f) * 0.5f;
            var loadedSailTurnMultiplier = Mathf.Lerp(
                opposingWindTurnMultiplier,
                Mathf.Max(opposingWindTurnMultiplier, supportingWindTurnMultiplier),
                supportFactor);
            var capturedWindLoad = _sailDeployment.Value * sailCapture;
            var sailResistanceMultiplier = Mathf.Lerp(1f, loadedSailTurnMultiplier, capturedWindLoad);
            var targetTurnRate = rudder * turnSpeed * rudderAuthority * sailResistanceMultiplier;
            var yawAcceleration = Mathf.Abs(targetTurnRate) > Mathf.Abs(_currentTurnRate)
                ? turnAcceleration
                : turnDeceleration;
            _currentTurnRate = Mathf.MoveTowards(_currentTurnRate, targetTurnRate, yawAcceleration * deltaTime);
            var previousHeading = _heading;
            var totalTurnRate = _currentTurnRate;
            _heading = Mathf.Repeat(_heading + totalTurnRate * deltaTime, 360f);
            var rotation = Quaternion.Euler(0f, _heading, 0f);
            var driftVelocity = _anchorLowered.Value
                ? Vector3.zero
                : windDirection * driftSpeedNow;

            if (_contactTimeRemaining > 0f)
            {
                var forwardIntoContact = Mathf.Max(
                    0f,
                    -Vector3.Dot(rotation * Vector3.forward, _blockingContactNormal));
                var blockingFactor = Mathf.InverseLerp(0.15f, 1f, forwardIntoContact);
                if (blockingFactor > 0f)
                {
                    _currentSpeed = Mathf.MoveTowards(
                        _currentSpeed,
                        0f,
                        collisionBraking * blockingFactor * deltaTime);
                }
            }

            var sailingVelocity = rotation * Vector3.forward * _currentSpeed + driftVelocity;
            var requestedPlanarVelocity = sailingVelocity;
            if (_contactTimeRemaining > 0f)
            {
                var inwardVelocity = Vector3.Dot(requestedPlanarVelocity, _blockingContactNormal);
                if (inwardVelocity < 0f)
                    requestedPlanarVelocity -= _blockingContactNormal * inwardVelocity;
            }
            var requestedDisplacement = requestedPlanarVelocity * deltaTime;
            var resolvedMotion = ResolveHullMotion(
                requestedDisplacement,
                previousHeading,
                ref _heading);
            var resolvedDisplacement = resolvedMotion.Displacement;
            var collision = resolvedMotion.Collision;
            _planarPosition += resolvedDisplacement;

            if (collision.HasValue)
            {
                ApplyCollisionImpact(
                    collision.Value,
                    requestedPlanarVelocity,
                    rotation);
                rotation = Quaternion.Euler(0f, _heading, 0f);
            }

            totalTurnRate = deltaTime > 0f
                ? Mathf.DeltaAngle(previousHeading, _heading) / deltaTime
                : 0f;

            var planarVelocity = deltaTime > 0f
                ? resolvedDisplacement / deltaTime
                : Vector3.zero;
            planarVelocity = Vector3.ClampMagnitude(
                planarVelocity,
                Mathf.Max(maximumSpeed + driftSpeed, 0.1f) * 1.5f);

            if (waterAlignment != null)
            {
                waterAlignment.rotation = _heading;
                if (_planarMotionTarget != null)
                {
                    _planarMotionTarget.position = _planarPosition;
                    waterAlignment.followTarget = _planarMotionTarget;
                }
            }

            _replicatedSpeed.Value = _currentSpeed;
            _replicatedTurnRate.Value = totalTurnRate;
            _replicatedPlanarVelocity.Value = planarVelocity;
        }

        private ResolvedHullMotion ResolveHullMotion(
            Vector3 requestedDisplacement,
            float previousHeading,
            ref float requestedHeading)
        {
            requestedDisplacement.y = 0f;
            if (collisionHull == null || !collisionHull.enabled || collisionHull.isTrigger)
                return new ResolvedHullMotion(requestedDisplacement, null);

            HullCollision? collision = null;
            var resolvedDisplacement = Vector3.zero;
            if (TryCastHull(
                    body.position,
                    body.rotation,
                    requestedDisplacement,
                    out var firstCollision,
                    out var allowedDistance))
            {
                collision = firstCollision;
                var requestedDirection = requestedDisplacement.normalized;
                var travel = requestedDirection * allowedDistance;
                resolvedDisplacement = travel;

                // Only the velocity component pointing into the obstacle is blocked. The
                // remaining tangent is swept once more, so touching a wall does not slow
                // down travel parallel to it and cannot create alternating push-out frames.
                var remaining = requestedDisplacement - travel;
                var slide = Vector3.ProjectOnPlane(remaining, firstCollision.Normal);
                slide.y = 0f;
                if (slide.sqrMagnitude > 0.0000001f)
                {
                    var slideStart = body.position + travel;
                    if (TryCastHull(
                            slideStart,
                            body.rotation,
                            slide,
                            out _,
                            out var allowedSlideDistance))
                    {
                        resolvedDisplacement += slide.normalized * allowedSlideDistance;
                    }
                    else
                    {
                        resolvedDisplacement += slide;
                    }
                }
            }
            else
            {
                resolvedDisplacement = requestedDisplacement;
            }

            // Box casts do not account for the small yaw rotation made during this tick.
            // Cancel only the offending yaw instead of depenetrating the hull. Repeated
            // positional push-out was the source of the visible tapping at rest.
            var yawDelta = Mathf.DeltaAngle(previousHeading, requestedHeading);
            if (Mathf.Abs(yawDelta) > 0.0001f)
            {
                var candidatePosition = body.position + resolvedDisplacement;
                var candidateRotation = Quaternion.AngleAxis(yawDelta, Vector3.up) * body.rotation;
                if (TryGetHullOverlap(candidatePosition, candidateRotation, out var rotationCollision))
                {
                    requestedHeading = previousHeading;
                    collision ??= rotationCollision;
                }
            }

            return new ResolvedHullMotion(resolvedDisplacement, collision);
        }

        private bool TryCastHull(
            Vector3 rootPosition,
            Quaternion rootRotation,
            Vector3 displacement,
            out HullCollision collision,
            out float allowedDistance)
        {
            collision = default;
            allowedDistance = displacement.magnitude;
            if (allowedDistance <= 0.00001f)
                return false;

            GetHullWorldBox(
                rootPosition,
                rootRotation,
                out var center,
                out var halfExtents,
                out var orientation);
            var direction = displacement / allowedDistance;
            var hitCount = Physics.BoxCastNonAlloc(
                center,
                halfExtents,
                direction,
                _hullCastHits,
                orientation,
                allowedDistance + collisionSkin,
                obstacleLayers,
                QueryTriggerInteraction.Ignore);

            var nearestDistance = float.PositiveInfinity;
            for (var i = 0; i < hitCount; i++)
            {
                var hit = _hullCastHits[i];
                if (!IsBlockingObstacle(hit.collider) ||
                    Vector3.Dot(direction, hit.normal) >= -0.001f ||
                    hit.distance >= nearestDistance)
                    continue;

                nearestDistance = hit.distance;
                collision = new HullCollision(hit.normal, hit.point);
            }

            if (float.IsPositiveInfinity(nearestDistance))
                return false;

            allowedDistance = Mathf.Min(
                allowedDistance,
                Mathf.Max(0f, nearestDistance - collisionSkin));
            return true;
        }

        private bool TryGetHullOverlap(
            Vector3 rootPosition,
            Quaternion rootRotation,
            out HullCollision collision)
        {
            collision = default;
            GetHullWorldBox(
                rootPosition,
                rootRotation,
                out var center,
                out var halfExtents,
                out var orientation);
            var overlapCount = Physics.OverlapBoxNonAlloc(
                center,
                halfExtents,
                _hullOverlapHits,
                orientation,
                obstacleLayers,
                QueryTriggerInteraction.Ignore);

            for (var i = 0; i < overlapCount; i++)
            {
                var obstacle = _hullOverlapHits[i];
                if (!IsBlockingObstacle(obstacle) ||
                    !Physics.ComputePenetration(
                        collisionHull,
                        rootPosition,
                        rootRotation,
                        obstacle,
                        obstacle.transform.position,
                        obstacle.transform.rotation,
                        out var separationDirection,
                        out var separationDistance) ||
                    separationDistance <= 0.0001f)
                    continue;

                var planarNormal = Vector3.ProjectOnPlane(separationDirection, Vector3.up);
                if (planarNormal.sqrMagnitude < 0.0001f)
                    continue;

                planarNormal.Normalize();
                collision = new HullCollision(planarNormal, obstacle.ClosestPoint(center));
                return true;
            }

            return false;
        }

        private void ApplyCollisionImpact(
            HullCollision collision,
            Vector3 requestedVelocity,
            Quaternion sailingRotation)
        {
            var normal = Vector3.ProjectOnPlane(collision.Normal, Vector3.up);
            if (normal.sqrMagnitude < 0.0001f)
                return;
            normal.Normalize();

            _blockingContactNormal = normal;
            _contactTimeRemaining = contactHoldTime;

            var impactSpeed = Mathf.Max(0f, -Vector3.Dot(requestedVelocity, normal));
            if (impactSpeed <= 0.01f)
                return;

            var forwardIntoContact = Mathf.Max(
                0f,
                -Vector3.Dot(sailingRotation * Vector3.forward, normal));
            var blockingFactor = Mathf.InverseLerp(0.15f, 1f, forwardIntoContact);
            _currentSpeed *= Mathf.Lerp(1f, collisionSpeedRetention, blockingFactor);

            // A light graze should be silent and must not restart the rocking spring.
            if (impactSpeed < 0.25f)
                return;

            var localContact = Quaternion.Inverse(sailingRotation) *
                               (collision.Point - body.worldCenterOfMass);
            var halfSize = collisionHull != null ? collisionHull.size * 0.5f : Vector3.one;
            var normalizedSide = Mathf.Clamp(localContact.x / Mathf.Max(halfSize.x, 0.1f), -1f, 1f);
            var normalizedBow = Mathf.Clamp(localContact.z / Mathf.Max(halfSize.z, 0.1f), -1f, 1f);

            _impactTiltVelocity.x += normalizedBow * impactSpeed * collisionRockingResponse;
            _impactTiltVelocity.y -= normalizedSide * impactSpeed * collisionRockingResponse;
            _impactHeaveVelocity -= impactSpeed * collisionHeaveResponse;
        }

        private void AdvanceCollisionResponse(float deltaTime)
        {
            _contactTimeRemaining = Mathf.Max(0f, _contactTimeRemaining - deltaTime);
            if (_contactTimeRemaining <= 0f)
                _blockingContactNormal = Vector3.zero;

            AdvanceDampedSpring(
                ref _impactTilt.x,
                ref _impactTiltVelocity.x,
                impactSpringFrequency,
                impactSpringDamping,
                deltaTime);
            AdvanceDampedSpring(
                ref _impactTilt.y,
                ref _impactTiltVelocity.y,
                impactSpringFrequency,
                impactSpringDamping,
                deltaTime);
            AdvanceDampedSpring(
                ref _impactHeave,
                ref _impactHeaveVelocity,
                impactSpringFrequency,
                impactSpringDamping,
                deltaTime);

            _impactTilt = Vector2.ClampMagnitude(_impactTilt, maximumImpactTilt);
            _impactHeave = Mathf.Clamp(_impactHeave, -maximumImpactHeave, maximumImpactHeave);
        }

        private static void AdvanceDampedSpring(
            ref float displacement,
            ref float velocity,
            float frequency,
            float damping,
            float deltaTime)
        {
            var angularFrequency = Mathf.PI * 2f * frequency;
            var acceleration = -angularFrequency * angularFrequency * displacement -
                               2f * damping * angularFrequency * velocity;
            velocity += acceleration * deltaTime;
            displacement += velocity * deltaTime;
        }

        private void GetHullWorldBox(
            Vector3 rootPosition,
            Quaternion rootRotation,
            out Vector3 center,
            out Vector3 halfExtents,
            out Quaternion orientation)
        {
            var scale = transform.lossyScale;
            halfExtents = Vector3.Scale(
                collisionHull.size * 0.5f,
                new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
            halfExtents = Vector3.Max(halfExtents - Vector3.one * collisionSkin, Vector3.one * 0.001f);
            center = rootPosition + rootRotation * Vector3.Scale(collisionHull.center, scale);
            orientation = rootRotation;
        }

        private bool IsBlockingObstacle(Collider candidate)
        {
            if (candidate == null || candidate.isTrigger || candidate == collisionHull ||
                candidate.attachedRigidbody == body)
                return false;

            // Players and loose dynamic items are handled by their own movement/physics and
            // must never be able to stop the authoritative ship sweep.
            if (candidate.GetComponentInParent<NetworkPlayerController>() != null)
                return false;
            var otherBody = candidate.attachedRigidbody;
            return otherBody == null || otherBody.isKinematic;
        }

        private void ResetCollisionResponse()
        {
            _blockingContactNormal = Vector3.zero;
            _contactTimeRemaining = 0f;
            _impactTilt = Vector2.zero;
            _impactTiltVelocity = Vector2.zero;
            _impactHeave = 0f;
            _impactHeaveVelocity = 0f;
        }

        private readonly struct HullCollision
        {
            public readonly Vector3 Normal;
            public readonly Vector3 Point;

            public HullCollision(Vector3 normal, Vector3 point)
            {
                Normal = normal;
                Point = point;
            }
        }

        private readonly struct ResolvedHullMotion
        {
            public readonly Vector3 Displacement;
            public readonly HullCollision? Collision;

            public ResolvedHullMotion(Vector3 displacement, HullCollision? collision)
            {
                Displacement = displacement;
                Collision = collision;
            }
        }

        public bool TryGetKccMoverTarget(out Vector3 position, out Quaternion rotation)
        {
            if (IsSpawned && IsServer && waterAlignment != null &&
                waterAlignment.HasExternalMotionTarget)
            {
                position = waterAlignment.ExternalMotionTargetPosition;
                position += Vector3.up * _impactHeave;
                rotation = waterAlignment.ExternalMotionTargetRotation *
                           Quaternion.Euler(_impactTilt.x, 0f, _impactTilt.y);
                return true;
            }

            position = default;
            rotation = Quaternion.identity;
            return false;
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
