using System;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using WaveByWave.Generation;

namespace WaveByWave.Editor
{
    public static class OceanGenerationChecks
    {
        [MenuItem("Tools/Wave by Wave/Validate Island Geometry")]
        public static void Run()
        {
            foreach (var size in new[] { 5f, 10f, 15f }) Validate(size);
            Debug.Log("[Ocean checks] PASS: all sizes, seed determinism, mesh validity, immutable bedrock, dig replay and chunk borders.");
        }

        public static void CheckDecorationPlacement()
        {
            var authored = AssetDatabase.LoadAssetAtPath<OceanGenerationSettings>("Assets/_Project/Resources/OceanGeneration.asset");
            var palm = authored.Decorations.Find(entry => entry.Prefab != null && entry.Prefab.name == "SW3_PalmTree");
            Require(palm != null && palm.GroundMeshBase, "The configured palm must use mesh-base grounding.");
            var anchor = ProceduralIsland.DecorationGroundAnchor(palm.Prefab);
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            foreach (var seed in new uint[] { 1, 42, 481516 })
            {
                var root = new GameObject("Palm grounding regression"); root.SetActive(false);
                var settings = UnityEngine.Object.Instantiate(authored);
                settings.SmallDiameter = 10;
                settings.Decorations = new System.Collections.Generic.List<IslandDecoration> {
                    new IslandDecoration { Prefab = palm.Prefab, GroundMeshBase = true,
                        InstancesPerSquareMetre = 0.06f, ScaleRange = palm.ScaleRange,
                        MaximumSlope = 50, MinimumHeightAboveWater = 0.1f, Spacing = 0.5f } };
                var island = root.AddComponent<ProceduralIsland>();
                try
                {
                    island.Initialize(-99999, IslandSize.Small, seed, settings);
                    var builder = (System.Collections.Generic.IEnumerable<byte>)typeof(ProceduralIsland)
                        .GetMethod("CreateDecorations", flags).Invoke(island, null);
                    foreach (var unused in builder) { }
                    Require(root.transform.childCount > 0, "Regression seed placed no palms.");
                    foreach (Transform tree in root.transform)
                    {
                        var contact = tree.TransformPoint(anchor);
                        Require(island.TrySurface(contact.x, contact.z, out var surface, out _, true) &&
                            Mathf.Abs(contact.y - surface.y) < 0.002f, "Generated palm base missed the island surface.");
                        Require(island.DensityAt(contact - Vector3.up * 0.15f).x >= -0.1f,
                            "The palm lost its digging support after pivot compensation.");
                        var lowest = float.PositiveInfinity;
                        // Editor-only read access proves contact against the actual FBX
                        // vertices without enabling Read/Write or changing its importer.
                        foreach (var filter in tree.GetComponentsInChildren<MeshFilter>(true))
                        {
                            using var dataArray = MeshUtility.AcquireReadOnlyMeshData(filter.sharedMesh);
                            var data = dataArray[0];
                            using var vertices = new NativeArray<Vector3>(data.vertexCount, Allocator.Temp);
                            data.GetVertices(vertices);
                            foreach (var vertex in vertices)
                                lowest = Mathf.Min(lowest, filter.transform.TransformPoint(vertex).y);
                        }
                        Require(Mathf.Abs(lowest - surface.y) < 0.005f,
                            $"Actual palm geometry floats/sinks: bottom={lowest}, ground={surface.y}, seed={seed}.");
                    }
                }
                finally
                {
                    // The fixture stays inactive, so explicitly release its native jobs.
                    typeof(ProceduralIsland).GetMethod("OnDestroy", flags).Invoke(island, null);
                    UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(settings);
                }
            }
            Debug.Log($"[Palm grounding] PASS; authored prefab base anchor={anchor}.");
        }

        private static void Validate(float diameter)
        {
            var cell = 0.2f;
            var points = new int3(Mathf.CeilToInt(diameter / cell) + 5,
                Mathf.CeilToInt((1.8f + 1f - -4.4f) / cell) + 1,
                Mathf.CeilToInt(diameter / cell) + 5);
            var p = new IslandFieldParameters { Points = points, CellSize = cell,
                Origin = new float3(-(points.x - 1) * cell * 0.5f, -4.4f, -(points.z - 1) * cell * 0.5f),
                Diameter = diameter, SandDepth = 2.4f, SandHeight = 1.8f, NoiseScale = 2.8f,
                Irregularity = 0.22f, Seed = 12345 };
            using var field = new NativeArray<float2>(points.x * points.y * points.z, Allocator.TempJob);
            using var replay = new NativeArray<float2>(field.Length, Allocator.TempJob);
            new IslandInitializeJob { Parameters = p, Density = field }.Schedule(field.Length, 128).Complete();
            new IslandInitializeJob { Parameters = p, Density = replay }.Schedule(field.Length, 128).Complete();
            var original = field.ToArray();
            for (var i = 0; i < field.Length; i++)
                Require(math.all(field[i] == replay[i]) && math.all(math.isfinite(field[i])), "Seed differs or density is non-finite.");
            var before = BuildMesh(p, field, int3.zero, points - 1);
            Require(before.Vertices.Length > 0, "Empty island.");
            CheckMesh(before, p, field); Dispose(before);
            // Repeated excavation through the whole centre must stop at the original rock field.
            for (var n = 0; n < 9; n++)
            {
                var center = new float3(0f, 1.6f - n * 0.5f, 0f);
                foreach (var target in new[] { field, replay })
                    new IslandDigJob { Parameters = p, Density = target, Center = center, Radius = 0.8f,
                        Smoothing = 0.45f, Minimum = int3.zero, Maximum = points - 1 }.Schedule().Complete();
            }
            var changed = 0;
            for (var i = 0; i < field.Length; i++)
            {
                Require(field[i].y == original[i].y, "Dig modified bedrock.");
                Require(math.all(field[i] == replay[i]), "Late-join replay differs.");
                Require(field[i].x <= original[i].x, "Dig added sand.");
                if (field[i].x != original[i].x) changed++;
            }
            Require(changed > 0, "Dig removed no sand.");
            var after = BuildMesh(p, field, int3.zero, points - 1);
            CheckMesh(after, p, field);
            var rock = 0;
            for (var i = 0; i < after.Colors.Length; i++) if (after.Colors[i].r > 200) rock++;
            Require(rock > 0, "Bedrock material is not represented.");
            // Chunk extraction must produce exactly the same triangles as a single full mesh.
            var split = points.x / 2;
            var left = BuildMesh(p, field, int3.zero, new int3(split, points.y - 1, points.z - 1));
            var right = BuildMesh(p, field, new int3(split, 0, 0), points - 1);
            Require(left.Vertices.Length + right.Vertices.Length == after.Vertices.Length, "Chunk triangles lost or duplicated.");
            var fullVertices = new System.Collections.Generic.Dictionary<float3, int>();
            foreach (var vertex in after.Vertices) fullVertices[vertex] = fullVertices.TryGetValue(vertex, out var count) ? count + 1 : 1;
            foreach (var chunk in new[] { left, right })
            foreach (var vertex in chunk.Vertices)
            {
                Require(fullVertices.TryGetValue(vertex, out var count) && count > 0, "Chunk seam differs.");
                fullVertices[vertex] = count - 1;
            }
            Debug.Log($"[Ocean checks] {diameter}m PASS: {field.Length} samples, {after.Vertices.Length / 3} triangles; {changed} sand samples excavated.");
            Dispose(after); Dispose(left); Dispose(right);
        }

        private static IslandMeshJob BuildMesh(IslandFieldParameters p, NativeArray<float2> field, int3 min, int3 max)
        {
            var job = new IslandMeshJob { Parameters = p, Density = field, Minimum = min, Maximum = max,
                Vertices = new NativeList<float3>(1024, Allocator.TempJob), Normals = new NativeList<float3>(1024, Allocator.TempJob),
                Colors = new NativeList<Color32>(1024, Allocator.TempJob) };
            job.Schedule().Complete();
            return job;
        }
        private static void CheckMesh(IslandMeshJob job, IslandFieldParameters parameters, NativeArray<float2> field)
        {
            Require(job.Vertices.Length % 3 == 0 && job.Normals.Length == job.Vertices.Length &&
                job.Colors.Length == job.Vertices.Length, "Invalid vertex streams.");
            for (var i = 0; i < job.Vertices.Length; i++)
            {
                Require(math.all(math.isfinite(job.Vertices[i])), "Non-finite vertex.");
                Require(math.all(math.isfinite(job.Normals[i])) && math.abs(math.length(job.Normals[i]) - 1f) < 0.01f, "Invalid normal.");
            }
            for (var i = 0; i < job.Vertices.Length; i += 3)
            {
                var a = job.Vertices[i]; var b = job.Vertices[i + 1]; var c = job.Vertices[i + 2];
                var face = math.normalizesafe(math.cross(b - a, c - a));
                Require(math.dot(face, job.Normals[i] + job.Normals[i + 1] + job.Normals[i + 2]) > 0f,
                    "Back-facing triangle or inverted vertex normal.");
                var center = (a + b + c) / 3f;
                Require(Solid(center + face * 0.03f, parameters, field) <=
                        Solid(center - face * 0.03f, parameters, field) + 0.0001f,
                    "Triangle winding points into solid density.");
                var longest = math.max(math.distance(a, b), math.max(math.distance(b, c), math.distance(c, a)));
                Require(longest <= 0.9f, "Stretched excavation triangle.");
            }
        }
        private static float Solid(float3 position, IslandFieldParameters parameters, NativeArray<float2> field)
        {
            var grid = (position - parameters.Origin) / parameters.CellSize;
            var point = math.clamp((int3)math.floor(grid), int3.zero, parameters.Points - 2);
            var t = math.saturate(math.frac(grid));
            float Read(int3 p) { var value = field[parameters.Index(p)]; return math.max(value.x, value.y); }
            var a = math.lerp(Read(point), Read(point + new int3(1, 0, 0)), t.x);
            var b = math.lerp(Read(point + new int3(0, 1, 0)), Read(point + new int3(1, 1, 0)), t.x);
            var c = math.lerp(Read(point + new int3(0, 0, 1)), Read(point + new int3(1, 0, 1)), t.x);
            var d = math.lerp(Read(point + new int3(0, 1, 1)), Read(point + new int3(1, 1, 1)), t.x);
            return math.lerp(math.lerp(a, b, t.y), math.lerp(c, d, t.y), t.z);
        }
        private static void Dispose(IslandMeshJob job) { job.Vertices.Dispose(); job.Normals.Dispose(); job.Colors.Dispose(); }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
