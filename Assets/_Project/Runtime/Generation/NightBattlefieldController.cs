using Unity.Netcode;
using UnityEngine;
using WaveByWave.Ships;

namespace WaveByWave.Generation
{
    // One ship, sampled a few times per second. Returning inside resets grace
    // immediately; a frame hitch cannot create a burst of hull breaches.
    public sealed class BattlefieldBoundaryBreachTimer
    {
        private float _outsideSince = -1f;
        private float _nextBreachAt = -1f;
        public void Reset() { _outsideSince = -1f; _nextBreachAt = -1f; }
        public bool Step(float now, bool outside, float graceSeconds, float breachInterval)
        {
            if (!outside) { Reset(); return false; }
            if (_outsideSince < 0f)
            {
                _outsideSince = now;
                _nextBreachAt = now + Mathf.Max(0f, graceSeconds);
            }
            if (now < _nextBreachAt) return false;
            _nextBreachAt = now + Mathf.Max(0.5f, breachInterval);
            return true;
        }
    }

    // Presentation outlives the gameplay boundary and fades from the last visible opacity.
    public sealed class BattlefieldFogFade
    {
        private bool _wasActive;
        private float _fadeOutStarted, _fadeOutFrom;
        public float Opacity { get; private set; }

        public void Step(bool active, float age, float now, float fadeInSeconds, float fadeOutSeconds)
        {
            if (active)
            {
                _wasActive = true;
                Opacity = fadeInSeconds <= 0f ? 1f : Mathf.SmoothStep(0f, 1f, age / fadeInSeconds);
                return;
            }
            if (_wasActive)
            {
                _wasActive = false;
                _fadeOutStarted = now;
                _fadeOutFrom = Opacity;
            }
            Opacity = fadeOutSeconds <= 0f ? 0f : _fadeOutFrom *
                (1f - Mathf.SmoothStep(0f, 1f, (now - _fadeOutStarted) / fadeOutSeconds));
        }

        public void Reset() { _wasActive = false; _fadeOutFrom = 0f; Opacity = 0f; }
    }

    /// <summary>
    /// Owns the battlefield boundary, its damage and fog presentation. Wave logic only
    /// starts and stops this system, so fog tuning remains independent from wave content.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class NightBattlefieldController : MonoBehaviour
    {
        public static NightBattlefieldController Active { get; private set; }
        public static bool BattleInProgress => Active != null && Active._battery != null &&
            Active._battery.IsSpawned && Active._battery.BattlefieldActive;

        [SerializeField] private NightBattlefieldSettings settings;
        private ShipCannonBattery _battery;
        private ShipHullHealth _hull;
        private readonly BattlefieldBoundaryBreachTimer _boundaryBreaches = new();
        private readonly BattlefieldFogFade _fogFade = new();
        private Vector4 _fogZone;
        private float _nextBoundaryCheck;
        private float _localOutsideSince = -1f;

        public static NightBattlefieldController EnsureInstance(GameObject host)
        {
            if (Active != null) return Active;
            var controller = host.GetComponent<NightBattlefieldController>();
            return controller != null ? controller : host.AddComponent<NightBattlefieldController>();
        }

        public static bool TryGetBattlefield(out Vector4 centerRadius, out NightBattlefieldSettings battlefieldSettings)
        {
            centerRadius = default;
            battlefieldSettings = null;
            var controller = Active;
            if (controller == null || controller._battery == null || !controller._battery.IsSpawned ||
                !controller._battery.BattlefieldActive || controller.settings == null) return false;
            var center = controller._battery.BattlefieldCenter;
            centerRadius = new Vector4(center.x, center.y, center.z, controller._battery.BattlefieldRadius);
            battlefieldSettings = controller.settings;
            return true;
        }

        public static bool TryGetFog(out Vector4 centerRadius, out NightBattlefieldSettings battlefieldSettings,
            out float opacity)
        {
            var controller = Active;
            centerRadius = controller != null ? controller._fogZone : default;
            battlefieldSettings = controller != null ? controller.settings : null;
            opacity = controller != null ? controller._fogFade.Opacity : 0f;
            return battlefieldSettings != null && centerRadius.w > 0f && opacity > 0f;
        }

        private void OnEnable()
        {
            Active = this;
            settings ??= Resources.Load<NightBattlefieldSettings>("NightBattlefieldSettings");
        }

        private void OnDisable()
        {
            _fogFade.Reset();
            _fogZone = default;
            _boundaryBreaches.Reset();
            if (Active == this) Active = null;
        }

        private void Update()
        {
            ResolveBattery();
            UpdateBoundaryWarning();
            if (settings == null || _battery == null || !_battery.IsSpawned ||
                !_battery.BattlefieldActive || _battery.UpgradePaused || _battery.VoyageEnded ||
                NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer ||
                Time.time < _nextBoundaryCheck) return;
            _nextBoundaryCheck = Time.time + settings.BoundaryCheckInterval;
            var outside = !_battery.IsInsideBattlefield(_battery.transform.position);
            if (_boundaryBreaches.Step(Time.time, outside,
                settings.BoundaryGraceSeconds, settings.BoundaryBreachInterval))
                _hull?.OpenBoundaryBreachServer(settings.BoundaryBreachLeakMultiplier);
        }

        private void LateUpdate()
        {
            if (settings == null) return;
            var active = TryGetBattlefield(out var zone, out _);
            if (active) _fogZone = zone;
            _fogFade.Step(active, active ? _battery.BattlefieldAge : 0f, Time.unscaledTime,
                settings.FogFadeInSeconds, settings.FogFadeOutSeconds);
        }

        public void BeginServer(ShipCannonBattery battery, Vector3 center, float radius)
        {
            _battery = battery;
            if (_battery == null) return;
            _hull = _battery.GetComponent<ShipHullHealth>();
            _battery.SetBattlefieldServer(center, radius);
            _boundaryBreaches.Reset();
            _nextBoundaryCheck = Time.time;
        }

        public void EndServer()
        {
            _battery?.ClearBattlefieldServer();
            _boundaryBreaches.Reset();
            _localOutsideSince = -1f;
        }

        private void ResolveBattery()
        {
            if (_battery == null || !_battery.IsSpawned)
            {
                _battery = FindFirstObjectByType<ShipCannonBattery>();
                _hull = _battery != null ? _battery.GetComponent<ShipHullHealth>() : null;
            }
        }

        private void UpdateBoundaryWarning()
        {
            if (_battery == null || !_battery.IsSpawned || !_battery.BattlefieldActive)
            { _localOutsideSince = -1f; return; }
            if (_battery.IsInsideBattlefield(_battery.transform.position))
            { _localOutsideSince = -1f; return; }
            if (_localOutsideSince < 0f) _localOutsideSince = Time.time;
        }

        public string BoundaryWarning
        {
            get
            {
                if (_localOutsideSince < 0f || settings == null) return "";
                var grace = Mathf.Max(0f, settings.BoundaryGraceSeconds - (Time.time - _localOutsideSince));
                return grace > 0f ? $"ВЕРНИТЕСЬ В ПОЛЕ БОЯ · УРОН ЧЕРЕЗ {Mathf.CeilToInt(grace)} С" :
                    "ВЕРНИТЕСЬ В ПОЛЕ БОЯ · ПОЯВЛЯЮТСЯ ПРОБОИНЫ";
            }
        }
    }
}
