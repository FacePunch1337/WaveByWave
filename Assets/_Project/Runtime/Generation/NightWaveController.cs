using Unity.Netcode;
using UnityEngine;
using WaveByWave.Enemies;
using WaveByWave.Networking;
using WaveByWave.Ships;

namespace WaveByWave.Generation
{
    // Gameplay waves depend on night notifications; the lighting clock never depends on waves.
    public sealed class NightWaveController : MonoBehaviour
    {
        public static NightWaveController Active { get; private set; }
        public static bool BattleInProgress => NightBattlefieldController.BattleInProgress;
        public int CurrentDay => _waveIndex + 1;
        [SerializeField] private NightWaveSettings settings;
        [SerializeField] private VoyageDayNightController dayNight;
        [SerializeField] private Transform[] waveSpawnPoints;

        private ShipCannonBattery _battery;
        private int _waveIndex, _fragmentIndex = -1, _waveGroup, _waveTotalBots;
        private bool _waveActive, _victoryRequested;
        private bool _waveNumberPublished;
        private float _nextWaveCheck;
        private NightBattlefieldController _battlefield;
        [SerializeField, Min(0.5f)] private float announcementDuration = 4f;
        private VoyagePhase _lastAnnouncedPhase;
        private bool _phaseObserved;
        private int _announcedWave;
        private string _announcement;
        private float _announcementUntil;

        private void OnEnable()
        {
            Active = this;
            settings ??= Resources.Load<NightWaveSettings>("NightWaveSettings");
            _battlefield = NightBattlefieldController.EnsureInstance(gameObject);
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
            if (Time.time < _nextWaveCheck) return;
            _nextWaveCheck = Time.time + 0.25f;
            PublishEnemyProgress();
            if (FragmentRemaining() != 0) return;
            var wave = CurrentWave;
            if (wave != null && _fragmentIndex + 1 < (wave.Fragments?.Length ?? 0))
            {
                _fragmentIndex++;
                SpawnFragment(wave.Fragments[_fragmentIndex]);
                PublishEnemyProgress();
                return;
            }
            FinishWave();
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
            var wave = settings.Waves[_waveIndex];
            if (wave == null)
            {
                dayNight?.ReleaseNightServer();
                return;
            }
            _battery.SetVoyageWaveServer(_waveIndex + 1);
            _battlefield ??= NightBattlefieldController.EnsureInstance(gameObject);
            _battlefield.BeginServer(_battery, _battery.transform.position, wave.BattlefieldRadius);
            _waveActive = true;
            _fragmentIndex = 0;
            _waveTotalBots = CountWaveBots(wave, ShipDefinition);
            _battery.SetWaveEnemyProgressServer(0, _waveTotalBots);
            if (wave.Fragments == null || wave.Fragments.Length == 0)
            {
                FinishWave();
                return;
            }
            SpawnFragment(wave.Fragments[0]);
            PublishEnemyProgress();
        }

        public bool StartAdminWaveServer(int waveIndex)
        {
            settings ??= Resources.Load<NightWaveSettings>("NightWaveSettings");
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer ||
                settings == null || settings.Waves == null ||
                waveIndex < 0 || waveIndex >= settings.Waves.Length || settings.Waves[waveIndex] == null)
                return false;
            if (_battery == null || !_battery.IsSpawned)
                _battery = FindFirstObjectByType<ShipCannonBattery>();
            if (_battery == null || !_battery.IsSpawned || _battery.VoyageEnded) return false;
            dayNight ??= GetComponent<VoyageDayNightController>();
            if (dayNight == null) dayNight = FindFirstObjectByType<VoyageDayNightController>();
            if (dayNight == null) return false;

            if (_waveActive && _waveGroup != 0)
            {
                DotsEnemyRuntime.Instance?.DespawnGroup(_waveGroup);
                DotsEnemyShipRuntime.Instance?.DespawnGroup(_waveGroup);
            }
            _battlefield?.EndServer();
            _waveIndex = waveIndex;
            _fragmentIndex = -1;
            _waveGroup = 0;
            _waveTotalBots = 0;
            _waveActive = false;
            _victoryRequested = false;
            _waveNumberPublished = true;
            _battery.SetVoyageWaveServer(_waveIndex + 1);
            _battery.SetWaveEnemyProgressServer(0, CountWaveBots(settings.Waves[_waveIndex], ShipDefinition));
            return dayNight.ForceNightServer();
        }

        private Vector3 SpawnCenter(int index)
        {
            if (waveSpawnPoints != null && waveSpawnPoints.Length > 0 &&
                waveSpawnPoints[index % waveSpawnPoints.Length] != null)
                return waveSpawnPoints[index % waveSpawnPoints.Length].position;
            return _battery.transform.position;
        }

        private NightWaveDefinition CurrentWave => settings != null && settings.Waves != null &&
            _waveIndex >= 0 && _waveIndex < settings.Waves.Length ? settings.Waves[_waveIndex] : null;

        private EnemyShipDefinition ShipDefinition => DotsEnemyShipRuntime.Instance != null
            ? DotsEnemyShipRuntime.Instance.Definition
            : EnemyShipDefinition.Load();

        private void SpawnFragment(NightWaveFragment fragment)
        {
            _waveGroup = 100000 + _waveIndex * 1000 + _fragmentIndex;
            var enemies = DotsEnemyRuntime.EnsureInstance();
            var ships = DotsEnemyShipRuntime.EnsureInstance();
            var entryIndex = 0;
            foreach (var entry in fragment?.Enemies ?? System.Array.Empty<NightWaveEnemy>())
            {
                if (entry == null || entry.Count <= 0 || entry.EnemyType == null) continue;
                var center = SpawnCenter(entryIndex++);
                var minimumRadius = Mathf.Max(1f, entry.SpawnRadius);
                var bandWidth = entry.SpawnBandWidth > 0f ? entry.SpawnBandWidth : 20f;
                var maximumRadius = minimumRadius + Mathf.Max(1f, bandWidth);
                if (entry.EnemyType.IsShip)
                {
                    ships?.SpawnAt(center, entry.Count, maximumRadius,
                        unchecked((uint)(_waveGroup * 7919 + entryIndex * 104729)), _waveGroup,
                        minimumRadius);
                    continue;
                }
                enemies?.QueueNightWave(center, entry.Count, minimumRadius, maximumRadius,
                    entry.EnemyType.CombatType, entry.EnemyType.Species, _waveGroup);
            }
            _nextWaveCheck = Time.time + 1f;
        }

        private int FragmentRemaining()
        {
            return (DotsEnemyRuntime.Instance?.NightWaveRemaining(_waveGroup) ?? 0) +
                   (DotsEnemyShipRuntime.Instance?.NightWaveRemaining(_waveGroup) ?? 0);
        }

        private void PublishEnemyProgress()
        {
            if (_battery == null || !_waveActive) return;
            var wave = CurrentWave;
            var future = CountWaveBots(wave, ShipDefinition, _fragmentIndex + 1);
            var current = (DotsEnemyRuntime.Instance?.NightWaveRemaining(_waveGroup) ?? 0) +
                (DotsEnemyShipRuntime.Instance?.NightWavePendingCrew(_waveGroup) ?? 0);
            _battery.SetWaveEnemyProgressServer(Mathf.Clamp(_waveTotalBots - future - current, 0, _waveTotalBots),
                _waveTotalBots);
        }

        internal static int CountWaveBots(NightWaveDefinition wave, EnemyShipDefinition shipDefinition,
            int firstFragment = 0)
        {
            if (wave?.Fragments == null) return 0;
            var total = 0;
            for (var f = Mathf.Max(0, firstFragment); f < wave.Fragments.Length; f++)
                foreach (var entry in wave.Fragments[f]?.Enemies ?? System.Array.Empty<NightWaveEnemy>())
                    if (entry != null && entry.EnemyType != null && entry.Count > 0)
                        total += entry.Count * entry.EnemyType.CountedBotsPerSpawn(shipDefinition);
            return Mathf.Max(0, total);
        }

        private void FinishWave()
        {
            _waveActive = false;
            _battery.SetWaveEnemyProgressServer(_waveTotalBots, _waveTotalBots);
            _battlefield?.EndServer();
            if (_waveIndex + 1 >= settings.Waves.Length)
            {
                _victoryRequested = true;
                dayNight?.PauseClockServer();
                _battery.SetVoyageVictoryServer(settings.VictoryDisplayDuration);
                return;
            }
            _waveIndex++;
            _fragmentIndex = -1;
            _battery.SetVoyageWaveServer(_waveIndex + 1);
            dayNight?.ReleaseNightServer();
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
    }
}
