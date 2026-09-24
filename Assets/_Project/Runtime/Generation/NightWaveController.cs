using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using WaveByWave.Combat;
using WaveByWave.Enemies;
using WaveByWave.Networking;
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

    // Gameplay waves depend on night notifications; the lighting clock never depends on waves.
    public sealed class NightWaveController : MonoBehaviour
    {
        public static NightWaveController Active { get; private set; }
        public static bool BattleInProgress => Active != null && Active._battery != null &&
            Active._battery.IsSpawned && Active._battery.BattlefieldActive;
        [SerializeField] private NightWaveSettings settings;
        [SerializeField] private VoyageDayNightController dayNight;
        [SerializeField] private Transform[] waveSpawnPoints;

        private ShipCannonBattery _battery;
        private readonly List<NetworkHealth> _wavePrefabs = new();
        private int _waveIndex, _waveGroup;
        private bool _waveActive, _victoryRequested;
        private bool _waveNumberPublished;
        private float _nextWaveCheck;
        private float _nextBoundaryCheck;
        private readonly BattlefieldBoundaryBreachTimer _boundaryBreaches = new();
        private readonly BattlefieldFogFade _fogFade = new();
        private Vector4 _fogZone;
        private float _localOutsideSince = -1f;
        [SerializeField, Min(0.5f)] private float announcementDuration = 4f;
        private VoyagePhase _lastAnnouncedPhase;
        private bool _phaseObserved;
        private int _announcedWave;
        private string _announcement;
        private float _announcementUntil;

        public static bool TryGetBattlefield(out Vector4 centerRadius, out NightWaveSettings fogSettings)
        {
            centerRadius = default;
            fogSettings = null;
            var controller = Active;
            if (controller == null || controller._battery == null || !controller._battery.IsSpawned ||
                !controller._battery.BattlefieldActive || controller.settings == null) return false;
            var center = controller._battery.BattlefieldCenter;
            centerRadius = new Vector4(center.x, center.y, center.z, controller._battery.BattlefieldRadius);
            fogSettings = controller.settings;
            return true;
        }

        public static bool TryGetFog(out Vector4 centerRadius, out NightWaveSettings fogSettings, out float opacity)
        {
            var controller = Active;
            centerRadius = controller != null ? controller._fogZone : default;
            fogSettings = controller != null ? controller.settings : null;
            opacity = controller != null ? controller._fogFade.Opacity : 0f;
            return fogSettings != null && centerRadius.w > 0f && opacity > 0f;
        }

        private void LateUpdate()
        {
            if (settings == null) return;
            var active = TryGetBattlefield(out var zone, out _);
            if (active) _fogZone = zone;
            _fogFade.Step(active, active ? _battery.BattlefieldAge : 0f, Time.unscaledTime,
                settings.FogFadeInSeconds, settings.FogFadeOutSeconds);
        }

        private void OnEnable()
        {
            Active = this;
            settings ??= Resources.Load<NightWaveSettings>("NightWaveSettings");
            dayNight ??= GetComponent<VoyageDayNightController>();
            if (dayNight != null) dayNight.NightStartedServer += OnNightStartedServer;
        }

        private void OnDisable()
        {
            _fogFade.Reset();
            _fogZone = default;
            if (Active == this) Active = null;
            if (dayNight != null) dayNight.NightStartedServer -= OnNightStartedServer;
        }

        private void Update()
        {
            if (_battery == null || !_battery.IsSpawned)
                _battery = FindFirstObjectByType<ShipCannonBattery>();
            UpdateAnnouncement();
            UpdateBoundaryWarning();
            if (settings == null || NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
                return;
            if (_battery == null || !_battery.IsSpawned || _battery.VoyageEnded) return;
            if (!_waveNumberPublished)
            {
                _battery.SetVoyageWaveServer(_waveIndex + 1);
                _waveNumberPublished = true;
            }
            if (_battery.UpgradePaused || _battery.VoyageEnded) return;

            if (_victoryRequested)
            {
                return;
            }
            if (!_waveActive) return;
            UpdateBoundaryServer();
            if (Time.time < _nextWaveCheck) return;
            _nextWaveCheck = Time.time + 0.5f;
            if (WaveRemaining() != 0) return;
            _waveActive = false;
            _battery.ClearBattlefieldServer();
            _boundaryBreaches.Reset();
            if (_waveIndex + 1 >= settings.Waves.Length)
            {
                _victoryRequested = true;
                dayNight?.PauseClockServer();
                _battery.SetVoyageVictoryServer(settings.VictoryDisplayDuration);
            }
            else
            {
                _waveIndex++;
                _battery.SetVoyageWaveServer(_waveIndex + 1);
                dayNight?.ReleaseNightServer();
            }
        }

        private void OnNightStartedServer()
        {
            if (_battery == null || !_battery.IsSpawned)
                _battery = FindFirstObjectByType<ShipCannonBattery>();
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer ||
                _battery == null || _battery.VoyageEnded || _waveActive || _victoryRequested) return;
            if (settings == null || settings.Waves == null || settings.Waves.Length == 0)
            {
                dayNight?.ReleaseNightServer();
                return;
            }
            if (_waveIndex >= settings.Waves.Length) return;
            _battery.SetVoyageWaveServer(_waveIndex + 1);
            _battery.SetBattlefieldServer(_battery.transform.position,
                settings.Waves[_waveIndex].BattlefieldRadius);
            _boundaryBreaches.Reset();
            _nextBoundaryCheck = Time.time;
            SpawnWave();
            _waveActive = true;
        }

        private void UpdateBoundaryServer()
        {
            if (!_battery.BattlefieldActive || Time.time < _nextBoundaryCheck) return;
            _nextBoundaryCheck = Time.time + settings.BoundaryCheckInterval;
            var outside = !_battery.IsInsideBattlefield(_battery.transform.position);
            if (_boundaryBreaches.Step(Time.time, outside,
                settings.BoundaryGraceSeconds, settings.BoundaryBreachInterval))
                _battery.OpenBoundaryBreachServer(settings.BoundaryBreachLeakMultiplier);
        }

        private void UpdateBoundaryWarning()
        {
            if (_battery == null || !_battery.IsSpawned || !_battery.BattlefieldActive)
            { _localOutsideSince = -1f; return; }
            if (_battery.IsInsideBattlefield(_battery.transform.position))
            { _localOutsideSince = -1f; return; }
            if (_localOutsideSince < 0f) _localOutsideSince = Time.time;
        }

        private Vector3 SpawnCenter(int index)
        {
            if (waveSpawnPoints != null && waveSpawnPoints.Length > 0 &&
                waveSpawnPoints[index % waveSpawnPoints.Length] != null)
                return waveSpawnPoints[index % waveSpawnPoints.Length].position;
            return _battery.transform.position;
        }

        private void SpawnWave()
        {
            var wave = settings.Waves[_waveIndex];
            _waveGroup = 100000 + _waveIndex;
            _wavePrefabs.Clear();
            var skeletons = DotsEnemyRuntime.Instance;
            var entryIndex = 0;
            foreach (var entry in wave.Bots ?? System.Array.Empty<NightWaveBot>())
            {
                if (entry == null || entry.Count <= 0) continue;
                var center = SpawnCenter(entryIndex++);
                if (entry.Prefab != null && entry.Prefab.TryGetComponent<NetworkObject>(out _))
                {
                    if (!entry.Prefab.TryGetComponent<NetworkHealth>(out _))
                    {
                        Debug.LogWarning($"Night-wave prefab '{entry.Prefab.name}' needs NetworkHealth to track its defeat.", this);
                        continue;
                    }
                    for (var i = 0; i < entry.Count; i++)
                        SpawnNetworkBot(entry.Prefab, center, wave.SpawnRadius);
                    continue;
                }
                var type = entry.Prefab != null &&
                    entry.Prefab.TryGetComponent<EnemySpawnPoint>(out var marker)
                    ? marker.CombatType : entry.SkeletonType;
                skeletons?.QueueNightWave(center, entry.Count, wave.SpawnRadius, type, _waveGroup);
            }
            if (wave.EnemyShips > 0)
                DotsEnemyShipRuntime.Instance?.SpawnAt(_battery.transform.position +
                    _battery.transform.forward * 65f, wave.EnemyShips,
                    Mathf.Max(15f, wave.SpawnRadius * 2f),
                    unchecked((uint)(_waveGroup * 7919)), _waveGroup);
            _nextWaveCheck = Time.time + 1f;
        }

        private void SpawnNetworkBot(GameObject prefab, Vector3 center, float radius)
        {
            var offset = Random.insideUnitCircle * radius;
            var wanted = center + new Vector3(offset.x, 0f, offset.y);
            var position = wanted + Vector3.up;
            var hits = Physics.RaycastAll(wanted + Vector3.up * 25f, Vector3.down, 50f);
            foreach (var hit in hits)
                if (hit.collider.GetComponentInParent<StylizedWater3.WaterObject>() == null &&
                    hit.normal.y >= 0.5f && hit.point.y > position.y - 10f)
                    position = hit.point + Vector3.up * 0.1f;
            var instance = Instantiate(prefab, position, Quaternion.identity);
            if (!instance.TryGetComponent<NetworkObject>(out var networkObject))
            { Destroy(instance); return; }
            networkObject.Spawn();
            if (instance.TryGetComponent<NetworkHealth>(out var health))
            {
                health.DisableWaveRespawnServer();
                _wavePrefabs.Add(health);
            }
        }

        private int WaveRemaining()
        {
            var count = (DotsEnemyRuntime.Instance?.NightWaveRemaining(_waveGroup) ?? 0) +
                (DotsEnemyShipRuntime.Instance?.NightWaveRemaining(_waveGroup) ?? 0);
            foreach (var health in _wavePrefabs)
                if (health != null && !health.IsDead) count++;
            return count;
        }

        private void UpdateAnnouncement()
        {
            if (_battery == null || !_battery.IsSpawned) return;
            var phase = _battery.Phase;
            if (_phaseObserved && _lastAnnouncedPhase == phase) return;
            var previous = _lastAnnouncedPhase;
            var hadPrevious = _phaseObserved;
            _phaseObserved = true;
            _lastAnnouncedPhase = phase;
            if (phase == VoyagePhase.Night)
            {
                _announcedWave = _battery.WaveNumber;
                ShowAnnouncement($"НОЧНАЯ ВОЛНА {_announcedWave}");
            }
            else if (hadPrevious && previous == VoyagePhase.Night && phase == VoyagePhase.Sunrise)
                ShowAnnouncement($"ВОЛНА {_announcedWave} ОТБИТА");
            else if (phase == VoyagePhase.Victory)
                ShowAnnouncement($"ВОЛНА {_announcedWave} ОТБИТА · КАРТА ПРОЙДЕНА");
        }

        private void ShowAnnouncement(string message)
        {
            _announcement = message;
            _announcementUntil = Time.unscaledTime + announcementDuration;
        }

        public string Announcement => Time.unscaledTime < _announcementUntil ? _announcement : "";
        public float AnnouncementAlpha => Mathf.Clamp01(_announcementUntil-Time.unscaledTime);
        public string BoundaryWarning
        {
            get
            {
                if(_localOutsideSince<0f || settings==null)return "";
                var grace=Mathf.Max(0f,settings.BoundaryGraceSeconds-(Time.time-_localOutsideSince));
                return grace>0f?$"ВЕРНИТЕСЬ В ПОЛЕ БОЯ · УРОН ЧЕРЕЗ {Mathf.CeilToInt(grace)} С":
                    "ВЕРНИТЕСЬ В ПОЛЕ БОЯ · ПОЯВЛЯЮТСЯ ПРОБОИНЫ";
            }
        }
    }
}
