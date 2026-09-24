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

    // Gameplay waves depend on night notifications; the lighting clock never depends on waves.
    public sealed class NightWaveController : MonoBehaviour
    {
        public static NightWaveController Active { get; private set; }
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
        private float _localOutsideSince = -1f;
        [SerializeField, Min(0.5f)] private float announcementDuration = 4f;
        private VoyagePhase _lastAnnouncedPhase;
        private bool _phaseObserved;
        private int _announcedWave;
        private string _announcement;
        private float _announcementUntil;
        private GUIStyle _announcementStyle;
        private GUIStyle _boundaryStyle;

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

        private void OnEnable()
        {
            Active = this;
            settings ??= Resources.Load<NightWaveSettings>("NightWaveSettings");
            dayNight ??= GetComponent<VoyageDayNightController>();
            if (dayNight != null) dayNight.NightStartedServer += OnNightStartedServer;
        }

        private void OnDisable()
        {
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

        private void OnGUI()
        {
            if (_battery == null || !_battery.IsSpawned)
                _battery = FindFirstObjectByType<ShipCannonBattery>();
            if (_battery == null || !_battery.IsSpawned || _battery.VoyageEnded) return;
            var rect = new Rect((Screen.width - 300f) * 0.5f, 12f, 300f, 36f);
            var label = _battery.Phase == VoyagePhase.Day ? $"День {_battery.WaveNumber}" :
                _battery.Phase == VoyagePhase.Victory ? "Карта пройдена!" :
                _battery.Phase == VoyagePhase.Night ? $"Ночная волна {_battery.WaveNumber}" :
                _battery.Phase == VoyagePhase.Sunset ? "Наступает ночь" : "Рассвет";
            GUI.Box(rect, label);
            var bar = new Rect(rect.x + 6f, rect.y + 26f, rect.width - 12f, 5f);
            var previous = GUI.color;
            GUI.color = Color.black;
            GUI.DrawTexture(bar, Texture2D.whiteTexture);
            bar.width *= _battery.DayProgress;
            GUI.color = _battery.Phase == VoyagePhase.Night ? Color.cyan : Color.yellow;
            GUI.DrawTexture(bar, Texture2D.whiteTexture);
            GUI.color = previous;
            if (_localOutsideSince >= 0f && settings != null)
            {
                var grace = Mathf.Max(0f, settings.BoundaryGraceSeconds -
                    (Time.time - _localOutsideSince));
                var warning = grace > 0f
                    ? $"ВЕРНИТЕСЬ В ПОЛЕ БОЯ · УРОН ЧЕРЕЗ {Mathf.CeilToInt(grace)} С"
                    : "ВЕРНИТЕСЬ В ПОЛЕ БОЯ · ПОЯВЛЯЮТСЯ ПРОБОИНЫ";
                _boundaryStyle ??= new GUIStyle(GUI.skin.label)
                { alignment = TextAnchor.MiddleCenter, fontSize = 19, fontStyle = FontStyle.Bold };
                _boundaryStyle.normal.textColor = new Color(1f, .58f, .4f);
                GUI.Label(new Rect(Screen.width * .5f - 360f, 92f, 720f, 32f), warning, _boundaryStyle);
            }
            if (string.IsNullOrEmpty(_announcement) || Time.unscaledTime >= _announcementUntil) return;
            var remaining = Mathf.Min(1f, _announcementUntil - Time.unscaledTime);
            var banner = new Rect(Screen.width * 0.5f - 310f, Screen.height * 0.18f, 620f, 80f);
            GUI.color = new Color(0.02f, 0.06f, 0.1f, 0.8f * remaining);
            GUI.DrawTexture(banner, Texture2D.whiteTexture);
            _announcementStyle ??= new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 34,
                fontStyle = FontStyle.Bold
            };
            _announcementStyle.normal.textColor = new Color(1f, 0.86f, 0.55f, remaining);
            GUI.color = Color.white;
            GUI.Label(banner, _announcement, _announcementStyle);
            GUI.color = previous;
        }
    }
}
