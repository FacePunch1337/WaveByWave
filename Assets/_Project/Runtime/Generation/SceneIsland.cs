using Unity.Netcode;
using UnityEngine;

namespace WaveByWave.Generation
{
    [DefaultExecutionOrder(100)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(ProceduralIsland))]
    public sealed class SceneIsland : MonoBehaviour
    {
        public const int CurrentBakeVersion = 2;
        [Tooltip("Общий профиль геометрии, материалов, декораций и сундуков.")]
        public OceanGenerationSettings Settings;
        public IslandSize Size = IslandSize.Medium;
        [Min(1), Tooltip("Фиксированный seed: одинаковое значение всегда даёт один и тот же остров.")]
        public int Seed = 24681357;
        [Tooltip("Создавать на этом острове зарытые сетевые сундуки. Для острова в Port выключено по умолчанию.")]
        public bool SpawnChests;
        [Tooltip("Генерировать точки скелетов из Settings во время игры. Для безопасного острова Port оставьте выключенным.")]
        public bool SpawnEnemyPoints;
        [Tooltip("Уникальный отрицательный id. Если разместите второй Scene Island, назначьте ему другой id.")]
        public int NetworkIslandId = -10001;

        [SerializeField, HideInInspector] private Transform bakedGeometryRoot;
        [SerializeField, HideInInspector] private Transform decorationRoot;
        [SerializeField, HideInInspector] private int bakedSeed;
        [SerializeField, HideInInspector] private IslandSize bakedSize;
        [SerializeField, HideInInspector] private float bakedDiameter, bakedVoxelSize;
        [SerializeField, HideInInspector] private int bakedVersion;

        private ProceduralIsland _island;
        private float _nextRegistration;
        public Transform BakedGeometryRoot => bakedGeometryRoot;
        public Transform DecorationRoot => decorationRoot;
        public bool BakeMatchesSettings => Settings != null && bakedSeed == Mathf.Max(1, Seed) && bakedSize == Size &&
            Mathf.Approximately(bakedDiameter, Settings.Diameter(Size)) &&
            Mathf.Approximately(bakedVoxelSize, Settings.VoxelSize) && bakedGeometryRoot != null &&
            bakedVersion == CurrentBakeVersion;

        public void ConfigureForBake(OceanGenerationSettings settings, Transform geometry, Transform decorations)
        { Settings = settings; bakedGeometryRoot = geometry; decorationRoot = decorations; }

        public void MarkBaked()
        {
            bakedSeed = Mathf.Max(1, Seed); bakedSize = Size;
            bakedDiameter = Settings != null ? Settings.Diameter(Size) : 0f;
            bakedVoxelSize = Settings != null ? Settings.VoxelSize : 0f;
            bakedVersion = CurrentBakeVersion;
        }

        private void Awake()
        {
            if (!Application.isPlaying) return;
            if (Settings == null) Settings = Resources.Load<OceanGenerationSettings>("OceanGeneration");
            if (Settings == null || bakedGeometryRoot == null)
            { Debug.LogError("Scene Island is not baked. Generate the prefab in its Inspector first.", this); enabled = false; return; }
            _island = GetComponent<ProceduralIsland>();
            _island.InitializeBaked(NetworkIslandId < 0 ? NetworkIslandId : -Mathf.Max(1, NetworkIslandId),
                Size, (uint)Mathf.Max(1, Seed), Settings,
                bakedGeometryRoot.GetComponentsInChildren<BakedIslandChunk>(true), decorationRoot);
            Register(false);
        }

        private void Update()
        {
            if (_island == null || Time.unscaledTime < _nextRegistration) return;
            _nextRegistration = Time.unscaledTime + 0.5f;
            var manager = NetworkManager.Singleton;
            var populate = SpawnChests && manager != null && manager.IsListening && manager.IsServer;
            Register(populate);
        }

        private void Register(bool populate)
        {
            var director = OceanWorldDirector.EnsureInstance();
            if (director != null) director.RegisterSceneIsland(_island, populate);
        }
    }
}
