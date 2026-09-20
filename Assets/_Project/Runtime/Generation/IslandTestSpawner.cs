using UnityEngine;

namespace WaveByWave.Generation
{
    public sealed class IslandTestSpawner : MonoBehaviour
    {
        public IslandSize Size = IslandSize.Medium;
        public int Seed = 12345;
        public bool RandomizeSeed = true;
        public bool SnapToWater = true;
        public bool SpawnChests = true;
        [SerializeField, HideInInspector] private int generatedIsland;

        [ContextMenu("Сгенерировать остров")]
        public void Generate()
        {
            if (!Application.isPlaying) { Debug.LogWarning("Генератор предназначен для Play Mode.", this); return; }
            var director = OceanWorldDirector.EnsureInstance();
            if (director == null || !director.IsAuthority) { Debug.LogWarning("Генерация островов доступна хосту.", this); return; }
            if (generatedIsland != 0) director.RemoveIsland(generatedIsland);
            generatedIsland = director.GenerateIsland(transform.position, Size,
                RandomizeSeed ? (uint)Random.Range(1, int.MaxValue) : (uint)Mathf.Max(1, Seed),
                SnapToWater, true, SpawnChests);
        }
        [ContextMenu("Удалить тестовый остров")]
        public void Remove()
        {
            if (OceanWorldDirector.Instance != null && OceanWorldDirector.Instance.IsAuthority)
                OceanWorldDirector.Instance.RemoveIsland(generatedIsland);
            generatedIsland = 0;
        }
    }
}
