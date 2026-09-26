using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using WaveByWave.Networking;
using WaveByWave.Player;

namespace WaveByWave.Combat
{
    /// <summary>
    /// Server-authoritative health, damage feedback, death and respawn shared by players and creatures.
    /// Equipment calls ReceiveEquipmentHitServer; other server systems can call ApplyDamageServer directly.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject))]
    public sealed class NetworkHealth : NetworkBehaviour, IEquipmentDamageReceiver
    {
        [Header("Health")]
        [SerializeField, Min(1f)] private float maximumHealth = 100f;
        [SerializeField, Min(0f)] private float respawnDelay = 3f;
        [SerializeField] private bool respawnAfterDeath = true;

        [Header("Damage response")]
        [SerializeField, Min(0f)] private float knockbackSpeed = 7f;
        [SerializeField, Min(0f)] private float knockbackLift = 2.5f;
        [SerializeField, Min(0.01f)] private float flashDuration = 0.12f;
        [Tooltip("Optional per-prefab override. If empty, Resources/DamageFlash is used.")]
        [SerializeField] private Material damageFlashMaterial;

        [Header("Presentation")]
        [SerializeField] private Transform visualRoot;
        [SerializeField, Tooltip("Prefab белой пыли смерти. Визуал и ParticleSystem настраиваются только в prefab.")]
        private GameObject deathDustPrefab;
        [SerializeField, Min(0.25f)] private float worldBarHeight = 2.15f;

        private readonly NetworkVariable<float> _health = new(100f);
        private readonly NetworkVariable<bool> _dead = new(false);
        private readonly List<RendererState> _renderers = new();
        private readonly List<Collider> _creatureColliders = new();

        private NetworkPlayerController _player;
        private Rigidbody _body;
        private HealthBarView _healthBar;
        private Coroutine _flashRoutine;
        private Vector3 _spawnPosition;
        private Quaternion _spawnRotation;
        private double _respawnAt;

        public event Action<float, float> HealthChanged;
        public event Action Died;
        public event Action Respawned;

        public float CurrentHealth => _health.Value;
        public float MaximumHealth => maximumHealth + (_player != null
            ? _player.RingValue(PlayerRingStat.MaximumHealth) : 0f);
        public float NormalizedHealth => MaximumHealth > 0f ? Mathf.Clamp01(_health.Value / MaximumHealth) : 0f;
        public bool IsDead => _dead.Value;
        public void DisableWaveRespawnServer()
        {
            if (IsServer) respawnAfterDeath = false;
        }

        public void ApplyRingHealthBonusServer(float bonus)
            => ApplyRingHealthChangeServer(bonus);

        public void ApplyRingHealthChangeServer(float difference)
        {
            if (!IsServer || _dead.Value) return;
            _health.Value = Mathf.Min(MaximumHealth,
                difference > 0f ? _health.Value + difference : _health.Value);
        }

        private sealed class RendererState
        {
            public Renderer Renderer;
            public bool WasEnabled;
            public Material[] FlashRestoreMaterials;
        }

        private static Material _defaultFlashMaterial;

        private void Awake()
        {
            _player = GetComponent<NetworkPlayerController>();
            _body = GetComponent<Rigidbody>();
            visualRoot ??= transform.Find("Visual");
            CachePresentation();
        }

        public override void OnNetworkSpawn()
        {
            _health.OnValueChanged += OnHealthChanged;
            _dead.OnValueChanged += OnDeadChanged;

            if (IsServer)
            {
                maximumHealth = Mathf.Max(1f, maximumHealth);
                _health.Value = MaximumHealth;
                _dead.Value = false;
                _spawnPosition = transform.position;
                _spawnRotation = transform.rotation;
            }

            if (IsClient)
                _healthBar = HealthBarView.Create(this, _player != null && IsOwner, worldBarHeight);

            ApplyDeadPresentation(_dead.Value, false);
            NotifyHealthChanged();
        }

        public override void OnNetworkDespawn()
        {
            _health.OnValueChanged -= OnHealthChanged;
            _dead.OnValueChanged -= OnDeadChanged;
            if (_flashRoutine != null)
            {
                StopCoroutine(_flashRoutine);
                RestoreFlashMaterials();
            }
            if (_healthBar != null)
                _healthBar.Detach(this);
            _healthBar = null;
        }

        private void Update()
        {
            if (IsServer && _dead.Value && respawnAfterDeath && NetworkManager.ServerTime.Time >= _respawnAt)
                RespawnServer();
        }

        public void ReceiveEquipmentHitServer(float damage, Vector3 attackerPosition, bool canBlock = true, Vector3? impactPoint = null)
        {
            ApplyDamageServer(damage, attackerPosition, 1f, impactPoint);
        }

        public bool ApplyDamageServer(float amount, Vector3 sourcePosition, float knockbackMultiplier = 1f, Vector3? impactPoint = null)
        {
            if (!IsServer || _dead.Value || !IsFinite(amount) || amount <= 0f || !IsFinite(sourcePosition))
                return false;

            var previous = _health.Value;
            _health.Value = Mathf.Max(0f, previous - amount);
            if (Mathf.Approximately(previous, _health.Value))
                return false;

            DotsDamagePopups.ReportServer(impactPoint ?? GetEffectCenter(), amount);
            var away = Vector3.ProjectOnPlane(transform.position - sourcePosition, Vector3.up);
            if (away.sqrMagnitude < 0.0001f)
                away = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
            if (away.sqrMagnitude < 0.0001f)
                away = Vector3.forward;
            var impulse = away.normalized * knockbackSpeed * Mathf.Max(0f, knockbackMultiplier) +
                          Vector3.up * knockbackLift * Mathf.Max(0f, knockbackMultiplier);

            if (_player == null && _body != null && !_body.isKinematic)
                _body.AddForce(impulse, ForceMode.VelocityChange);

            DamageFeedbackClientRpc(impulse);
            if (_health.Value <= 0f)
                DieServer();
            return true;
        }

        public bool HealServer(float amount)
        {
            if (!IsServer || _dead.Value || !IsFinite(amount) || amount <= 0f || _health.Value >= MaximumHealth)
                return false;
            _health.Value = Mathf.Min(MaximumHealth, _health.Value + amount);
            return true;
        }

        [ServerRpc(RequireOwnership = false)]
        public void RequestRespawnServerRpc(ServerRpcParams rpc = default)
        {
            if (!IsServer || !_dead.Value || rpc.Receive.SenderClientId != OwnerClientId ||
                NetworkManager.ServerTime.Time < _respawnAt)
                return;
            RespawnServer();
        }

        private void DieServer()
        {
            if (!IsServer || _dead.Value)
                return;
            _health.Value = 0f;
            _dead.Value = true;
            _respawnAt = NetworkManager.ServerTime.Time + respawnDelay;
        }

        private void RespawnServer()
        {
            if (!IsServer || !_dead.Value)
                return;

            if (_player != null)
            {
                var director = FindFirstObjectByType<NetworkSpawnDirector>();
                if (director != null)
                    director.RespawnPlayerServer(_player);
                else
                    transform.SetPositionAndRotation(_spawnPosition, _spawnRotation);
            }
            else
            {
                transform.SetPositionAndRotation(_spawnPosition, _spawnRotation);
                if (_body != null)
                {
                    _body.linearVelocity = Vector3.zero;
                    _body.angularVelocity = Vector3.zero;
                }
            }

            _health.Value = MaximumHealth;
            _dead.Value = false;
        }

        [ClientRpc]
        private void DamageFeedbackClientRpc(Vector3 knockback)
        {
            PlayFlash();
            _healthBar?.Flash();
            if (_player != null && IsOwner)
                _player.ApplyDamageKnockback(knockback);
        }

        private void OnHealthChanged(float previous, float current)
        {
            NotifyHealthChanged();
        }

        private void OnDeadChanged(bool previous, bool current)
        {
            ApplyDeadPresentation(current, current && !previous);
            if (current)
                Died?.Invoke();
            else if (previous)
                Respawned?.Invoke();
        }

        private void NotifyHealthChanged()
        {
            _healthBar?.SetValue(NormalizedHealth, _health.Value, MaximumHealth);
            HealthChanged?.Invoke(_health.Value, MaximumHealth);
        }

        private void ApplyDeadPresentation(bool dead, bool playEffect)
        {
            if (playEffect && IsClient)
            {
                if (deathDustPrefab != null)
                {
                    var effect = Instantiate(deathDustPrefab, GetEffectCenter(), Quaternion.identity);
                    if (effect.TryGetComponent<DeathDustBurst>(out var burst)) burst.Play(GetEffectScale());
                    else Destroy(effect, 2f);
                }
                else Debug.LogError("NetworkHealth requires a death dust prefab.", this);
            }

            foreach (var state in _renderers)
            {
                if (state.Renderer != null)
                    state.Renderer.enabled = dead ? false : state.WasEnabled;
            }

            // The player controller owns its KCC/capsule state. Generic creatures use their
            // regular colliders, which are disabled while dead on every peer.
            if (_player == null)
                foreach (var collider in _creatureColliders)
                    if (collider != null)
                        collider.enabled = !dead;

            _player?.SetDamageAliveState(!dead);
            _healthBar?.SetDead(dead);
        }

        private void CachePresentation()
        {
            _renderers.Clear();
            _creatureColliders.Clear();
            var root = visualRoot != null ? visualRoot : transform;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                var materials = renderer.sharedMaterials;
                var state = new RendererState
                {
                    Renderer = renderer,
                    WasEnabled = renderer.enabled
                };
                _renderers.Add(state);
            }

            if (_player == null)
                _creatureColliders.AddRange(GetComponentsInChildren<Collider>(true));
        }

        public void RefreshPresentationRenderers()
        {
            if (_flashRoutine != null)
            {
                StopCoroutine(_flashRoutine);
                RestoreFlashMaterials();
                _flashRoutine = null;
            }
            CachePresentation();
            if (IsSpawned)
                ApplyDeadPresentation(_dead.Value, false);
        }

        private void PlayFlash()
        {
            if (_flashRoutine != null)
            {
                StopCoroutine(_flashRoutine);
                RestoreFlashMaterials();
            }
            _flashRoutine = StartCoroutine(FlashRoutine());
        }

        private IEnumerator FlashRoutine()
        {
            var white = GetWhiteFlashMaterial();
            foreach (var state in _renderers)
            {
                if (state.Renderer == null || white == null)
                    continue;
                state.FlashRestoreMaterials = state.Renderer.sharedMaterials;
                var flashMaterials = new Material[state.FlashRestoreMaterials.Length];
                for (var i = 0; i < flashMaterials.Length; i++)
                    flashMaterials[i] = white;
                state.Renderer.sharedMaterials = flashMaterials;
            }

            yield return new WaitForSecondsRealtime(flashDuration);
            RestoreFlashMaterials();
            _flashRoutine = null;
        }

        private void RestoreFlashMaterials()
        {
            foreach (var state in _renderers)
            {
                if (state.Renderer != null && state.FlashRestoreMaterials != null)
                    state.Renderer.sharedMaterials = state.FlashRestoreMaterials;
                state.FlashRestoreMaterials = null;
            }
        }

        private Material GetWhiteFlashMaterial()
        {
            if (damageFlashMaterial != null)
                return damageFlashMaterial;
            if (_defaultFlashMaterial == null)
                _defaultFlashMaterial = Resources.Load<Material>("DamageFlash");
            return _defaultFlashMaterial;
        }

        private Vector3 GetEffectCenter()
        {
            var bounds = new Bounds(transform.position + Vector3.up, Vector3.one);
            var found = false;
            foreach (var state in _renderers)
            {
                if (state.Renderer == null)
                    continue;
                if (!found) bounds = state.Renderer.bounds;
                else bounds.Encapsulate(state.Renderer.bounds);
                found = true;
            }
            return bounds.center;
        }

        private float GetEffectScale()
        {
            var scale = visualRoot != null ? visualRoot.lossyScale : transform.lossyScale;
            return Mathf.Max(0.5f, Mathf.Max(scale.x, Mathf.Max(scale.y, scale.z)));
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool IsFinite(Vector3 value) => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
    }
}
