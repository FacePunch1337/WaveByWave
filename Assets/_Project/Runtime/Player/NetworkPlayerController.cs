using System.Collections;
using System.Collections.Generic;
using KinematicCharacterController;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using WaveByWave.Combat;
using WaveByWave.Customization;
using WaveByWave.Items;
using WaveByWave.Networking;
using WaveByWave.Ships;
using WaveByWave.UI;

namespace WaveByWave.Player
{
    [DefaultExecutionOrder(9000)]
    [RequireComponent(typeof(Rigidbody), typeof(CapsuleCollider), typeof(NetworkObject))]
    [RequireComponent(typeof(OwnerNetworkTransform), typeof(NetworkRigidbody))]
    [RequireComponent(typeof(KinematicCharacterMotor))]
    public sealed class NetworkPlayerController : NetworkBehaviour, ICharacterController
    {
        public const string LocalBodyLayerName = "LocalPlayerBody";
        private const float GroundCastStartOffset = 0.08f;

        [Header("Movement")]
        [SerializeField, Min(0f)] private float moveSpeed = 5f;
        [SerializeField, Min(1f)] private float sprintMultiplier = 1.65f;
        [SerializeField, Min(0f)] private float jumpHeight = 1.35f;
        [SerializeField] private float gravity = -24f;
        [SerializeField, Min(0f)] private float groundAcceleration = 55f;
        [SerializeField, Min(0f)] private float airAcceleration = 12f;
        [SerializeField, Range(1f, 89f)] private float maximumSlopeAngle = 50f;
        [SerializeField, Min(0f)] private float groundStickSpeed = 2f;

        [Header("Moving platforms")]
        [SerializeField, Min(0.02f)] private float platformProbeDistance = 0.22f;
        [Tooltip("Maximum gap at which a client may attach to a network-interpolated deck.")]
        [SerializeField, Min(0.005f)] private float clientPlatformContactTolerance = 0.04f;
        [SerializeField, Min(0f)] private float platformContactGrace = 0.15f;
        [SerializeField, Min(0f)] private float platformAnchorSharpness = 40f;
        [SerializeField, Min(0f)] private float maximumPlatformCorrectionSpeed = 8f;
        [Tooltip("Local-space smoothing used only when displaying another player on a moving platform.")]
        [SerializeField, Min(1f)] private float remotePlatformPositionSharpness = 24f;
        [SerializeField, Min(1f)] private float remotePlatformRotationSharpness = 28f;

        [Header("Interaction")]
        [SerializeField, Min(0.5f)] private float interactionDistance = 3f;
        [SerializeField] private LayerMask interactionMask = ~0;
        [Tooltip("Nearby fallback reach when the crosshair has no interaction target. Raycast targets always take priority.")]
        [SerializeField, Min(0f)] private float interactionProximityDistance = 1.25f;
        [Tooltip("Time to ease into an anchor handle, in the rotating handle's local frame.")]
        [SerializeField, Min(0f)] private float anchorHandleApproachDuration = 0.4f;

        [Header("References")]
        [SerializeField] private Rigidbody body;
        [SerializeField] private CapsuleCollider bodyCollider;
        [SerializeField] private KinematicCharacterMotor _kccMotor;
        [SerializeField] private PlayerAnimationSync animationSync;
        [SerializeField] private PlayerInventory inventory;
        [SerializeField] private Transform cameraTarget;
        [SerializeField] private FirstPersonCamera ownerCamera;
        [SerializeField] private Transform firstPersonHiddenRoot;

        private FirstPersonCamera _camera;
        private Transform _presentationRoot;
        private readonly List<(Transform Transform, int Layer)> _hiddenLayerRestore = new();
        private Vector2 _moveInput;
        private bool _sprintHeld;
        private bool _jumpQueued;
        private bool _isGrounded;
        private Vector3 _groundNormal = Vector3.up;
        private Quaternion _desiredBodyRotation = Quaternion.identity;
        private Vector3 _airbornePlatformMomentum;
        private float _ignoreGroundUntil;
        private MovingPlatform _platform;
        private bool _airborneFromPlatform;
        private Vector3 _platformLocalAnchor;
        private bool _platformAnchorLocked;
        private float _lastPlatformContactTime;
        private readonly RaycastHit[] _groundHits = new RaycastHit[12];
        private readonly RaycastHit[] _interactionHits = new RaycastHit[64];
        private readonly Collider[] _interactionNeighbours = new Collider[32];
        private bool _interactionInputBlockedThisFrame = true;
        private ShipHelm _activeHelm;
        private ShipSailControl _activeSailControl;
        private ShipMastControl _activeMastControl;
        private ShipAnchor _activeAnchor;
        private ShipCannon _activeCannon;
        private CustomizationStation _activeCustomizationStation;
        private CustomizationMenu _customizationMenu;
        private ShipCannon _pendingCannon;
        private bool _cannonControlActive;
        private float _cannonRequestDeadline;
        private float _nextCannonAimSend;
        private bool _cannonOccupationConfirmed;
        private float _cannonAssignmentDeadline;
        private Transform _anchorApproachStation;
        private Vector3 _anchorApproachPositionOffset;
        private Quaternion _anchorApproachRotationOffset;
        private float _anchorApproachStarted;
        private ShipAnchor _pendingAnchor;
        private ShipAnchor _lookedAtAnchor;
        private ShipAnchor _loweringAnchor;
        private int _anchorHandleIndex = -1;
        private bool _anchorOccupationConfirmed;
        private float _anchorAssignmentDeadline;
        private float _nextAnchorSend;
        private bool _lastAnchorPush;
        private float _anchorLowerHoldStarted;
        private float _nextAnchorLowerHeartbeat;
        private bool _anchorLowerCompleteSent;
        private float _nextHelmSend;
        private float _nextSailSend;
        private float _nextMastSend;
        private bool _menuWasOpen;
        private bool _sceneTransitioning;
        private int _placementRevision;
        private OwnerNetworkTransform _networkTransform;
        private NetworkObject _remotePlatformObject;
        private Vector3 _remotePlatformLocalPosition;
        private Quaternion _remotePlatformLocalRotation = Quaternion.identity;
        private bool _remotePlatformPoseInitialized;
        private Vector3 _remoteWorldPresentationOffset;
        private Quaternion _remoteWorldPresentationRotationOffset = Quaternion.identity;
        private bool _remoteWorldPresentationActive;
        private Quaternion _clientPlatformPoseRotation = Quaternion.identity;
        private Matrix4x4 _clientPlatformWorldToLocal = Matrix4x4.identity;
        private Vector3 _clientPlatformPreviousLocalPosition;
        private Vector3 _clientPlatformCurrentLocalPosition;
        private Quaternion _clientPlatformPreviousLocalRotation = Quaternion.identity;
        private Quaternion _clientPlatformCurrentLocalRotation = Quaternion.identity;
        private bool _clientPlatformPoseInitialized;
        private MovingPlatform _kccPresentationPlatform;
        private bool _usingClientPlatformRelativeVelocity;
        private bool _ownerPlatformPresentationActive;
        private Vector3 _ownerWorldPresentationOffset;
        private Quaternion _ownerWorldPresentationRotationOffset = Quaternion.identity;
        private PhysicsMaterial _motorPhysicsMaterial;
        private bool _useKccMotor;
        private NetworkHealth _health;

        // The ordinary OwnerNetworkTransform remains responsible for movement replication.
        // This small parallel state keeps presentation relative to the supporting platform,
        // preventing two independently interpolated world-space transforms from drifting apart.
        // The physics root deliberately remains unparented. Platform-relative presentation is
        // applied to a visual child, while the Rigidbody keeps one coherent world-space state.
        private readonly NetworkVariable<bool> _hasReplicatedPlatform = new(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private readonly NetworkVariable<NetworkObjectReference> _replicatedPlatform = new(
            default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private readonly NetworkVariable<Vector3> _replicatedPlatformLocalPosition = new(
            default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private readonly NetworkVariable<Quaternion> _replicatedPlatformLocalRotation = new(
            Quaternion.identity, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        public PlayerInventory Inventory => inventory;
        public bool IsAtHelm => _activeHelm != null;
        public bool IsAtSailControl => _activeSailControl != null;
        public bool IsAtMastControl => _activeMastControl != null;
        public bool IsAtAnchor => _activeAnchor != null;
        public bool IsAtCannon => _activeCannon != null;
        public ShipCannon ActiveCannon => _activeCannon;
        public CustomizationStation ActiveCustomizationStation => _activeCustomizationStation;
        public bool IsAtControlStation => IsAtHelm || IsAtSailControl || IsAtMastControl || IsAtAnchor ||
            IsAtCannon || _activeCustomizationStation != null;

        public ShipAnchor AnchorBeingReleased => _loweringAnchor;
        public Transform OwnerView => _camera != null ? _camera.transform : null;
        public float AnchorReleaseHoldProgress => _loweringAnchor != null
            ? Mathf.Clamp01((Time.unscaledTime - _anchorLowerHoldStarted) / _loweringAnchor.LowerHoldDuration)
            : 0f;

        public void SetDesiredBodyRotation(Quaternion rotation)
        {
            if (!IsOwner || IsAtControlStation)
                return;

            _desiredBodyRotation = rotation;
        }

        private void Awake()
        {
            body ??= GetComponent<Rigidbody>();
            bodyCollider ??= GetComponent<CapsuleCollider>();
            _kccMotor ??= GetComponent<KinematicCharacterMotor>();
            animationSync ??= GetComponent<PlayerAnimationSync>();
            inventory ??= GetComponent<PlayerInventory>();
            _health = GetComponent<NetworkHealth>();
            _networkTransform = GetComponent<OwnerNetworkTransform>();
            cameraTarget ??= transform;
            firstPersonHiddenRoot ??= transform.Find("Visual");
            EnsurePresentationRoot();

            body.useGravity = false;
            body.isKinematic = true;
            body.interpolation = RigidbodyInterpolation.None;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            body.constraints = RigidbodyConstraints.FreezeRotation;
            if (_kccMotor != null)
            {
                _kccMotor.CharacterController = null;
                _kccMotor.enabled = false;
            }
            else
                Debug.LogError("Player prefab is missing KinematicCharacterMotor.", this);
            ConfigureMotorPhysicsMaterial();
            _desiredBodyRotation = transform.rotation;

            _camera = ownerCamera;
            if (ownerCamera != null)
                ownerCamera.gameObject.SetActive(false);
        }

        private void ConfigureMotorPhysicsMaterial()
        {
            if (bodyCollider == null)
                return;

            // A velocity-driven Rigidbody must not inherit deck/slope friction. Otherwise the
            // contact solver can cancel horizontal motion while the capsule is held grounded.
            _motorPhysicsMaterial = new PhysicsMaterial("Player Motor (Runtime)")
            {
                hideFlags = HideFlags.HideAndDontSave,
                dynamicFriction = 0f,
                staticFriction = 0f,
                frictionCombine = PhysicsMaterialCombine.Minimum,
                bounciness = 0f,
                bounceCombine = PhysicsMaterialCombine.Minimum
            };
            bodyCollider.material = _motorPhysicsMaterial;
        }

        public override void OnDestroy()
        {
            if (_motorPhysicsMaterial != null)
                Destroy(_motorPhysicsMaterial);
            base.OnDestroy();
        }

        private void EnsurePresentationRoot()
        {
            _presentationRoot = transform.Find("Presentation Root");
            if (_presentationRoot == null)
            {
                _presentationRoot = new GameObject("Presentation Root").transform;
                _presentationRoot.SetParent(transform, false);
            }

            if (cameraTarget != null && cameraTarget != transform && cameraTarget.parent == transform)
                cameraTarget.SetParent(_presentationRoot, false);
            if (firstPersonHiddenRoot != null && firstPersonHiddenRoot != transform &&
                firstPersonHiddenRoot.parent == transform)
                firstPersonHiddenRoot.SetParent(_presentationRoot, false);
        }

        public override void OnNetworkSpawn()
        {
            // Every player is simulated by KCC on the machine that owns it. Network copies are
            // presentation-only and receive that owner's transform through OwnerNetworkTransform.
            if (_kccMotor != null)
            {
                _kccMotor.CharacterController = null;
                _kccMotor.enabled = false;
            }
            bodyCollider.enabled = IsOwner;
            body.isKinematic = true;
            body.interpolation = RigidbodyInterpolation.None;

            if (!IsOwner)
            {
                if (ownerCamera != null)
                    ownerCamera.gameObject.SetActive(false);
                return;
            }

            ActivateOwnerCamera();
            EnableKccMotor();
            var anchorProgress = GetComponent<AnchorHoldProgress>();
            if (anchorProgress == null)
                anchorProgress = gameObject.AddComponent<AnchorHoldProgress>();
            anchorProgress.Initialize(this);
            var cannonProgress = GetComponent<CannonReloadProgress>();
            if (cannonProgress == null)
                cannonProgress = gameObject.AddComponent<CannonReloadProgress>();
            cannonProgress.Initialize(this);
            gameObject.name = $"Player_{OwnerClientId}";
        }

        private void ActivateOwnerCamera()
        {
            _camera = ownerCamera;
            if (_camera == null)
            {
                Debug.LogError("Player prefab is missing its Camera Holder/FirstPersonCamera.", this);
                return;
            }

            _camera.gameObject.SetActive(true);
            _camera.SetTarget(cameraTarget, transform);
            HideOwnerBodyFromCamera();
        }

        private void HideOwnerBodyFromCamera()
        {
            var hiddenLayer = LayerMask.NameToLayer(LocalBodyLayerName);
            if (hiddenLayer < 0)
            {
                Debug.LogError($"Required layer '{LocalBodyLayerName}' is missing.", this);
                return;
            }

            var ownerView = _camera != null ? _camera.GetComponent<Camera>() : null;
            if (ownerView != null)
                ownerView.cullingMask &= ~(1 << hiddenLayer);

            RestoreOwnerBodyLayers();
            if (firstPersonHiddenRoot == null)
                return;

            foreach (var child in firstPersonHiddenRoot.GetComponentsInChildren<Transform>(true))
            {
                _hiddenLayerRestore.Add((child, child.gameObject.layer));
                child.gameObject.layer = hiddenLayer;
            }
        }

        private void RestoreOwnerBodyLayers()
        {
            foreach (var entry in _hiddenLayerRestore)
            {
                if (entry.Transform != null)
                    entry.Transform.gameObject.layer = entry.Layer;
            }
            _hiddenLayerRestore.Clear();
        }

        private void Update()
        {
            _interactionInputBlockedThisFrame = true;
            if (!IsOwner || !IsSpawned)
                return;

            if (_health != null && _health.IsDead)
            {
                ClearMovementInput();
                return;
            }

            if (Keyboard.current == null)
            {
                CancelAnchorLowerHold();
                return;
            }

            if (_sceneTransitioning)
                return;

            if (_activeCustomizationStation != null)
            {
                UpdateCustomization();
                return;
            }

            if (_anchorHandleIndex >= 0 && (_activeAnchor == null ||
                _activeAnchor.Ship == null || !_activeAnchor.Ship.IsSpawned))
                LeaveAnchorHandle(false);
            if (_cannonControlActive && (_activeCannon == null || _activeCannon.Battery == null || !_activeCannon.Battery.IsSpawned))
                LeaveCannon(false);
            if (_pendingCannon != null && (Time.unscaledTime >= _cannonRequestDeadline ||
                _pendingCannon.Battery == null || !_pendingCannon.Battery.IsSpawned))
            {
                if (_pendingCannon.Battery != null && _pendingCannon.Battery.IsSpawned)
                    _pendingCannon.Battery.ReleaseCannonServerRpc();
                _pendingCannon = null;
            }

            if (PlayerEquipment.InputCaptured)
            {
                if (!_menuWasOpen)
                    StopShipControlInputsForMenu();
                _menuWasOpen = true;
                MaintainPlatformAttachmentWhileMenuIsOpen();
                return;
            }

            if (_menuWasOpen)
            {
                _menuWasOpen = false;
                ResyncRigidbodyAfterMenu();
                // Consume the frame that closed the menu so Escape cannot also leave the helm.
                return;
            }

            if (_activeHelm != null)
            {
                UpdateHelmInput();
                return;
            }

            if (_activeSailControl != null)
            {
                UpdateSailControlInput();
                return;
            }

            if (_activeMastControl != null)
            {
                UpdateMastControlInput();
                return;
            }

            if (_activeAnchor != null)
            {
                UpdateAnchorInput();
                return;
            }

            if (_activeCannon != null)
            {
                UpdateCannonInput();
                return;
            }

            CaptureMovementInput();
            _interactionInputBlockedThisFrame = false;
            if (!IsAtControlStation)
                UpdateItemActions();
        }

        private void StopShipControlInputsForMenu()
        {
            ClearMovementInput();
            CancelAnchorLowerHold();
            if (_activeAnchor != null && _activeAnchor.Ship.IsSpawned)
                _activeAnchor.Ship.SubmitAnchorPushServerRpc(false);
            _lastAnchorPush = false;
            if (_activeHelm != null)
                _activeHelm.Ship.SubmitHelmInputServerRpc(0f);
            if (_activeSailControl != null)
                _activeSailControl.Ship.SubmitSailInputServerRpc(0f);
            if (_activeMastControl != null)
                _activeMastControl.Ship.SubmitMastInputServerRpc(0f);
        }

        private void MaintainPlatformAttachmentWhileMenuIsOpen()
        {
            if (_platform != null)
                SnapToActiveControlStation();
            PublishPlatformPose();
            _camera?.RefreshPose();
        }

        private void ResyncRigidbodyAfterMenu()
        {
            if (body == null || body.isKinematic)
                return;

            body.WakeUp();
            if (_platform != null && !_airborneFromPlatform)
            {
                _platformLocalAnchor = _platform.transform.InverseTransformPoint(body.position);
                _platformAnchorLocked = true;
            }
        }

        private void CaptureMovementInput()
        {
            var keyboard = Keyboard.current;
            _moveInput = Vector2.ClampMagnitude(new Vector2(
                (keyboard.dKey.isPressed ? 1f : 0f) - (keyboard.aKey.isPressed ? 1f : 0f),
                (keyboard.wKey.isPressed ? 1f : 0f) - (keyboard.sKey.isPressed ? 1f : 0f)), 1f);
            _sprintHeld = keyboard.leftShiftKey.isPressed;
            _jumpQueued |= keyboard.spaceKey.wasPressedThisFrame;
        }

        private void ClearMovementInput()
        {
            _moveInput = Vector2.zero;
            _sprintHeld = false;
            _jumpQueued = false;
        }

        private void EnableKccMotor()
        {
            if (!IsOwner || body == null || bodyCollider == null || _kccMotor == null)
                return;

            var capsuleRadius = bodyCollider.radius;
            var capsuleHeight = bodyCollider.height;
            var capsuleYOffset = bodyCollider.center.y;
            body.isKinematic = true;
            body.interpolation = RigidbodyInterpolation.None;
            body.angularVelocity = Vector3.zero;

            _useKccMotor = true;
            _kccMotor.CharacterController = this;
            _kccMotor.AttachedRigidbodyOverride = null;
            _kccMotor.SetCapsuleDimensions(
                capsuleRadius,
                capsuleHeight,
                capsuleYOffset);
            _kccMotor.GroundDetectionExtraDistance = 0f;
            _kccMotor.MaxStableSlopeAngle = maximumSlopeAngle;
            _kccMotor.StepHandling = StepHandlingMethod.Extra;
            _kccMotor.MaxStepHeight = 0.5f;
            _kccMotor.MaxStableDistanceFromLedge = capsuleRadius;
            _kccMotor.InteractiveRigidbodyHandling = true;
            _kccMotor.RigidbodyInteractionType = RigidbodyInteractionType.SimulatedDynamic;
            // NetworkPhysicsObject applies the same explicit server impulse for host and
            // remote owners. A zero interaction mass prevents KCC from adding a second,
            // host-only impulse while still treating the prop as a solid obstruction.
            _kccMotor.SimulatedCharacterMass = 0f;
            _kccMotor.PreserveAttachedRigidbodyMomentum = true;
            var initialForward = Vector3.ProjectOnPlane(
                transform.rotation * Vector3.forward, Vector3.up);
            if (initialForward.sqrMagnitude < 0.0001f)
                initialForward = Vector3.forward;
            _desiredBodyRotation = Quaternion.LookRotation(initialForward.normalized, Vector3.up);
            _kccMotor.SetPositionAndRotation(transform.position, _desiredBodyRotation);
            _kccMotor.BaseVelocity = Vector3.zero;
            _kccMotor.enabled = true;
            bodyCollider.enabled = true;

            // KCC interpolates the physics root itself. The old render-only correction from the
            // Rigidbody controller must not be applied a second time.
            ResetClientPlatformFrame();
            ResetPresentationPose();
        }

        public void BeforeCharacterUpdate(float deltaTime)
        {
        }

        public void UpdateRotation(ref Quaternion currentRotation, float deltaTime)
        {
            if (!_useKccMotor || !IsOwner || _sceneTransitioning || IsAtControlStation)
                return;

            currentRotation = _desiredBodyRotation;
        }

        public void UpdateVelocity(ref Vector3 currentVelocity, float deltaTime)
        {
            if (!_useKccMotor || !IsOwner || _sceneTransitioning || IsAtControlStation)
            {
                currentVelocity = Vector3.zero;
                _jumpQueued = false;
                return;
            }

            var characterUp = _kccMotor.CharacterUp;
            var speed = moveSpeed * (_sprintHeld ? sprintMultiplier : 1f);
            var inputDirection = _desiredBodyRotation *
                                 new Vector3(_moveInput.x, 0f, _moveInput.y);
            inputDirection = Vector3.ProjectOnPlane(inputDirection, characterUp);
            if (inputDirection.sqrMagnitude > 1f)
                inputDirection.Normalize();

            var stableOnGround = _kccMotor.GroundingStatus.IsStableOnGround;
            if (stableOnGround)
            {
                currentVelocity = _kccMotor.GetDirectionTangentToSurface(
                                      currentVelocity,
                                      _kccMotor.GroundingStatus.GroundNormal) *
                                  currentVelocity.magnitude;

                var inputRight = Vector3.Cross(inputDirection, characterUp);
                var desiredGroundVelocity = inputDirection.sqrMagnitude > 0.0001f
                    ? Vector3.Cross(_kccMotor.GroundingStatus.GroundNormal, inputRight).normalized *
                      inputDirection.magnitude * speed
                    : Vector3.zero;
                currentVelocity = Vector3.MoveTowards(
                    currentVelocity, desiredGroundVelocity, groundAcceleration * deltaTime);

                if (_jumpQueued)
                {
                    _airbornePlatformMomentum = Vector3.zero;
                    SetKccAirbornePlatformFrame(_platform);
                    _kccMotor.ForceUnground();
                    var jumpSpeed = Mathf.Sqrt(jumpHeight * -2f * gravity);
                    currentVelocity = Vector3.ProjectOnPlane(currentVelocity, characterUp) +
                                      characterUp * jumpSpeed;
                    _isGrounded = false;
                    if (_platform != null)
                    {
                        _airborneFromPlatform = true;
                        _lastPlatformContactTime = Time.fixedTime;
                    }
                }
            }
            else
            {
                var verticalVelocity = Vector3.Project(currentVelocity, characterUp);
                var horizontalVelocity = currentVelocity - verticalVelocity;
                if (inputDirection.sqrMagnitude > 0.0001f)
                {
                    var targetAirVelocity = Vector3.ProjectOnPlane(
                                                _airbornePlatformMomentum, characterUp) +
                                            inputDirection.normalized * speed;
                    horizontalVelocity = Vector3.MoveTowards(
                        horizontalVelocity, targetAirVelocity, airAcceleration * deltaTime);
                }

                currentVelocity = horizontalVelocity + verticalVelocity +
                                  characterUp * (gravity * deltaTime);
            }

            _jumpQueued = false;
            animationSync.SetLocomotion(
                _moveInput.magnitude * (speed / Mathf.Max(0.01f, moveSpeed)),
                _isGrounded,
                Vector3.Dot(currentVelocity, characterUp));
        }

        public void ApplyDamageKnockback(Vector3 velocityChange)
        {
            if (!IsOwner || !_useKccMotor || _kccMotor == null || _health != null && _health.IsDead)
                return;
            if (velocityChange.sqrMagnitude < 0.0001f)
                return;
            _kccMotor.ForceUnground();
            _kccMotor.BaseVelocity += velocityChange;
            _isGrounded = false;
            _platform = null;
            _airborneFromPlatform = true;
        }

        public void SetDamageAliveState(bool alive)
        {
            if (!IsOwner || !IsSpawned)
                return;
            ClearMovementInput();
            if (!alive)
            {
                ExitCustomization();
                StopShipControlInputsForMenu();
                ResetAnchorInteraction();
                _activeHelm = null;
                _activeSailControl = null;
                _activeMastControl = null;
                _platform = null;
                _airborneFromPlatform = false;
                SetOwnerPhysicsSimulation(false);
                ResetPresentationPose();
            }
            else if (!_sceneTransitioning)
                SetOwnerPhysicsSimulation(true);
        }

        public void PostGroundingUpdate(float deltaTime)
        {
            if (!_useKccMotor)
                return;

            var wasGrounded = _isGrounded;
            var previousPlatform = _platform;
            _isGrounded = _kccMotor.GroundingStatus.IsStableOnGround;

            MovingPlatform supportingPlatform = null;
            if (_isGrounded && _kccMotor.GroundingStatus.GroundCollider != null)
                MovingPlatform.TryResolve(
                    _kccMotor.GroundingStatus.GroundCollider,
                    out supportingPlatform);

            if (_isGrounded && supportingPlatform != null)
            {
                // Grounding will attach KCC to this Rigidbody normally in the same motor phase.
                // The explicit override is only needed while the capsule is airborne.
                SetKccAirbornePlatformFrame(null);
                var platformChanged = _platform != supportingPlatform || !_platformAnchorLocked || !wasGrounded;
                _platform = supportingPlatform;
                _airborneFromPlatform = false;
                if (platformChanged)
                    _platformLocalAnchor = GetPlatformLocalPoint(
                        supportingPlatform, _kccMotor.TransientPosition);
                _platformAnchorLocked = true;
                _lastPlatformContactTime = Time.fixedTime;
                _airbornePlatformMomentum = Vector3.zero;
                _camera?.SetReferenceFrame(supportingPlatform.transform);
            }
            else if (_isGrounded)
            {
                SetKccAirbornePlatformFrame(null);
                ClearPlatformReference();
                _airbornePlatformMomentum = Vector3.zero;
            }
            else
            {
                if (wasGrounded && previousPlatform != null)
                {
                    _airbornePlatformMomentum = Vector3.zero;
                    SetKccAirbornePlatformFrame(previousPlatform);
                    _airborneFromPlatform = true;
                    _lastPlatformContactTime = Time.fixedTime;
                }
                else if (_airborneFromPlatform && _platform != null)
                    SetKccAirbornePlatformFrame(_platform);

                RefreshAirbornePlatformReference();
            }
        }

        public void AfterCharacterUpdate(float deltaTime)
        {
            if (!_useKccMotor || !IsOwner || _sceneTransitioning)
                return;

            if (_platform == null || !_platform.UsesKccMover ||
                (!_isGrounded && !_airborneFromPlatform))
            {
                ResetClientPlatformFrame();
                return;
            }

            // Both transient poses belong to the completed KCC tick. Never
            // subtract a render-time ship pose from a fixed-time motor pose.
            var mover = _platform.KccMover;
            var simulationWorldToLocal = Matrix4x4.TRS(mover.TransientPosition,
                mover.TransientRotation, _platform.transform.lossyScale).inverse;
            var localPosition = simulationWorldToLocal.MultiplyPoint3x4(_kccMotor.TransientPosition);

            var holdingDeckPosition = _isGrounded && !_kccMotor.MustUnground() &&
                                      _moveInput.sqrMagnitude <= 0.0001f && !_jumpQueued &&
                                      _platformAnchorLocked;
            if (holdingDeckPosition)
            {
                // The mover already transports the capsule by its angular velocity.
                // Re-apply the fixed local anchor to remove accumulated solver error
                // while the player is standing still on a rocking deck.
                _kccMotor.SetTransientPosition(GetPlatformWorldPoint(mover, _platformLocalAnchor));
                localPosition = _platformLocalAnchor;
            }
            else if (_isGrounded && !_kccMotor.MustUnground())
            {
                // Walking deliberately changes the local anchor. Store the completed
                // tick pose, so releasing the key locks the point where the player
                // actually stopped instead of the point where walking began.
                _platformLocalAnchor = localPosition;
                _platformAnchorLocked = true;
            }

            var localRotation = GetPlatformRelativeCharacterRotation(
                mover.TransientRotation, _kccMotor.TransientRotation);

            // KCC's default attached-rigidbody transport keeps the character upright by
            // design. A ship deck is different: the player's body must share the ship's
            // roll and pitch while retaining the yaw chosen by the player relative to deck.
            _kccMotor.SetTransientRotation(mover.TransientRotation * localRotation);
            if (!_clientPlatformPoseInitialized || _kccPresentationPlatform != _platform)
            {
                _clientPlatformPreviousLocalPosition = localPosition;
                _clientPlatformPreviousLocalRotation = localRotation;
            }
            else
            {
                _clientPlatformPreviousLocalPosition = _clientPlatformCurrentLocalPosition;
                _clientPlatformPreviousLocalRotation = _clientPlatformCurrentLocalRotation;
            }
            _clientPlatformCurrentLocalPosition = localPosition;
            _clientPlatformCurrentLocalRotation = localRotation;
            _clientPlatformPoseInitialized = true;
            _kccPresentationPlatform = _platform;
        }

        private static Quaternion GetPlatformRelativeCharacterRotation(
            Quaternion platformRotation, Quaternion characterRotation)
        {
            var localForward = Quaternion.Inverse(platformRotation) *
                               (characterRotation * Vector3.forward);
            localForward = Vector3.ProjectOnPlane(localForward, Vector3.up);
            if (localForward.sqrMagnitude < 0.0001f)
                localForward = Vector3.forward;

            return Quaternion.LookRotation(localForward.normalized, Vector3.up);
        }

        private static Vector3 GetPlatformLocalPoint(MovingPlatform platform, Vector3 worldPoint)
        {
            var mover = platform != null ? platform.KccMover : null;
            if (mover == null)
                return platform != null ? platform.transform.InverseTransformPoint(worldPoint) : worldPoint;

            var frame = Matrix4x4.TRS(mover.TransientPosition, mover.TransientRotation,
                platform.transform.lossyScale);
            return frame.inverse.MultiplyPoint3x4(worldPoint);
        }

        private static Vector3 GetPlatformWorldPoint(PhysicsMover mover, Vector3 localPoint)
        {
            var platform = mover != null ? mover.GetComponent<MovingPlatform>() : null;
            var scale = platform != null ? platform.transform.lossyScale : Vector3.one;
            return Matrix4x4.TRS(mover.TransientPosition, mover.TransientRotation, scale)
                .MultiplyPoint3x4(localPoint);
        }

        public bool IsColliderValidForCollisions(Collider coll)
        {
            return coll != null && coll != bodyCollider && !coll.transform.IsChildOf(transform);
        }

        public void OnGroundHit(
            Collider hitCollider,
            Vector3 hitNormal,
            Vector3 hitPoint,
            ref HitStabilityReport hitStabilityReport)
        {
        }

        public void OnMovementHit(
            Collider hitCollider,
            Vector3 hitNormal,
            Vector3 hitPoint,
            ref HitStabilityReport hitStabilityReport)
        {
            if (!IsOwner || hitCollider == null || _kccMotor == null)
                return;

            var pushable = hitCollider.GetComponentInParent<NetworkPhysicsObject>();
            if (pushable == null)
                return;

            var pushDirection = Vector3.ProjectOnPlane(-hitNormal, Vector3.up);
            if (pushDirection.sqrMagnitude < 0.0001f)
                return;

            pushDirection.Normalize();
            var intendedDirection = _desiredBodyRotation *
                                    new Vector3(_moveInput.x, 0f, _moveInput.y);
            intendedDirection = Vector3.ProjectOnPlane(intendedDirection, Vector3.up);
            if (intendedDirection.sqrMagnitude > 1f)
                intendedDirection.Normalize();

            // BaseVelocity may already be projected to zero by the kinematic client proxy
            // when this callback runs. Input intent remains stable and is the value the host
            // and remote client can reproduce identically.
            var intendedSpeed = moveSpeed * (_sprintHeld ? sprintMultiplier : 1f);
            var approachSpeed = Mathf.Max(
                0f,
                Vector3.Dot(intendedDirection * intendedSpeed, pushDirection));
            pushable.RequestPush(pushDirection, approachSpeed);
        }

        public void ProcessHitStabilityReport(
            Collider hitCollider,
            Vector3 hitNormal,
            Vector3 hitPoint,
            Vector3 atCharacterPosition,
            Quaternion atCharacterRotation,
            ref HitStabilityReport hitStabilityReport)
        {
        }

        public void OnDiscreteCollisionDetected(Collider hitCollider)
        {
        }

        private void SetKccAirbornePlatformFrame(MovingPlatform platform)
        {
            if (!_useKccMotor || _kccMotor == null)
                return;

            _kccMotor.AttachedRigidbodyOverride = platform != null ? platform.Body : null;
        }

        private void SimulateMovement(float deltaTime)
        {
            ApplyClientPlatformFrameMotion();
            body.MoveRotation(_desiredBodyRotation);

            var wasGrounded = _isGrounded;
            var previousPlatform = _platform;
            var mayGround = Time.fixedTime >= _ignoreGroundUntil;
            var groundHit = default(RaycastHit);
            var hasGroundCandidate = mayGround && TryGetGroundHit(out groundHit);
            MovingPlatform supportingPlatform = null;
            if (hasGroundCandidate)
                MovingPlatform.TryResolve(groundHit.collider, out supportingPlatform);

            _isGrounded = hasGroundCandidate;
            if (_isGrounded && !wasGrounded && supportingPlatform != null &&
                supportingPlatform.UsesInterpolatedNetworkMotion)
            {
                var groundGap = Mathf.Max(0f, groundHit.distance - GroundCastStartOffset);
                if (groundGap > clientPlatformContactTolerance)
                {
                    // The long probe locates a fast remote deck but must not count as physical
                    // contact. Otherwise the anchor records the capsule while it is still in air.
                    _isGrounded = false;
                    supportingPlatform = null;
                }
                else if (groundGap > 0.0001f)
                {
                    // Resolve the remaining small gap before locking the ship-local anchor so
                    // clients stand at the same capsule height as the host.
                    body.position += Vector3.down * groundGap;
                }
            }

            _groundNormal = _isGrounded ? groundHit.normal.normalized : Vector3.up;

            if (_isGrounded && supportingPlatform != null)
            {
                if (_platform != supportingPlatform || _airborneFromPlatform || !wasGrounded)
                    AttachToPlatform(supportingPlatform);
                else
                    _lastPlatformContactTime = Time.fixedTime;
            }
            else if (_isGrounded)
            {
                ConvertClientPlatformVelocityToWorld(previousPlatform);
                ClearPlatformReference();
            }
            else
            {
                if (wasGrounded)
                {
                    var departurePlatformVelocity = previousPlatform != null
                        ? previousPlatform.GetPointVelocity(body.position)
                        : Vector3.zero;
                    ConvertClientPlatformVelocityToWorld(previousPlatform, departurePlatformVelocity);
                    _airbornePlatformMomentum = departurePlatformVelocity;
                    if (previousPlatform != null)
                    {
                        _airborneFromPlatform = true;
                        _lastPlatformContactTime = Time.fixedTime;
                    }
                }

                RefreshAirbornePlatformReference();
            }

            var speed = moveSpeed * (_sprintHeld ? sprintMultiplier : 1f);
            var inputDirection = _desiredBodyRotation *
                                 new Vector3(_moveInput.x, 0f, _moveInput.y);
            if (inputDirection.sqrMagnitude > 1f)
                inputDirection.Normalize();

            var currentVelocity = body.linearVelocity;
            var platformVelocity = _isGrounded && supportingPlatform != null
                ? supportingPlatform.GetPointVelocity(body.position)
                : Vector3.zero;
            var usesClientRelativeMotion = _isGrounded && supportingPlatform != null &&
                                           supportingPlatform.UsesInterpolatedNetworkMotion;

            if (_isGrounded)
            {
                var desiredGroundVelocity = Vector3.ProjectOnPlane(inputDirection, _groundNormal);
                if (desiredGroundVelocity.sqrMagnitude > 0.0001f)
                    desiredGroundVelocity = desiredGroundVelocity.normalized * speed;

                // A remote ship advances through buffered snapshots in render time. Its replicated
                // velocity must not also be integrated by the local Rigidbody or the player is
                // carried twice on some fixed ticks and not at all on others. Once attached, this
                // motor stores velocity relative to that ship and frame displacement carries the
                // body between the ship poses sampled by physics.
                var relativeVelocity = currentVelocity -
                                       (usesClientRelativeMotion && _usingClientPlatformRelativeVelocity
                                           ? Vector3.zero
                                           : platformVelocity);
                var relativeGroundVelocity = Vector3.ProjectOnPlane(relativeVelocity, _groundNormal);
                relativeGroundVelocity = Vector3.MoveTowards(
                    relativeGroundVelocity, desiredGroundVelocity, groundAcceleration * deltaTime);

                var anchorCorrection = Vector3.zero;
                if (supportingPlatform != null)
                {
                    if (_moveInput.sqrMagnitude > 0.0001f)
                    {
                        // While walking, the anchor follows the physics body instead of pulling it
                        // back to the point where movement started.
                        _platformLocalAnchor = supportingPlatform.transform.InverseTransformPoint(body.position);
                        _platformAnchorLocked = false;
                    }
                    else if (!_platformAnchorLocked)
                    {
                        // Lock at the final physics position on the first stationary tick. This
                        // avoids a one-tick snap after releasing a movement key.
                        _platformLocalAnchor = supportingPlatform.transform.InverseTransformPoint(body.position);
                        _platformAnchorLocked = true;
                    }
                    else
                    {
                        var anchorTarget = supportingPlatform.transform.TransformPoint(_platformLocalAnchor);
                        anchorCorrection = Vector3.ClampMagnitude(
                            (anchorTarget - body.position) * platformAnchorSharpness,
                            maximumPlatformCorrectionSpeed);
                    }
                }

                if (_jumpQueued)
                {
                    var jumpSpeed = Mathf.Sqrt(jumpHeight * -2f * gravity);
                    body.linearVelocity = platformVelocity + relativeGroundVelocity + Vector3.up * jumpSpeed;
                    _usingClientPlatformRelativeVelocity = false;
                    _airbornePlatformMomentum = platformVelocity;
                    _isGrounded = false;
                    _ignoreGroundUntil = Time.fixedTime + 0.12f;
                    if (supportingPlatform != null)
                    {
                        _platform = supportingPlatform;
                        _airborneFromPlatform = true;
                        _platformLocalAnchor = supportingPlatform.transform.InverseTransformPoint(body.position);
                        _platformAnchorLocked = false;
                    }
                }
                else
                {
                    var velocityFrame = usesClientRelativeMotion ? Vector3.zero : platformVelocity;
                    body.linearVelocity = velocityFrame + relativeGroundVelocity + anchorCorrection +
                                          -_groundNormal * groundStickSpeed;
                    _usingClientPlatformRelativeVelocity = usesClientRelativeMotion;
                }
            }
            else
            {
                var horizontalVelocity = Vector3.ProjectOnPlane(currentVelocity, Vector3.up);
                if (_moveInput.sqrMagnitude > 0.0001f)
                {
                    var targetAirVelocity = Vector3.ProjectOnPlane(_airbornePlatformMomentum, Vector3.up) +
                                            Vector3.ProjectOnPlane(inputDirection, Vector3.up).normalized * speed;
                    horizontalVelocity = Vector3.MoveTowards(
                        horizontalVelocity, targetAirVelocity, airAcceleration * deltaTime);
                }

                body.linearVelocity = horizontalVelocity +
                                      Vector3.up * (currentVelocity.y + gravity * deltaTime);
            }

            _jumpQueued = false;
            var animationPlatformVelocity = usesClientRelativeMotion && _isGrounded
                ? Vector3.zero
                : platformVelocity;
            var relativeVerticalVelocity = body.linearVelocity.y - animationPlatformVelocity.y;
            animationSync.SetLocomotion(
                _moveInput.magnitude * (speed / Mathf.Max(0.01f, moveSpeed)),
                _isGrounded,
                relativeVerticalVelocity);
        }

        private void ApplyClientPlatformFrameMotion()
        {
            if (_platform == null || !_platform.UsesInterpolatedNetworkMotion)
                return;

            var platformTransform = _platform.transform;
            if (_airborneFromPlatform)
            {
                // Air physics remains world-space, but its completed fixed-step poses are sampled
                // in the same platform frame used while grounded. Rendering never has to switch
                // between the player's world interpolation and the ship's snapshot timeline.
                SampleClientPlatformPose(platformTransform);
                return;
            }

            if (!_usingClientPlatformRelativeVelocity)
                return;

            if (!_clientPlatformPoseInitialized)
            {
                InitializeClientPlatformFrame(platformTransform);
                return;
            }

            // body.position is the result of the preceding physics step, but the ship may already
            // have advanced in a render Update. Recover the completed movement against the ship
            // pose used by that physics step, then rebuild it against the newest ship pose.
            // Use the complete previous platform matrix. The ship prefab is uniformly scaled and
            // its walkable surfaces are child colliders; rotation-only conversion would apply the
            // root scale a second time during presentation and visibly lift clients above the deck.
            var completedLocalPosition = _clientPlatformWorldToLocal.MultiplyPoint3x4(body.position);
            var completedLocalRotation = Quaternion.Inverse(_clientPlatformPoseRotation) * body.rotation;
            _clientPlatformPreviousLocalPosition = _clientPlatformCurrentLocalPosition;
            _clientPlatformPreviousLocalRotation = _clientPlatformCurrentLocalRotation;
            _clientPlatformCurrentLocalPosition = completedLocalPosition;
            _clientPlatformCurrentLocalRotation = completedLocalRotation;

            var currentPlatformRotation = platformTransform.rotation;
            var frameRotation = currentPlatformRotation * Quaternion.Inverse(_clientPlatformPoseRotation);
            body.position = platformTransform.TransformPoint(completedLocalPosition);
            body.linearVelocity = frameRotation * body.linearVelocity;

            _clientPlatformPoseRotation = currentPlatformRotation;
            _clientPlatformWorldToLocal = platformTransform.worldToLocalMatrix;
        }

        private void SampleClientPlatformPose(Transform platformTransform)
        {
            if (platformTransform == null)
                return;

            if (!_clientPlatformPoseInitialized)
            {
                InitializeClientPlatformFrame(platformTransform);
                return;
            }

            _clientPlatformPreviousLocalPosition = _clientPlatformCurrentLocalPosition;
            _clientPlatformPreviousLocalRotation = _clientPlatformCurrentLocalRotation;
            _clientPlatformCurrentLocalPosition = platformTransform.InverseTransformPoint(body.position);
            _clientPlatformCurrentLocalRotation =
                Quaternion.Inverse(platformTransform.rotation) * body.rotation;
            _clientPlatformPoseRotation = platformTransform.rotation;
            _clientPlatformWorldToLocal = platformTransform.worldToLocalMatrix;
        }

        private void InitializeClientPlatformFrame(Transform platformTransform)
        {
            if (platformTransform == null)
            {
                ResetClientPlatformFrame();
                return;
            }

            _clientPlatformPoseRotation = platformTransform.rotation;
            _clientPlatformWorldToLocal = platformTransform.worldToLocalMatrix;
            var localPosition = platformTransform.InverseTransformPoint(body.position);
            var localRotation = Quaternion.Inverse(_clientPlatformPoseRotation) * body.rotation;
            _clientPlatformPreviousLocalPosition = localPosition;
            _clientPlatformCurrentLocalPosition = localPosition;
            _clientPlatformPreviousLocalRotation = localRotation;
            _clientPlatformCurrentLocalRotation = localRotation;
            _clientPlatformPoseInitialized = true;
        }

        private void ResetClientPlatformFrame()
        {
            _clientPlatformPoseInitialized = false;
            _kccPresentationPlatform = null;
            _usingClientPlatformRelativeVelocity = false;
        }

        private void ConvertClientPlatformVelocityToWorld(
            MovingPlatform platform,
            Vector3? knownPlatformVelocity = null)
        {
            if (!_usingClientPlatformRelativeVelocity)
                return;

            if (body != null && !body.isKinematic && platform != null)
                body.linearVelocity += knownPlatformVelocity ?? platform.GetPointVelocity(body.position);

            ResetClientPlatformFrame();
        }

        private bool TryGetGroundHit(out RaycastHit nearestHit, float probeDistance = -1f,
            float minimumUpDot = -1f)
        {
            nearestHit = default;
            if (bodyCollider == null || !bodyCollider.enabled)
                return false;

            var scale = transform.lossyScale;
            var radiusScale = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
            var radius = Mathf.Max(0.05f, bodyCollider.radius * radiusScale * 0.92f);
            var halfHeight = Mathf.Max(radius,
                bodyCollider.height * Mathf.Abs(scale.y) * 0.5f);
            var scaledCenter = Vector3.Scale(bodyCollider.center, scale);
            var rootPosition = _useKccMotor && _kccMotor != null
                ? _kccMotor.TransientPosition
                : body.position;
            var rootRotation = _useKccMotor && _kccMotor != null
                ? _kccMotor.TransientRotation
                : body.rotation;
            var worldCenter = rootPosition + rootRotation * scaledCenter;
            var bottomSphere = worldCenter - rootRotation * Vector3.up * (halfHeight - radius);
            var origin = bottomSphere + Vector3.up * GroundCastStartOffset;
            var distance = (probeDistance > 0f ? probeDistance : platformProbeDistance) +
                           GroundCastStartOffset;
            var hitCount = Physics.SphereCastNonAlloc(origin, radius, Vector3.down, _groundHits,
                distance, ~0, QueryTriggerInteraction.Ignore);
            var nearestDistance = float.PositiveInfinity;
            var requiredUpDot = minimumUpDot >= 0f
                ? minimumUpDot
                : Mathf.Cos(maximumSlopeAngle * Mathf.Deg2Rad);

            for (var i = 0; i < hitCount; i++)
            {
                var hit = _groundHits[i];
                if (hit.collider == null || hit.collider == bodyCollider ||
                    hit.distance >= nearestDistance || Vector3.Dot(hit.normal, Vector3.up) < requiredUpDot)
                    continue;

                nearestDistance = hit.distance;
                nearestHit = hit;
            }

            return nearestDistance < float.PositiveInfinity;
        }

        private bool TryFindPlatformBelow(out MovingPlatform platform, float probeDistance)
        {
            platform = null;
            return TryGetGroundHit(out var hit, probeDistance, 0.35f) &&
                   MovingPlatform.TryResolve(hit.collider, out platform);
        }

        private void RefreshAirbornePlatformReference()
        {
            if (_platform == null)
            {
                ClearPlatformReference();
                return;
            }

            if (Time.fixedTime - _lastPlatformContactTime <= platformContactGrace)
                return;

            var airborneProbeDistance = jumpHeight + bodyCollider.height * Mathf.Abs(transform.lossyScale.y) +
                                        platformProbeDistance;
            if (TryFindPlatformBelow(out var platformBelow, airborneProbeDistance) && platformBelow == _platform)
                return;

            ClearPlatformReference();
        }

        private void AttachToPlatform(MovingPlatform platform)
        {
            if (platform == null)
                return;

            SetKccAirbornePlatformFrame(null);

            var preserveAirborneSamples = _platform == platform && _airborneFromPlatform &&
                                          _clientPlatformPoseInitialized;
            if (_platform != platform)
                ConvertClientPlatformVelocityToWorld(_platform);

            _platform = platform;
            _airborneFromPlatform = false;
            _platformLocalAnchor = platform.transform.InverseTransformPoint(body != null ? body.position : transform.position);
            _platformAnchorLocked = true;
            _lastPlatformContactTime = Time.fixedTime;
            _airbornePlatformMomentum = Vector3.zero;
            if (platform.UsesInterpolatedNetworkMotion)
            {
                if (preserveAirborneSamples)
                    SampleClientPlatformPose(platform.transform);
                else
                    InitializeClientPlatformFrame(platform.transform);
            }
            else
                ResetClientPlatformFrame();
            _camera?.SetReferenceFrame(platform.transform);
            PublishPlatformPose();
        }

        private void ClearPlatformReference()
        {
            // Also clear a stale KCC override when the bookkeeping was already reset by
            // placement/despawn code. Leaving the override alive would keep carrying the
            // character with an old ship while the gameplay state says it is world-relative.
            SetKccAirbornePlatformFrame(null);

            if (_platform == null && !_airborneFromPlatform)
                return;

            _platform = null;
            _airborneFromPlatform = false;
            _platformAnchorLocked = false;
            _airbornePlatformMomentum = Vector3.zero;
            ResetClientPlatformFrame();
            _camera?.SetReferenceFrame(null);
            PublishPlatformPose();
        }

        private void PublishPlatformPose()
        {
            if (!IsOwner || !IsSpawned)
                return;

            var hasContinuousAirborneFrame = _platform != null && _airborneFromPlatform &&
                                             (_useKccMotor ||
                                              (_platform.UsesInterpolatedNetworkMotion &&
                                               _clientPlatformPoseInitialized));
            if (_platform == null || (!_isGrounded && !hasContinuousAirborneFrame && !IsAtControlStation))
            {
                _hasReplicatedPlatform.Value = false;
                return;
            }

            var platformObject = _platform.GetComponentInParent<NetworkObject>();
            if (platformObject == null || !platformObject.IsSpawned)
            {
                _hasReplicatedPlatform.Value = false;
                return;
            }

            var platformWorldPosition = _useKccMotor
                ? _presentationRoot.position
                : _platformAnchorLocked
                    ? _platform.transform.TransformPoint(_platformLocalAnchor)
                    : transform.position;
            var platformWorldRotation = _useKccMotor ? _presentationRoot.rotation : body.rotation;
            if (!_useKccMotor && _platform.UsesInterpolatedNetworkMotion &&
                TryGetClientPlatformPresentationPose(out var localPosition, out var localRotation))
            {
                platformWorldPosition = _platform.transform.TransformPoint(localPosition);
                platformWorldRotation = _platform.transform.rotation * localRotation;
            }

            _replicatedPlatform.Value = new NetworkObjectReference(platformObject);
            _replicatedPlatformLocalPosition.Value =
                platformObject.transform.InverseTransformPoint(platformWorldPosition);
            _replicatedPlatformLocalRotation.Value =
                Quaternion.Inverse(platformObject.transform.rotation) * platformWorldRotation;
            _hasReplicatedPlatform.Value = true;
        }

        // Compare interaction positions in the same ship frame on the server.
        // A client's delayed world Transform would otherwise select the wrong
        // handle or fail the distance check on a fast moving vessel.
        public bool TryGetPositionOnPlatform(NetworkObject platformObject, out Vector3 worldPosition)
        {
            worldPosition = default;
            if (!_hasReplicatedPlatform.Value ||
                !_replicatedPlatform.Value.TryGet(out var supportingObject, NetworkManager) ||
                supportingObject != platformObject)
                return false;
            worldPosition = platformObject.transform.TransformPoint(_replicatedPlatformLocalPosition.Value);
            return true;
        }

        public NetworkShipController GetSupportingShipOnServer()
        {
            if (!IsServer || !_hasReplicatedPlatform.Value ||
                !_replicatedPlatform.Value.TryGet(out var platformObject, NetworkManager)) return null;
            return platformObject.GetComponent<NetworkShipController>();
        }

        public void GetItemDropPose(out Vector3 position, out Vector3 forward, out NetworkObject platformObject)
        {
            var source = _presentationRoot != null ? _presentationRoot : transform;
            position = source.position;
            forward = source.forward;
            platformObject = _platform != null && (_isGrounded || _airborneFromPlatform || IsAtControlStation)
                ? _platform.GetComponentInParent<NetworkObject>() : null;
            if (platformObject != null && !platformObject.IsSpawned) platformObject = null;
        }

        private void ApplyRemotePlatformPose()
        {
            if (!_hasReplicatedPlatform.Value ||
                !_replicatedPlatform.Value.TryGet(out var platformObject, NetworkManager) ||
                platformObject == null || !platformObject.IsSpawned ||
                platformObject.GetComponentInChildren<MovingPlatform>(true) == null)
            {
                ApplyRemoteWorldPresentation();
                return;
            }

            // Occupancy is server-authored. Ease into the authored handle once;
            // its subsequent circular motion follows the rotor directly.
            var ship = platformObject.GetComponent<NetworkShipController>();
            Transform operatorStation = null;
            if (ship != null)
            {
                if (ship.Anchor != null)
                    operatorStation = ship.Anchor.GetHandleStation(ship.GetAnchorHandleForClient(OwnerClientId));
                if (ship.TryGetComponent<ShipCannonBattery>(out var battery))
                {
                    var index = battery.GetOperatorCannon(OwnerClientId);
                    if (index >= 0 && index < battery.Cannons.Length) operatorStation = battery.Cannons[index].Station;
                }
                if (operatorStation != null)
                {
                    _remotePlatformObject = platformObject;
                    _remotePlatformPoseInitialized = true;
                    _remoteWorldPresentationActive = false;
                    _remoteWorldPresentationOffset = Vector3.zero;
                    _remoteWorldPresentationRotationOffset = Quaternion.identity;
                    GetAnchorApproachPose(operatorStation, out var approachPosition, out var approachRotation);
                    _remotePlatformLocalPosition = platformObject.transform.InverseTransformPoint(approachPosition);
                    _remotePlatformLocalRotation = Quaternion.Inverse(platformObject.transform.rotation) * approachRotation;
                    _presentationRoot.SetPositionAndRotation(approachPosition, approachRotation);
                    return;
                }
            }

            _anchorApproachStation = null;
            var targetLocalPosition = _replicatedPlatformLocalPosition.Value;
            var targetLocalRotation = _replicatedPlatformLocalRotation.Value;
            if (!_remotePlatformPoseInitialized || _remotePlatformObject != platformObject)
            {
                _remotePlatformObject = platformObject;
                // Enter platform space from the pose already on screen. This avoids a one-frame
                // pop when the grounded flag arrives on a different network tick than position.
                _remotePlatformLocalPosition = platformObject.transform.InverseTransformPoint(
                    _presentationRoot.position);
                _remotePlatformLocalRotation = Quaternion.Inverse(platformObject.transform.rotation) *
                                               _presentationRoot.rotation;
                _remotePlatformPoseInitialized = true;
                _remoteWorldPresentationActive = false;
                _remoteWorldPresentationOffset = Vector3.zero;
                _remoteWorldPresentationRotationOffset = Quaternion.identity;
            }
            else
            {
                var positionBlend = 1f - Mathf.Exp(-remotePlatformPositionSharpness * Time.deltaTime);
                var rotationBlend = 1f - Mathf.Exp(-remotePlatformRotationSharpness * Time.deltaTime);
                _remotePlatformLocalPosition = Vector3.Lerp(
                    _remotePlatformLocalPosition, targetLocalPosition, positionBlend);
                _remotePlatformLocalRotation = Quaternion.Slerp(
                    _remotePlatformLocalRotation, targetLocalRotation, rotationBlend);
            }

            // Apply after platform snapshot interpolation. Platform motion is exact in this
            // frame; only the player's own walking/jumping is smoothed in platform space.
            _presentationRoot.SetPositionAndRotation(
                platformObject.transform.TransformPoint(_remotePlatformLocalPosition),
                platformObject.transform.rotation * _remotePlatformLocalRotation);
        }

        private void ApplyRemoteWorldPresentation()
        {
            _anchorApproachStation = null;
            if (_presentationRoot == null)
                return;

            if (_remotePlatformPoseInitialized)
            {
                _remoteWorldPresentationOffset = _presentationRoot.position - transform.position;
                _remoteWorldPresentationRotationOffset =
                    Quaternion.Inverse(transform.rotation) * _presentationRoot.rotation;
                _remoteWorldPresentationActive = true;
                _remotePlatformObject = null;
                _remotePlatformPoseInitialized = false;
            }

            if (!_remoteWorldPresentationActive)
            {
                ResetPresentationPose();
                return;
            }

            var positionDecay = Mathf.Exp(-remotePlatformPositionSharpness * Time.deltaTime);
            var rotationDecay = Mathf.Exp(-remotePlatformRotationSharpness * Time.deltaTime);
            _remoteWorldPresentationOffset *= positionDecay;
            _remoteWorldPresentationRotationOffset = Quaternion.Slerp(
                Quaternion.identity,
                _remoteWorldPresentationRotationOffset,
                rotationDecay);

            if (_remoteWorldPresentationOffset.sqrMagnitude < 0.00000001f &&
                Quaternion.Angle(_remoteWorldPresentationRotationOffset, Quaternion.identity) < 0.01f)
            {
                _remoteWorldPresentationActive = false;
                ResetPresentationPose();
                return;
            }

            _presentationRoot.SetPositionAndRotation(
                transform.position + _remoteWorldPresentationOffset,
                transform.rotation * _remoteWorldPresentationRotationOffset);
        }

        private void ResetRemotePlatformPose()
        {
            _anchorApproachStation = null;
            _remotePlatformObject = null;
            _remotePlatformPoseInitialized = false;
            _remoteWorldPresentationActive = false;
            _remoteWorldPresentationOffset = Vector3.zero;
            _remoteWorldPresentationRotationOffset = Quaternion.identity;
            ResetPresentationPose();
        }

        private void LateUpdate()
        {
            if (!IsSpawned)
                return;

            if (!IsOwner)
            {
                ApplyRemotePlatformPose();
                return;
            }

            if (_sceneTransitioning)
                return;

            if (_useKccMotor)
            {
                if (!SnapToActiveControlStation())
                {
                    // Display local walking/jumping in the ship's snapshot render frame.
                    // The motor root retains KCC collision simulation and world
                    // interpolation; only the camera/visual child is rebased.
                    if (_platform != null && _platform.UsesInterpolatedNetworkMotion &&
                        _clientPlatformPoseInitialized && (_isGrounded || _airborneFromPlatform))
                        ApplyOwnerPlatformPresentation();
                    else
                        ResetPresentationPose();
                }
                _camera?.RefreshPose();
                PublishPlatformPose();
                UpdatePresentedInteraction();
                return;
            }

            if (_platform != null)
            {
                // Platform snapshot interpolation happens before this LateUpdate. Keep
                // the rendered body/camera in the exact platform frame while physics remains in
                // fixed time. This removes the render-time gap without teleporting the Rigidbody.
                var usesContinuousAirborneFrame = _airborneFromPlatform &&
                                                  _platform.UsesInterpolatedNetworkMotion &&
                                                  _clientPlatformPoseInitialized;
                if (!SnapToActiveControlStation() &&
                    ((_isGrounded && !_airborneFromPlatform) || usesContinuousAirborneFrame))
                    ApplyOwnerPlatformPresentation();
                else if (!IsAtControlStation)
                    ApplyOwnerWorldPresentation();
                _camera?.RefreshPose();
            }
            else
            {
                ApplyOwnerWorldPresentation();
            }

            PublishPlatformPose();
            _camera?.RefreshPose();
            UpdatePresentedInteraction();
        }

        private void UpdatePresentedInteraction()
        {
            // Query after ship, player and camera presentation agree with this frame's HUD.
            // A station's release key must not also interact again in the same frame.
            if (!_interactionInputBlockedThisFrame && !PlayerEquipment.InputCaptured &&
                !IsAtControlStation && Keyboard.current != null &&
                (Keyboard.current.eKey.wasPressedThisFrame || _loweringAnchor != null))
                UpdateInteraction();
        }

        private void ApplyOwnerPlatformPresentation()
        {
            if (_presentationRoot == null || _platform == null)
                return;

            Vector3 targetPosition;
            Quaternion targetRotation;

            if (_platform.UsesInterpolatedNetworkMotion &&
                TryGetClientPlatformPresentationPose(out var localPosition, out var localRotation))
            {
                // The local walk is interpolated in ship space, then composed with the ship's
                // newest render pose. Translation of the ship therefore reaches the camera and
                // character exactly once and cannot fight Rigidbody interpolation.
                targetPosition = _platform.transform.TransformPoint(localPosition);
                targetRotation = _platform.transform.rotation * localRotation;
            }
            else if (_airborneFromPlatform || !_platformAnchorLocked || _moveInput.sqrMagnitude > 0.0001f)
            {
                targetPosition = transform.position;
                targetRotation = transform.rotation;
            }
            else
            {
                targetPosition = _platform.transform.TransformPoint(_platformLocalAnchor);
                targetRotation = body.rotation;
            }

            if (!_ownerPlatformPresentationActive)
            {
                // Land in platform space from the pose already displayed in world space. Contact
                // and the network pose can arrive on adjacent ticks, so snapping directly to the
                // new target would produce a visible landing jerk.
                _ownerWorldPresentationOffset = _presentationRoot.position - targetPosition;
                _ownerWorldPresentationRotationOffset =
                    Quaternion.Inverse(targetRotation) * _presentationRoot.rotation;
            }

            var positionDecay = Mathf.Exp(-remotePlatformPositionSharpness * Time.deltaTime);
            var rotationDecay = Mathf.Exp(-remotePlatformRotationSharpness * Time.deltaTime);
            _ownerWorldPresentationOffset *= positionDecay;
            _ownerWorldPresentationRotationOffset = Quaternion.Slerp(
                Quaternion.identity,
                _ownerWorldPresentationRotationOffset,
                rotationDecay);
            _ownerPlatformPresentationActive = true;
            _presentationRoot.SetPositionAndRotation(
                targetPosition + _ownerWorldPresentationOffset,
                targetRotation * _ownerWorldPresentationRotationOffset);
        }

        private void ApplyOwnerWorldPresentation()
        {
            if (_presentationRoot == null)
                return;

            if (_ownerPlatformPresentationActive)
            {
                // Preserve the last deck-relative render pose on the take-off frame. The tiny
                // fixed/render-time gap then fades out instead of becoming a visible backward snap.
                _ownerWorldPresentationOffset = _presentationRoot.position - transform.position;
                _ownerWorldPresentationRotationOffset =
                    Quaternion.Inverse(transform.rotation) * _presentationRoot.rotation;
                _ownerPlatformPresentationActive = false;
            }

            var preserveTakeoffContinuity = _platform != null && _airborneFromPlatform &&
                                            _platform.UsesInterpolatedNetworkMotion;
            if (!preserveTakeoffContinuity)
            {
                var decay = Mathf.Exp(-remotePlatformPositionSharpness * Time.deltaTime);
                _ownerWorldPresentationOffset *= decay;
                _ownerWorldPresentationRotationOffset = Quaternion.Slerp(
                    Quaternion.identity,
                    _ownerWorldPresentationRotationOffset,
                    decay);
            }

            if (_ownerWorldPresentationOffset.sqrMagnitude < 0.00000001f &&
                Quaternion.Angle(_ownerWorldPresentationRotationOffset, Quaternion.identity) < 0.01f)
            {
                _ownerWorldPresentationOffset = Vector3.zero;
                _ownerWorldPresentationRotationOffset = Quaternion.identity;
                _presentationRoot.localPosition = Vector3.zero;
                _presentationRoot.localRotation = Quaternion.identity;
                _presentationRoot.localScale = Vector3.one;
                return;
            }

            _presentationRoot.SetPositionAndRotation(
                transform.position + _ownerWorldPresentationOffset,
                transform.rotation * _ownerWorldPresentationRotationOffset);
        }

        private bool TryGetClientPlatformPresentationPose(
            out Vector3 localPosition,
            out Quaternion localRotation)
        {
            localPosition = default;
            localRotation = Quaternion.identity;
            if (!_clientPlatformPoseInitialized)
                return false;

            var interpolation = Mathf.Clamp01(
                (Time.time - Time.fixedTime) / Mathf.Max(Time.fixedDeltaTime, 0.0001f));
            localPosition = Vector3.Lerp(
                _clientPlatformPreviousLocalPosition,
                _clientPlatformCurrentLocalPosition,
                interpolation);
            localRotation = Quaternion.Slerp(
                _clientPlatformPreviousLocalRotation,
                _clientPlatformCurrentLocalRotation,
                interpolation);
            return true;
        }

        private void ResetPresentationPose()
        {
            if (_presentationRoot == null)
                return;

            _presentationRoot.localPosition = Vector3.zero;
            _presentationRoot.localRotation = Quaternion.identity;
            _presentationRoot.localScale = Vector3.one;
            _ownerPlatformPresentationActive = false;
            _ownerWorldPresentationOffset = Vector3.zero;
            _ownerWorldPresentationRotationOffset = Quaternion.identity;
        }

        private void UpdateInteraction()
        {
            var target = FindInteractionTarget(_loweringAnchor == null, out var aimedDirectly);
            _lookedAtAnchor = aimedDirectly ? target as ShipAnchor : null;

            if (_loweringAnchor != null)
            {
                UpdateAnchorLowerHold();
                return;
            }

            if (target == null || !Keyboard.current.eKey.wasPressedThisFrame || _pendingAnchor != null || _pendingCannon != null)
                return;

            if (_lookedAtAnchor != null && _lookedAtAnchor.Ship != null &&
                _lookedAtAnchor.Ship.CanBeginAnchorDrop)
            {
                BeginAnchorLowerHold(_lookedAtAnchor);
                return;
            }

            target.Interact(this);
        }

        private IPlayerInteractable FindInteractionTarget(bool allowProximity, out bool aimedDirectly)
        {
            aimedDirectly = false;
            var view = _camera != null ? _camera.GetComponent<Camera>() : null;
            var ray = view != null ? view.ViewportPointToRay(CannonReloadProgress.AimViewportPoint)
                : new Ray(cameraTarget.position, transform.forward);
            var playerPosition = (_presentationRoot != null ? _presentationRoot.position : transform.position) +
                (_presentationRoot != null ? _presentationRoot.up : transform.up) * 0.9f;
            var distance = interactionDistance + Vector3.Distance(playerPosition, ray.origin);
            var hits = GetInteractionRayHits(ray, distance, out var count);

            // Pickups under the reticle have priority over nearby station volumes.
            // This prevents a cannon or helm from stealing E when an item is visibly
            // targeted beside it.
            var nearestPickupDistance = float.PositiveInfinity;
            IPlayerInteractable nearestPickup = null;
            for (var i = 0; i < count; i++)
            {
                var collider = hits[i].collider;
                if (IgnoreInteractionCollider(collider)) continue;
                var pickup = collider.GetComponentInParent<WorldItem>();
                if (pickup == null || hits[i].distance >= nearestPickupDistance) continue;
                var pickupPoint = collider.ClosestPoint(playerPosition);
                if ((pickupPoint - playerPosition).sqrMagnitude > interactionDistance * interactionDistance ||
                    !HasInteractionLineOfSight(ray.origin, pickupPoint, pickup)) continue;
                nearestPickupDistance = hits[i].distance;
                nearestPickup = pickup;
            }
            if (nearestPickup != null)
            {
                aimedDirectly = true;
                return nearestPickup;
            }

            var nearestDistance = float.PositiveInfinity;
            Collider nearest = null;
            for (var i = 0; i < count; i++)
            {
                var collider = hits[i].collider;
                if (IgnoreInteractionCollider(collider)) continue;
                var interactable = collider.GetComponentInParent<IPlayerInteractable>();
                // Loot triggers participate; water/area triggers do not hide the object behind them.
                if (collider.isTrigger && interactable == null) continue;
                if (hits[i].distance < nearestDistance)
                {
                    nearestDistance = hits[i].distance;
                    nearest = collider;
                }
            }
            if (nearest != null)
            {
                var target = nearest.GetComponentInParent<IPlayerInteractable>();
                if (target != null)
                {
                    // An aimed object outside reach must never select a different nearby item.
                    aimedDirectly = true;
                    return (nearest.ClosestPoint(playerPosition) - playerPosition).sqrMagnitude <=
                        interactionDistance * interactionDistance ? target : null;
                }
            }
            if (!allowProximity || interactionProximityDistance <= 0f) return null;

            var reach = Mathf.Min(interactionProximityDistance, interactionDistance);
            var neighbours = _interactionNeighbours;
            var neighbourCount = Physics.OverlapSphereNonAlloc(playerPosition, reach, neighbours,
                interactionMask, QueryTriggerInteraction.Collide);
            if (neighbourCount == neighbours.Length)
            {
                neighbours = Physics.OverlapSphere(playerPosition, reach, interactionMask, QueryTriggerInteraction.Collide);
                neighbourCount = neighbours.Length;
            }
            var bestDistance = float.PositiveInfinity;
            IPlayerInteractable best = null;
            for (var i = 0; i < neighbourCount; i++)
            {
                var collider = neighbours[i];
                if (IgnoreInteractionCollider(collider)) continue;
                var target = collider.GetComponentInParent<IPlayerInteractable>();
                if (target == null) continue;
                var point = collider.ClosestPoint(playerPosition);
                var squaredDistance = (point - playerPosition).sqrMagnitude;
                if (squaredDistance > reach * reach || squaredDistance >= bestDistance ||
                    !HasInteractionLineOfSight(ray.origin, point, target)) continue;
                best = target;
                bestDistance = squaredDistance;
            }
            return best;
        }

        private bool IgnoreInteractionCollider(Collider collider) => collider == null ||
            collider.GetComponentInParent<NetworkPlayerController>() == this;

        private RaycastHit[] GetInteractionRayHits(Ray ray, float distance, out int count)
        {
            count = Physics.RaycastNonAlloc(ray, _interactionHits, distance, interactionMask, QueryTriggerInteraction.Collide);
            if (count < _interactionHits.Length) return _interactionHits;
            var hits = Physics.RaycastAll(ray, distance, interactionMask, QueryTriggerInteraction.Collide);
            count = hits.Length;
            return hits;
        }

        private bool HasInteractionLineOfSight(Vector3 origin, Vector3 point, IPlayerInteractable target)
        {
            var delta = point - origin;
            var distance = delta.magnitude;
            if (distance <= 0.01f) return true;
            var hits = GetInteractionRayHits(new Ray(origin, delta / distance), distance, out var count);
            for (var i = 0; i < count; i++)
            {
                var collider = hits[i].collider;
                if (IgnoreInteractionCollider(collider) || collider.isTrigger ||
                    collider.GetComponentInParent<IPlayerInteractable>() != null) continue;
                // Ignore contact exactly at the target's surface, but reject an intervening wall.
                if (hits[i].distance < distance - 0.01f) return false;
            }
            return true;
        }

        private void UpdateItemActions()
        {
            if (Mouse.current == null)
                return;

            var equipmentAction = inventory.TryGetDefinition(inventory.SelectedIndex, out var heldItem) &&
                heldItem.EquipmentKind != ItemEquipmentKind.Carry;
            if (!equipmentAction && Mouse.current.leftButton.wasPressedThisFrame)
            {
               // animationSync.PlayAction("Primary");
                inventory.UseSelected(false);
            }
            else if (!equipmentAction && Mouse.current.rightButton.wasPressedThisFrame)
            {
               // animationSync.PlayAction("Special");
                inventory.UseSelected(true);
            }

            if (Keyboard.current != null && Keyboard.current.gKey.wasPressedThisFrame)
                inventory.DropSelected();
        }

        public void EnterHelm(ShipHelm helm)
        {
            if (!IsOwner || helm == null || IsAtControlStation || _pendingAnchor != null || _pendingCannon != null)
                return;

            _activeHelm = helm;
            ClearMovementInput();
            _airbornePlatformMomentum = Vector3.zero;
            var platform = helm.Ship.GetComponent<MovingPlatform>();
            if (platform != null)
                AttachToPlatform(platform);
            else
                _camera?.SetReferenceFrame(helm.Ship.transform);
            SetOwnerPhysicsSimulation(false);
            SnapToActiveControlStation();
            helm.Ship.RequestHelmServerRpc();
        }

        public void EnterSailControl(ShipSailControl sailControl)
        {
            if (!IsOwner || sailControl == null || IsAtControlStation || _pendingAnchor != null || _pendingCannon != null)
                return;

            _activeSailControl = sailControl;
            ClearMovementInput();
            _airbornePlatformMomentum = Vector3.zero;
            var platform = sailControl.Ship.GetComponent<MovingPlatform>();
            if (platform != null)
                AttachToPlatform(platform);
            else
                _camera?.SetReferenceFrame(sailControl.Ship.transform);

            SetOwnerPhysicsSimulation(false);
            SnapToActiveControlStation();
            sailControl.Ship.RequestSailControlServerRpc();
        }

        public void EnterMastControl(ShipMastControl mastControl)
        {
            if (!IsOwner || mastControl == null || IsAtControlStation || _pendingAnchor != null || _pendingCannon != null)
                return;

            _activeMastControl = mastControl;
            ClearMovementInput();
            _airbornePlatformMomentum = Vector3.zero;
            var platform = mastControl.Ship.GetComponent<MovingPlatform>();
            if (platform != null)
                AttachToPlatform(platform);
            else
                _camera?.SetReferenceFrame(mastControl.Ship.transform);

            SetOwnerPhysicsSimulation(false);
            SnapToActiveControlStation();
            mastControl.Ship.RequestMastControlServerRpc();
        }

        public void RequestAnchorHandle(ShipAnchor anchor)
        {
            if (!IsOwner || anchor == null || anchor.Ship == null || !anchor.Ship.IsSpawned ||
                IsAtControlStation || _pendingAnchor != null || _pendingCannon != null || _sceneTransitioning)
                return;
            _pendingAnchor = anchor;
            anchor.Ship.RequestAnchorHandleServerRpc();
        }

        public void HandleAnchorHandleResult(ShipAnchor anchor, int handleIndex)
        {
            if (!IsOwner)
                return;
            if (anchor == null || _pendingAnchor != anchor || _sceneTransitioning ||
                PlayerEquipment.InputCaptured || IsAtControlStation)
            {
                if (handleIndex >= 0 && anchor != null && anchor.Ship.IsSpawned)
                    anchor.Ship.ReleaseAnchorHandleServerRpc();
                _pendingAnchor = null;
                return;
            }
            _pendingAnchor = null;
            var station = anchor.GetHandleStation(handleIndex);
            if (station == null)
            {
                if (handleIndex >= 0 && anchor.Ship.IsSpawned)
                    anchor.Ship.ReleaseAnchorHandleServerRpc();
                return;
            }

            var initialLookRotation = _camera != null ? _camera.transform.rotation : station.rotation;
            BeginAnchorApproach(station);
            _activeAnchor = anchor;
            _anchorHandleIndex = handleIndex;
            _anchorOccupationConfirmed = false;
            _anchorAssignmentDeadline = Time.unscaledTime + 2f;
            _lastAnchorPush = false;
            _nextAnchorSend = 0f;
            ClearMovementInput();
            _airbornePlatformMomentum = Vector3.zero;
            var platform = anchor.Ship.GetComponent<MovingPlatform>();
            if (platform != null)
                AttachToPlatform(platform);
            _isGrounded = true;
            SetOwnerPhysicsSimulation(false);
            SnapToActiveControlStation();
            _camera?.SetReferenceFrame(anchor.Rotor);
            _camera?.SetLookRotation(initialLookRotation);
            _camera?.BlendLookRotation(station.rotation, anchorHandleApproachDuration);
        }

        private void UpdateAnchorInput()
        {
            var currentHandle = _activeAnchor.Ship.GetAnchorHandleForClient(OwnerClientId);
            if (currentHandle == _anchorHandleIndex)
                _anchorOccupationConfirmed = true;
            else if (_anchorOccupationConfirmed || Time.unscaledTime >= _anchorAssignmentDeadline)
            {
                LeaveAnchorHandle();
                return;
            }

            if (Keyboard.current.eKey.wasPressedThisFrame)
            {
                LeaveAnchorHandle();
                return;
            }

            SnapToActiveControlStation();
            var pushing = _loweringAnchor == null && !_activeAnchor.Ship.AnchorDropping &&
                          _activeAnchor.Ship.AnchorRaiseProgress < 1f && Keyboard.current.wKey.isPressed;
            if (pushing != _lastAnchorPush || Time.unscaledTime >= _nextAnchorSend)
            {
                _lastAnchorPush = pushing;
                _nextAnchorSend = Time.unscaledTime + 0.05f;
                _activeAnchor.Ship.SubmitAnchorPushServerRpc(pushing);
            }
            animationSync.SetLocomotion(pushing ? 1f : 0f, true, 0f);
        }

        public void RequestCannon(ShipCannon cannon)
        {
            if (!IsOwner || cannon == null || cannon.Battery == null || !cannon.Battery.IsSpawned ||
                IsAtControlStation || _pendingAnchor != null || _pendingCannon != null || _sceneTransitioning) return;
            _pendingCannon = cannon;
            _cannonRequestDeadline = Time.unscaledTime + 2f;
            cannon.Battery.RequestCannonServerRpc(cannon.Battery.GetCannonIndex(cannon));
        }

        public void HandleCannonAssignment(ShipCannonBattery battery, ShipCannon cannon)
        {
            if (!IsOwner) return;
            if (cannon == null) { _pendingCannon = null; return; }
            if (_pendingCannon != cannon || _sceneTransitioning || PlayerEquipment.InputCaptured || IsAtControlStation)
            {
                if (battery.IsSpawned) battery.ReleaseCannonServerRpc();
                _pendingCannon = null;
                return;
            }
            _pendingCannon = null;
            var look = _camera != null ? _camera.transform.rotation : cannon.transform.rotation;
            BeginAnchorApproach(cannon.Station);
            _activeCannon = cannon;
            _cannonControlActive = true;
            _cannonOccupationConfirmed = false;
            _cannonAssignmentDeadline = Time.unscaledTime + 2f;
            _nextCannonAimSend = 0f;
            ClearMovementInput();
            _airbornePlatformMomentum = Vector3.zero;
            var platform = battery.GetComponent<MovingPlatform>();
            if (platform != null) AttachToPlatform(platform);
            _isGrounded = true;
            SetOwnerPhysicsSimulation(false);
            SnapToActiveControlStation();
            var state = battery.GetState(battery.GetCannonIndex(cannon));
            _camera?.SetReferenceFrame(cannon.transform);
            _camera?.SetAimLimits(cannon.YawLimits, cannon.ElevationLimits, cannon.AimSpeed);
            _camera?.SetLookRotation(look);
            _camera?.BlendLookRotation(cannon.transform.rotation * Quaternion.Euler(-state.Elevation, state.Yaw, 0f),
                anchorHandleApproachDuration);
            cannon.SetLocalAim(new Vector2(state.Yaw, state.Elevation));
        }

        private void UpdateCannonInput()
        {
            var battery = _activeCannon.Battery;
            var state = battery.GetState(battery.GetCannonIndex(_activeCannon));
            if (state.Operator == OwnerClientId) _cannonOccupationConfirmed = true;
            else if (_cannonOccupationConfirmed || Time.unscaledTime >= _cannonAssignmentDeadline)
            { LeaveCannon(); return; }
            if (Keyboard.current.eKey.wasPressedThisFrame) { LeaveCannon(); return; }
            var aim = _camera != null ? _camera.AimAngles : new Vector2(state.Yaw, state.Elevation);
            _activeCannon.SetLocalAim(aim);
            if (Time.unscaledTime >= _nextCannonAimSend)
            {
                _nextCannonAimSend = Time.unscaledTime + 0.05f;
                battery.SubmitAimServerRpc(aim.x, aim.y);
            }
            if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
                battery.FireOrReloadServerRpc(inventory.SelectedIndex, inventory.SelectionRevision, aim.x, aim.y);
            SnapToActiveControlStation();
            animationSync.SetLocomotion(0f, true, 0f);
        }

        private void LeaveCannon(bool notifyServer = true)
        {
            var cannon = _activeCannon;
            _activeCannon = null;
            _cannonControlActive = false;
            _anchorApproachStation = null;
            cannon?.ReleaseLocalAim();
            _camera?.ClearAimLimits();
            if (notifyServer && cannon != null && cannon.Battery != null && cannon.Battery.IsSpawned)
                cannon.Battery.ReleaseCannonServerRpc();
            ClearMovementInput();
            if (_platform != null) AttachToPlatform(_platform);
            else _camera?.SetReferenceFrame(null);
            SetOwnerPhysicsSimulation(true, _platform != null ? _platform.GetPointVelocity(body.position) : Vector3.zero);
        }

        private void ResetCannonInteraction()
        {
            var cannon = _activeCannon != null ? _activeCannon : _pendingCannon;
            if (IsOwner && cannon != null && cannon.Battery != null && cannon.Battery.IsSpawned &&
                NetworkManager != null && NetworkManager.IsListening) cannon.Battery.ReleaseCannonServerRpc();
            cannon?.ReleaseLocalAim();
            _camera?.ClearAimLimits();
            _activeCannon = _pendingCannon = null;
            _cannonControlActive = false;
            _cannonOccupationConfirmed = false;
        }

        private void LeaveAnchorHandle(bool notifyServer = true)
        {
            var anchor = _activeAnchor;
            CancelAnchorLowerHold();
            if (notifyServer && anchor != null && anchor.Ship.IsSpawned)
                anchor.Ship.ReleaseAnchorHandleServerRpc();
            _activeAnchor = null;
            _anchorApproachStation = null;
            _anchorHandleIndex = -1;
            _anchorOccupationConfirmed = false;
            ClearMovementInput();
            if (_platform != null)
                AttachToPlatform(_platform);
            else
                _camera?.SetReferenceFrame(null);
            SetOwnerPhysicsSimulation(true, _platform != null
                ? _platform.GetPointVelocity(body.position) : Vector3.zero);
        }

        private void BeginAnchorLowerHold(ShipAnchor anchor)
        {
            if (IsAtControlStation || _pendingAnchor != null || anchor == null || anchor.Ship == null ||
                !anchor.Ship.IsSpawned || !anchor.Ship.CanBeginAnchorDrop)
                return;
            _loweringAnchor = anchor;
            _anchorLowerHoldStarted = Time.unscaledTime;
            _nextAnchorLowerHeartbeat = 0f;
            _anchorLowerCompleteSent = false;
            anchor.Ship.BeginAnchorLowerHoldServerRpc();
        }

        private void UpdateAnchorLowerHold()
        {
            var anchor = _loweringAnchor;
            if (anchor == null || anchor.Ship == null || !anchor.Ship.IsSpawned ||
                !anchor.Ship.CanBeginAnchorDrop || IsAtControlStation || _lookedAtAnchor != anchor)
            {
                CancelAnchorLowerHold();
                return;
            }
            if (!Keyboard.current.eKey.isPressed)
            {
                var shortPress = !_anchorLowerCompleteSent &&
                                 Time.unscaledTime - _anchorLowerHoldStarted <= 0.25f;
                CancelAnchorLowerHold();
                if (shortPress)
                    anchor.Interact(this);
                return;
            }

            if (Time.unscaledTime >= _nextAnchorLowerHeartbeat)
            {
                _nextAnchorLowerHeartbeat = Time.unscaledTime + 0.1f;
                anchor.Ship.RefreshAnchorLowerHoldServerRpc();
            }
            if (!_anchorLowerCompleteSent && AnchorReleaseHoldProgress >= 1f)
            {
                _anchorLowerCompleteSent = true;
                anchor.Ship.CompleteAnchorLowerHoldServerRpc();
            }
        }

        private void CancelAnchorLowerHold()
        {
            if (_loweringAnchor != null && _loweringAnchor.Ship != null &&
                _loweringAnchor.Ship.IsSpawned && NetworkManager != null && NetworkManager.IsListening)
                _loweringAnchor.Ship.CancelAnchorLowerHoldServerRpc();
            _loweringAnchor = null;
        }

        private void ResetAnchorInteraction()
        {
            ResetCannonInteraction();
            _anchorApproachStation = null;
            CancelAnchorLowerHold();
            var anchor = _activeAnchor != null ? _activeAnchor : _pendingAnchor;
            if (IsOwner && anchor != null && anchor.Ship != null && anchor.Ship.IsSpawned &&
                NetworkManager != null && NetworkManager.IsListening)
                anchor.Ship.ReleaseAnchorHandleServerRpc();
            _activeAnchor = null;
            _pendingAnchor = null;
            _lookedAtAnchor = null;
            _anchorHandleIndex = -1;
            _anchorOccupationConfirmed = false;
        }

        private void UpdateHelmInput()
        {
            if (Keyboard.current.eKey.wasPressedThisFrame)
            {
                var helm = _activeHelm;
                helm.Ship.ReleaseHelmServerRpc();
                _activeHelm = null;
                var platform = helm.Ship.GetComponent<MovingPlatform>();
                if (platform != null)
                    AttachToPlatform(platform);
                else
                    _camera?.SetReferenceFrame(null);
                SetOwnerPhysicsSimulation(true, platform != null
                    ? platform.GetPointVelocity(body.position)
                    : Vector3.zero);
                return;
            }

            SnapToActiveControlStation();

            if (Time.unscaledTime < _nextHelmSend)
                return;

            _nextHelmSend = Time.unscaledTime + 0.05f;
            var steer = (Keyboard.current.dKey.isPressed ? 1f : 0f) - (Keyboard.current.aKey.isPressed ? 1f : 0f);
            _activeHelm.Ship.SubmitHelmInputServerRpc(steer);
            animationSync.SetLocomotion(0f, true, 0f);
        }

        private void UpdateSailControlInput()
        {
            if (Keyboard.current.eKey.wasPressedThisFrame)
            {
                var sailControl = _activeSailControl;
                sailControl.Ship.ReleaseSailControlServerRpc();
                _activeSailControl = null;
                var platform = sailControl.Ship.GetComponent<MovingPlatform>();
                if (platform != null)
                    AttachToPlatform(platform);
                else
                    _camera?.SetReferenceFrame(null);
                SetOwnerPhysicsSimulation(true, platform != null
                    ? platform.GetPointVelocity(body.position)
                    : Vector3.zero);
                return;
            }

            SnapToActiveControlStation();

            if (Time.unscaledTime < _nextSailSend)
                return;

            _nextSailSend = Time.unscaledTime + 0.05f;
            var sailInput = (Keyboard.current.sKey.isPressed ? 1f : 0f) -
                            (Keyboard.current.wKey.isPressed ? 1f : 0f);
            _activeSailControl.Ship.SubmitSailInputServerRpc(sailInput);
            animationSync.SetLocomotion(0f, true, 0f);
        }

        private void UpdateMastControlInput()
        {
            if (Keyboard.current.eKey.wasPressedThisFrame)
            {
                var mastControl = _activeMastControl;
                mastControl.Ship.ReleaseMastControlServerRpc();
                _activeMastControl = null;
                var platform = mastControl.Ship.GetComponent<MovingPlatform>();
                if (platform != null)
                    AttachToPlatform(platform);
                else
                    _camera?.SetReferenceFrame(null);
                SetOwnerPhysicsSimulation(true, platform != null
                    ? platform.GetPointVelocity(body.position)
                    : Vector3.zero);
                return;
            }

            SnapToActiveControlStation();

            if (Time.unscaledTime < _nextMastSend)
                return;

            _nextMastSend = Time.unscaledTime + 0.05f;
            var mastInput = (Keyboard.current.dKey.isPressed ? 1f : 0f) -
                            (Keyboard.current.aKey.isPressed ? 1f : 0f);
            _activeMastControl.Ship.SubmitMastInputServerRpc(mastInput);
            animationSync.SetLocomotion(0f, true, 0f);
        }

        private bool SnapToActiveControlStation()
        {
            Transform station = null;
            if (_activeHelm != null)
                station = _activeHelm.Station;
            else if (_activeSailControl != null)
                station = _activeSailControl.Station;
            else if (_activeMastControl != null)
                station = _activeMastControl.Station;
            else if (_activeAnchor != null)
                station = _activeAnchor.GetHandleStation(_anchorHandleIndex);
            else if (_activeCannon != null)
                station = _activeCannon.Station;
            else if (_activeCustomizationStation != null)
                station = _activeCustomizationStation.Station;

            if (station == null)
                return false;

            var position = station.position;
            var rotation = station.rotation;
            if (_activeAnchor != null || _activeCannon != null || _activeCustomizationStation != null)
                GetAnchorApproachPose(station, out position, out rotation);
            SetBodyPose(position, rotation);
            _desiredBodyRotation = rotation;
            UpdatePlatformAnchorFromCurrentPose();
            ResetPresentationPose();
            return true;
        }

        private void BeginAnchorApproach(Transform station)
        {
            _anchorApproachStation = station;
            _anchorApproachStarted = Time.unscaledTime;
            var presented = _presentationRoot != null ? _presentationRoot : transform;
            _anchorApproachPositionOffset = station.InverseTransformPoint(presented.position);
            _anchorApproachRotationOffset = Quaternion.Inverse(station.rotation) * presented.rotation;
        }

        public void ToggleCustomization(CustomizationStation station)
        {
            if (!IsOwner || station == null || _sceneTransitioning)
                return;
            if (_activeCustomizationStation == station)
            {
                ExitCustomization();
                return;
            }
            if (IsAtControlStation || _pendingAnchor != null || _pendingCannon != null)
                return;

            var appearance = GetComponent<NetworkPirateAppearance>();
            if (appearance == null)
                return;
            _activeCustomizationStation = station;
            ClearMovementInput();
            BeginAnchorApproach(station.Station);
            SetOwnerPhysicsSimulation(false);
            _camera?.SetReferenceFrame(null);
            _camera?.SetExternalView(station.ViewCamera);
            _customizationMenu = gameObject.AddComponent<CustomizationMenu>();
            _customizationMenu.Initialize(this, appearance);
        }

        public void ExitCustomization()
        {
            if (_activeCustomizationStation == null && _customizationMenu == null)
                return;
            _activeCustomizationStation = null;
            _anchorApproachStation = null;
            if (_customizationMenu != null)
                _customizationMenu.CloseSilently();
            _customizationMenu = null;
            _camera?.ClearExternalPose();
            if (IsOwner && IsSpawned && !_sceneTransitioning && (_health == null || !_health.IsDead))
                SetOwnerPhysicsSimulation(true);
        }

        private void UpdateCustomization()
        {
            if (_activeCustomizationStation == null)
            {
                ExitCustomization();
                return;
            }
            ClearMovementInput();
            SnapToActiveControlStation();
            animationSync.SetLocomotion(0f, true, 0f);
            if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
                ExitCustomization();
        }

        private void GetAnchorApproachPose(Transform station, out Vector3 position, out Quaternion rotation)
        {
            if (_anchorApproachStation != station)
                BeginAnchorApproach(station);
            var progress = anchorHandleApproachDuration > 0f
                ? Mathf.Clamp01((Time.unscaledTime - _anchorApproachStarted) / anchorHandleApproachDuration)
                : 1f;
            var blend = Mathf.SmoothStep(0f, 1f, progress);
            // Only the initial offset fades out. Ship motion and capstan rotation
            // are composed directly every frame, without a second world-space lag.
            position = station.TransformPoint(_anchorApproachPositionOffset * (1f - blend));
            rotation = station.rotation * Quaternion.Slerp(_anchorApproachRotationOffset, Quaternion.identity, blend);
        }

        private void UpdatePlatformAnchorFromCurrentPose()
        {
            if (_platform != null)
                _platformLocalAnchor = _platform.transform.InverseTransformPoint(body.position);
        }

        private void SetBodyPose(Vector3 position, Quaternion rotation)
        {
            body.position = position;
            body.rotation = rotation;
            transform.SetPositionAndRotation(position, rotation);
            if (_useKccMotor && _kccMotor != null)
                _kccMotor.SetPositionAndRotation(position, rotation);
        }

        private void SetOwnerPhysicsSimulation(bool enabled, Vector3 initialVelocity = default)
        {
            if (!IsOwner || body == null || _kccMotor == null || !_useKccMotor)
                return;

            if (!enabled)
            {
                _kccMotor.BaseVelocity = Vector3.zero;
                _kccMotor.AttachedRigidbodyOverride = null;
                _kccMotor.enabled = false;
                body.isKinematic = true;
                bodyCollider.enabled = false;
                ResetClientPlatformFrame();
                return;
            }

            var currentRotation = transform.rotation;
            var kccPlanarForward = Vector3.ProjectOnPlane(
                currentRotation * Vector3.forward, Vector3.up);
            if (kccPlanarForward.sqrMagnitude < 0.0001f)
                kccPlanarForward = Vector3.forward;
            _desiredBodyRotation = Quaternion.LookRotation(kccPlanarForward.normalized, Vector3.up);
            SetBodyPose(transform.position, _desiredBodyRotation);
            body.isKinematic = true;
            body.interpolation = RigidbodyInterpolation.None;
            bodyCollider.enabled = true;
            _kccMotor.CharacterController = this;
            _kccMotor.BaseVelocity = _platform != null ? Vector3.zero : initialVelocity;
            _kccMotor.enabled = true;
        }

        public void PrepareForSceneTransitionLocally()
        {
            if (!IsOwner)
                return;

            ExitCustomization();
            _sceneTransitioning = true;
            StopShipControlInputsForMenu();
            ResetAnchorInteraction();
            RemoveAnyNetworkParent();
            _activeHelm = null;
            _activeSailControl = null;
            _activeMastControl = null;
            _platform = null;
            _airborneFromPlatform = false;
            ResetClientPlatformFrame();
            _airbornePlatformMomentum = Vector3.zero;
            _isGrounded = false;
            _hasReplicatedPlatform.Value = false;
            _camera?.SetReferenceFrame(null);
            SetOwnerPhysicsSimulation(false);
            ResetPresentationPose();
        }

        public void RemoveNetworkParentOnServer()
        {
            if (!IsServer || !IsSpawned || transform.parent == null)
                return;

            NetworkObject.TryRemoveParent(true);
        }

        public void CancelSceneTransitionLocally()
        {
            if (!IsOwner || !IsSpawned)
                return;

            _sceneTransitioning = false;
            SetOwnerPhysicsSimulation(true);
        }

        private void RemoveAnyNetworkParent()
        {
            if (!IsOwner || !IsSpawned || transform.parent == null)
                return;

            NetworkObject.TryRemoveParent(true);
        }

        [ClientRpc]
        public void TeleportOwnerClientRpc(
            int targetSceneBuildIndex,
            Vector3 worldPosition,
            Quaternion worldRotation,
            bool usePlatformSpace,
            NetworkObjectReference platformReference,
            Vector3 platformLocalPosition,
            Quaternion platformLocalRotation,
            ClientRpcParams rpcParams = default)
        {
            if (!IsOwner)
                return;

            var revision = ++_placementRevision;
            StartCoroutine(ApplyPlacementWhenReady(
                revision,
                targetSceneBuildIndex,
                worldPosition,
                worldRotation,
                usePlatformSpace,
                platformReference,
                platformLocalPosition,
                platformLocalRotation));
        }

        private IEnumerator ApplyPlacementWhenReady(
            int revision,
            int targetSceneBuildIndex,
            Vector3 worldPosition,
            Quaternion worldRotation,
            bool usePlatformSpace,
            NetworkObjectReference platformReference,
            Vector3 platformLocalPosition,
            Quaternion platformLocalRotation)
        {
            while (IsSpawned && revision == _placementRevision &&
                   SceneManager.GetActiveScene().buildIndex != targetSceneBuildIndex)
                yield return null;

            NetworkObject platformObject = null;
            if (usePlatformSpace)
            {
                while (IsSpawned && revision == _placementRevision &&
                       !platformReference.TryGet(out platformObject, NetworkManager))
                    yield return null;
            }

            if (!IsSpawned || revision != _placementRevision)
                yield break;

            ApplyOwnerPlacement(
                worldPosition,
                worldRotation,
                usePlatformSpace ? platformObject : null,
                platformLocalPosition,
                platformLocalRotation);
        }

        private void ApplyOwnerPlacement(
            Vector3 worldPosition,
            Quaternion worldRotation,
            NetworkObject platformObject,
            Vector3 platformLocalPosition,
            Quaternion platformLocalRotation)
        {
            RemoveAnyNetworkParent();
            SetOwnerPhysicsSimulation(false);
            ResetAnchorInteraction();

            _activeHelm = null;
            _activeSailControl = null;
            _activeMastControl = null;
            _platform = null;
            _airborneFromPlatform = false;
            ResetClientPlatformFrame();
            _airbornePlatformMomentum = Vector3.zero;
            _isGrounded = false;
            ClearMovementInput();

            if (platformObject != null && platformObject.IsSpawned &&
                platformObject.GetComponentInChildren<MovingPlatform>(true) is { } platform)
            {
                var resolvedWorldPosition = platformObject.transform.TransformPoint(platformLocalPosition);
                var resolvedWorldRotation = platformObject.transform.rotation * platformLocalRotation;
                SetBodyPose(resolvedWorldPosition, resolvedWorldRotation);

                _platform = platform;
                _platformLocalAnchor = platform.transform.InverseTransformPoint(resolvedWorldPosition);
                _platformAnchorLocked = true;
                _lastPlatformContactTime = Time.fixedTime;
                if (platform.UsesInterpolatedNetworkMotion)
                    InitializeClientPlatformFrame(platform.transform);
                _networkTransform?.Teleport(resolvedWorldPosition, resolvedWorldRotation, transform.localScale);
                _camera?.SetReferenceFrame(platform.transform);
            }
            else
            {
                SetBodyPose(worldPosition, worldRotation);
                _networkTransform?.Teleport(worldPosition, worldRotation, transform.localScale);
                _hasReplicatedPlatform.Value = false;
                _camera?.SetReferenceFrame(null);
            }

            _desiredBodyRotation = body.rotation;
            _sceneTransitioning = false;
            SetOwnerPhysicsSimulation(true, _platform != null
                ? _platform.GetPointVelocity(body.position)
                : Vector3.zero);
            ResetPresentationPose();
            PublishPlatformPose();
            _camera?.RefreshPose();
        }

        public override void OnNetworkDespawn()
        {
            ExitCustomization();
            ResetAnchorInteraction();
            RestoreOwnerBodyLayers();
            if (_kccMotor != null)
            {
                _kccMotor.AttachedRigidbodyOverride = null;
                _kccMotor.CharacterController = null;
                _kccMotor.enabled = false;
            }
            _useKccMotor = false;
            if (bodyCollider != null)
                bodyCollider.enabled = false;
            if (body != null)
                body.isKinematic = true;
            _platform = null;
            _airborneFromPlatform = false;
            ResetClientPlatformFrame();
            _activeHelm = null;
            _activeSailControl = null;
            _activeMastControl = null;
            _airbornePlatformMomentum = Vector3.zero;
            _isGrounded = false;
            ClearMovementInput();
            _menuWasOpen = false;
            _sceneTransitioning = false;
            _placementRevision++;
            ResetRemotePlatformPose();
            _camera?.SetReferenceFrame(null);
            if (ownerCamera != null)
                ownerCamera.gameObject.SetActive(false);
            _camera = null;
        }
    }
}
