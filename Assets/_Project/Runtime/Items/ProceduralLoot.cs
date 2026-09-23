using System;
using System.Collections.Generic;
using Unity.Entities;
using Unity.NetCode;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;
using WaveByWave.Combat;
using WaveByWave.Generation;
using WaveByWave.Player;

namespace WaveByWave.Items
{
    public static partial class LootStressTest
    {
        internal const byte ChestOpening = 4, ChestBurst = 5;
        private static readonly List<int> OpeningChests = new();
        public static event Action<int> ServerItemRemoved;
        public static bool HasServerItem(int id) => ServerItems.ContainsKey(id);

        internal static bool IsExposed(int islandId, Vector3 position) => islandId == 0 ||
            (OceanWorldDirector.Instance != null && OceanWorldDirector.Instance.IsChestExposed(islandId, position));

        public static bool TryClientTarget(IPlayerInteractable target, out int id, out ItemDefinition definition)
            => LootStressPresentation.TryTarget(target, out id, out definition);
        public static void SetChestHoldProgress(int id, float progress) => LootStressPresentation.SetChestHoldProgress(id, progress);

        public static int SpawnPlacedServer(ItemDefinition definition, Vector3 position, Quaternion rotation,
            bool onWater, int islandId = 0, float fadeDuration = 0f)
            => CreatePlacedServer(definition, position, rotation, onWater, islandId, fadeDuration, true);

        private static int CreatePlacedServer(ItemDefinition definition, Vector3 position, Quaternion rotation,
            bool onWater, int islandId, float fadeDuration, bool publish)
        {
            if (definition == null || _catalog == null || ClientServerBootstrap.ServerWorld is not { IsCreated: true }) return 0;
            var index = -1;
            for (var i = 0; i < _catalog.Items.Count; i++) if (_catalog.Items[i] == definition) { index = i; break; }
            if (index < 0 || !TryGetVisual(definition, out _, out _)) return 0;
            EnsureStateForCurrentScene(position);
            if (onWater) position += Vector3.up * GetSurfaceClearance(definition, rotation, Vector3.up);
            var item = new ServerItem { CatalogIndex = index, Position = position, RestPosition = position,
                FlightStart = position, Rotation = rotation, BaseRotation = rotation, Dynamic = true,
                OnWater = onWater, WaterSize = GetSurfaceSize(definition), IslandId = islandId,
                FadeDuration = Mathf.Max(0f, fadeDuration) };
            var id = _nextDynamicId--;
            ServerItems.Add(id, item);
            if (publish)
            {
                var delta = BuildAddDelta(id, item, 0f);
                Broadcast(delta); LootStressPresentation.ApplyDelta(delta);
            }
            return id;
        }

        public static bool TryBeginChestOpening(int id, float luck = 0f)
        {
            if (!TryGetServerItem(id, out var definition, out _) || !definition.IsChest) return false;
            var item = ServerItems[id];
            item.OpenerLuck = Mathf.Max(0f, luck);
            item.OpeningAt = NetworkManager.Singleton.ServerTime.Time + definition.ChestLoot.ShakeDuration;
            OpeningChests.Add(id);
            var delta = new LootStressDeltaCommand { Id = id, Kind = ChestOpening, OpeningAt = item.OpeningAt };
            Broadcast(delta); LootStressPresentation.ApplyDelta(delta);
            return true;
        }

        internal static void UpdateOpeningChests()
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;
            var now = NetworkManager.Singleton.ServerTime.Time;
            for (var i = OpeningChests.Count - 1; i >= 0; i--)
            {
                var id = OpeningChests[i];
                if (!ServerItems.TryGetValue(id, out var item)) { OpeningChests.RemoveAt(i); continue; }
                if (now < item.OpeningAt) continue;
                UpdateItemPose(item);
                var definition = _catalog.Items[item.CatalogIndex];
                var table = definition.ChestLoot;
                var tier = table.FindTier(definition.Rarity);
                if (tier != null)
                {
                    var random = new Unity.Mathematics.Random(unchecked((uint)id * 747796405u + 2891336453u) | 1u);
                    var minimumRolls = Mathf.Clamp(tier.MinimumRolls, 1, 24);
                    var rolls = random.NextInt(minimumRolls, Mathf.Clamp(tier.MaximumRolls, minimumRolls, 24) + 1);
                    rolls = Mathf.Min(48, rolls + Mathf.FloorToInt(item.OpenerLuck * 2f));
                    var emitted = 0;
                    for (var roll = 0; roll < rolls && emitted < 48; roll++)
                    {
                        var reward = ChestLootTable.Choose(tier.Items, ref random, item.OpenerLuck);
                        if (reward == null || reward.IsChest) continue;
                        var entry = tier.Items.Find(e => e != null && e.Item == reward);
                        var amount = random.NextInt(Mathf.Clamp(entry.MinimumAmount, 1, 16),
                            Mathf.Clamp(entry.MaximumAmount, Mathf.Clamp(entry.MinimumAmount, 1, 16), 16) + 1);
                        if (reward.IsCoinReward)
                            amount = Mathf.Min(48, Mathf.CeilToInt(amount * (1f + item.OpenerLuck)));
                        for (var n = 0; n < amount && emitted < 48; n++, emitted++)
                        {
                            var angle = random.NextFloat(0f, Mathf.PI * 2f);
                            var direction = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                            SpawnChestReward(reward, item.Position + Vector3.up * 0.35f,
                                direction, Mathf.Max(0.2f, table.EjectionSpeed) * random.NextFloat(0.7f, 1.1f));
                        }
                    }
                }
                var burst = new LootStressDeltaCommand { Id = id, Kind = ChestBurst, Position = item.Position };
                Broadcast(burst); LootStressPresentation.ApplyDelta(burst);
                ServerItems.Remove(id); ServerItemRemoved?.Invoke(id); OpeningChests.RemoveAt(i);
            }
        }

        private static void SpawnChestReward(ItemDefinition definition, Vector3 start, Vector3 direction, float speed)
        {
            var target = start + direction * speed * 0.45f;
            if (!TryResolveSurface(target + Vector3.up, out var surface, out var normal, out var water, out var support))
            { surface = start; normal = Vector3.up; water = false; support = null; }
            var rotation = Quaternion.FromToRotation(Vector3.up, normal) * definition.RestingRotation;
            var end = surface + normal * GetSurfaceClearance(definition, rotation, normal);
            var id = CreatePlacedServer(definition, end, rotation, false, 0, 0f, false);
            if (id == 0) return;
            var item = ServerItems[id];
            item.Position = start; item.FlightStart = start; item.RestPosition = end; item.OnWater = water;
            item.FlightStarted = NetworkManager.Singleton.ServerTime.Time;
            item.FlightDuration = Mathf.Clamp(BallisticFlightTime(start.y - end.y, speed * 0.8f), 0.3f, 2f);
            item.ArcUp = Vector3.up; item.ArcHeight = Mathf.Max(0.3f, speed * 0.25f);
            SetSupport(item, support);
            // Identity and launch arrive together: no one-frame pop at the landing point,
            // and no dependency on the receive order of two separate RPC entities.
            var delta = BuildAddDelta(id, item, item.FlightDuration);
            Broadcast(delta); LootStressPresentation.ApplyDelta(delta);
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct LootChestServerSystem : ISystem
    { public void OnUpdate(ref SystemState state) => LootStressTest.UpdateOpeningChests(); }

    internal static partial class LootStressPresentation
    {
        private static Image _holdProgress;
        private static int _holdId = int.MaxValue;
        private static float _holdAmount;

        internal static bool TryTarget(IPlayerInteractable target, out int id, out ItemDefinition definition)
        {
            id = int.MaxValue; definition = null;
            if (target is not StressInteractable interactable || !Items.TryGetValue(interactable.Id, out var item)) return false;
            id = item.Id; definition = item.Definition; return true;
        }
        internal static void SetChestHoldProgress(int id, float progress) { _holdId = id; _holdAmount = Mathf.Clamp01(progress); }
        private static void UpdateChestProgress(int id)
        {
            if (_holdProgress == null)
            {
                var prefab = Resources.Load<GameObject>("ChestHoldProgress");
                if (prefab == null) return;
                var instance = UnityEngine.Object.Instantiate(prefab, _prompt.transform, false);
                _holdProgress = instance.transform.Find("Fill").GetComponent<Image>();
            }
            _holdProgress.transform.parent.gameObject.SetActive(id == _holdId && _holdAmount > 0f);
            _holdProgress.fillAmount = _holdAmount;
        }
        private static void UpdateChestAndFade(ClientItem item)
        {
            var position = item.Position; var rotation = item.Rotation;
            if (item.OpeningAt > 0d && item.Definition.IsChest)
            {
                var now = NetworkManager.Singleton != null ? NetworkManager.Singleton.ServerTime.Time : Time.timeAsDouble;
                var elapsed = Mathf.Max(0f, (float)(now - item.OpeningAt) + item.Definition.ChestLoot.ShakeDuration);
                var intensity = Mathf.Clamp01(elapsed / Mathf.Max(0.1f, item.Definition.ChestLoot.ShakeDuration));
                position += new Vector3(Mathf.Sin(elapsed * 71f), Mathf.Abs(Mathf.Sin(elapsed * 53f)),
                    Mathf.Cos(elapsed * 67f)) * (0.035f * intensity);
                rotation *= Quaternion.Euler(Mathf.Sin(elapsed * 63f) * 9f * intensity, 0f,
                    Mathf.Sin(elapsed * 57f) * 12f * intensity);
            }
            var size = item.FadeDuration > 0f ? Mathf.SmoothStep(0.01f, 1f,
                Mathf.Clamp01((float)(Time.timeAsDouble - item.FallStarted) / item.FadeDuration)) : 1f;
            item.DynamicPresentation?.SetPose(position, rotation, size);
            if (size >= 1f) item.FadeDuration = 0f;
        }
        private static void PlayChestBurst(ClientItem item)
        {
            var prefab = item.Definition.ChestLoot != null ? item.Definition.ChestLoot.OpenEffectPrefab : null;
            if (prefab == null) return;
            var effect = UnityEngine.Object.Instantiate(prefab, item.Position, Quaternion.identity);
            if (effect.TryGetComponent<DeathDustBurst>(out var dust)) dust.Play(0.65f);
            else UnityEngine.Object.Destroy(effect, 3f);
        }
    }
}
