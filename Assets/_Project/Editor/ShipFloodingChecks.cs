using System;
using Unity.Collections;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;
using WaveByWave.Player;
using WaveByWave.Ships;
using Object = UnityEngine.Object;

namespace WaveByWave.Editor
{
    public static class ShipFloodingChecks
    {
        private static void Check(bool condition, string reason)
        { if (!condition) throw new InvalidOperationException(reason); }

        [MenuItem("Tools/Wave by Wave/Ships/Check flooding, UV holes and bucket transfers")]
        public static void Run()
        {
            var tank = new FloodReservoir();
            tank.Add(3f * 2f, 600f);
            Check(tank.Litres == 6f, "Leak time integration");
            var bucket = tank.Take(10f);
            Check(bucket == 6f && tank.Litres == 0f, "Last partial bucket must conserve water");
            tank.Add(bucket, 600f);
            Check(tank.Litres == 6f, "Pouring back restores the exact scooped amount");
            tank.Take(10f);
            Check(tank.Litres == 0f, "Pouring overboard leaves the compartment drained");
            tank.Add(float.NaN, 600f); tank.Add(float.PositiveInfinity, 600f); tank.Add(-30f, 600f);
            Check(tank.Litres == 0f && tank.Take(-10f) == 0f, "Invalid transfers must not create water");
            tank.Add(599f, 600f); tank.Add(5f, 600f);
            Check(tank.Litres == 600f, "Flood capacity clamps at the sinking threshold");
            var one = new FloodReservoir(); var two = new FloodReservoir();
            one.Add(3f * 10f, 600f); two.Add(6f * 10f, 600f);
            Check(two.Litres == one.Litres * 2f, "Two equal holes flood twice as quickly");
            var repair = 0f; var upgradedRepair = 0f;
            for (var i = 0; i < 20; i++)
            { repair = ShipFlooding.AdvanceRepair(repair, 0.1f, 1f, 4f); upgradedRepair = ShipFlooding.AdvanceRepair(upgradedRepair, 0.1f, 1.2f, 4f); }
            Check(Mathf.Abs(repair - 0.5f) < 0.0001f && Mathf.Abs(upgradedRepair - 0.6f) < 0.0001f, "Repair ring scales held repair speed");
            Check(ShipFlooding.AdvanceRepair(0f, 99f, 1f, 4f) < 0.04f, "Repair heartbeat cannot credit arbitrary elapsed time");
            var breach = new HullBreach { Id = 17, UV = new Vector2(0.3f, 0.7f), RadiusUV = Vector2.one * 0.01f,
                Position = new Vector3(2,3,4), Normal = Vector3.right, Leak = 4.5f, Repair = 0.42f };
            using (var writer = new FastBufferWriter(128, Allocator.Temp))
            {
                writer.WriteNetworkSerializable(breach);
                using var reader = new FastBufferReader(writer, Allocator.Temp);
                reader.ReadNetworkSerializable(out HullBreach restored);
                Check(breach.Equals(restored), "Replicated breach state must preserve position, leak and repair progress");
            }
            var waterGrid = ShipWaterVolume.BuildSurfaceMesh(new Bounds(Vector3.zero, new Vector3(4, 2, 8)));
            try
            { Check(waterGrid.vertexCount > 100 && waterGrid.triangles.Length > 100 && waterGrid.bounds.size.y >= 2f,
                "Open hull needs a real, cull-safe water surface grid"); }
            finally { Object.DestroyImmediate(waterGrid); }

            var go = new GameObject("Water volume regression") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                var volume = go.AddComponent<ShipWaterVolume>();
                volume.LocalBounds = new Bounds(Vector3.zero, new Vector3(4, 2, 8));
                volume.Footprint = new[] { new Vector2(-2,-4), new Vector2(2,-4), new Vector2(2,2), new Vector2(0,4), new Vector2(-2,2) };
                go.transform.SetPositionAndRotation(new Vector3(12, 3, -8), Quaternion.Euler(0, 37, 0));
                Check(volume.ContainsColumn(go.transform.TransformPoint(Vector3.zero)), "Moving ship interior point");
                Check(!volume.ContainsColumn(go.transform.TransformPoint(new Vector3(1.9f, 0, 3.9f))), "Bow outside footprint must pour overboard");
                var origin = go.transform.TransformPoint(new Vector3(0, 2, 0));
                Check(volume.RaySurface(origin, Vector3.down, 3f, 0.5f, out var hit) && Mathf.Abs(hit.y - 3f) < 0.0001f,
                    "Bucket intersects the moving internal water level");
                Check(!volume.RaySurface(origin, Vector3.down, 3f, 0f, out _), "Dry hull has no scoopable surface");
                Check(!volume.RaySurface(origin, Vector3.up, 3f, 0.5f, out _), "Cannot scoop behind the aim ray");
                volume.StylizedWaterMaterial = AssetDatabase.LoadAssetAtPath<Material>(
                    "Assets/Stylized Water 3/Materials/StylizedWater3_Smooth.mat");
                volume.Present(.5f, false);
                var generated = go.transform.Find("Interior water surface");
                Check(generated != null && generated.GetComponent<Collider>() == null &&
                    generated.GetComponent<StylizedWater3.WaterObject>() == null &&
                    !volume.RuntimeWaterMaterial.GetShaderPassEnabled("WaterHeight"),
                    "Interior water must have no buoyancy source or height prepass");
            }
            finally { Object.DestroyImmediate(go); }

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/Ship.prefab");
            var ship = prefab.GetComponent<ShipFlooding>();
            Check(ship != null && ship.Hull != null && ship.WaterVolume != null, "Ship prefab flooding wiring");
            Check(ship.Hull.Sites.Length > 30, "Hull requires enough repairable breach sites");
            foreach (var site in ship.Hull.Sites)
                Check(ship.Hull.IsAllowed(site.UV, ship.Hull.RadiusUV(site), site.Position.y, site.Normal), "Site outside selected UV region");
            Check(ship.Hull.TryChooseSite(ship.transform.position, _ => false, out _), "Cannon hit must find a breach site");
            Check(!ship.Hull.TryChooseSite(ship.transform.position, _ => true, out _), "Existing holes reserve their positions");
            var oceanCut = ship.WaterVolume.OceanCutout.GetComponent<ShipOceanCutout>();
            Check(ship.WaterVolume.StylizedWaterMaterial != null &&
                ship.WaterVolume.StylizedWaterMaterial.HasProperty("_WaveProfile"),
                "Interior water must use a real Stylized Water 3 material with its wave profile");
            Check(oceanCut != null && oceanCut.Sections != null && oceanCut.SourceMesh ==
                ship.WaterVolume.OceanCutout.GetComponent<MeshFilter>().sharedMesh, "Ocean cutout must have sections for the authored mesh");
            foreach (var renderer in new[] { ship.WaterVolume.OceanCutout, ship.WaterVolume.InteriorWater })
            {
                var filter = renderer.GetComponent<MeshFilter>();
                Check(!WaveByWave.Collision.KinematicShipCollision.IsSolidModel(filter),
                    $"Water volume '{renderer.name}' must not become cached hull collision geometry; " +
                    $"parent={renderer.transform.parent?.name}, volume={renderer.GetComponentInParent<ShipWaterVolume>(true)?.name}, " +
                    $"collision assembly={typeof(WaveByWave.Collision.KinematicShipCollision).Assembly.Location}");
            }
            var catalog = AssetDatabase.LoadAssetAtPath<PlayerRingCatalog>("Assets/_Project/Resources/PlayerRingCatalog.asset");
            Check(catalog.Find(PlayerRingStat.Repair).BaseBonus > 0f && (int)PlayerRingStat.Repair < 16, "Repair ring must fit network offer encoding");
            foreach (var name in new[] { "Hull Holes", "Water Cut" })
            {
                var shader = Shader.Find("WaveByWave/Ships/" + name);
                Check(shader != null && !ShaderUtil.ShaderHasError(shader), "Shader error: " + name);
            }
            Check(AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/_Project/Generated/Animations/Equipment/BucketSplash.anim") != null,
                "Bucket pour animation is missing");
            Debug.Log("[Ship flooding checks] PASS: water conservation, leak rates, moving volume, UV sites, prefab wiring, repair ring and shaders.");
        }
    }
}
