using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace WaveByWave.Generation
{
    public struct IslandFieldParameters
    {
        public int3 Points;
        public float3 Origin;
        public float CellSize, Diameter, SandHeight, SandDepth, NoiseScale, Irregularity;
        public uint Seed;
        public int Index(int3 p) => p.x + Points.x * (p.y + Points.y * p.z);
        public float3 Position(int3 p) => Origin + (float3)p * CellSize;

        public float2 InitialDensity(float3 p)
        {
            var offset = new float2(Seed % 919u, Seed % 613u);
            var r = Diameter * 0.5f;
            var uv = p.xz / math.max(0.5f, NoiseScale) + offset;
            var coastNoise = noise.cnoise(uv * 0.8f);
            var shore = r * (0.8f + Irregularity * coastNoise) - math.length(p.xz);
            var height = math.lerp(-0.6f, SandHeight,
                math.saturate(shore / math.max(0.6f, r * 0.6f))) + noise.cnoise(uv * 1.8f) * 0.22f;
            var bottom = p.y - Origin.y - CellSize;
            var sand = math.min(math.min(height - p.y, shore), bottom);
            var rockHeight = height - SandDepth * (0.85f + 0.15f * noise.cnoise(uv * 0.65f));
            var rock = math.min(math.min(rockHeight - p.y, shore + CellSize * 0.5f), bottom);
            return new float2(sand, rock);
        }
    }

    [BurstCompile(FloatMode = FloatMode.Strict)]
    public struct IslandInitializeJob : IJobParallelFor
    {
        public IslandFieldParameters Parameters;
        [WriteOnly] public NativeArray<float2> Density;
        public void Execute(int i)
        {
            var p = new int3(i % Parameters.Points.x,
                (i / Parameters.Points.x) % Parameters.Points.y,
                i / (Parameters.Points.x * Parameters.Points.y));
            Density[i] = Parameters.InitialDensity(Parameters.Position(p));
        }
    }

    // Positive values are solid. Subtract a sphere from sand, never from bedrock.
    [BurstCompile(FloatMode = FloatMode.Strict)]
    public struct IslandDigJob : IJob
    {
        public IslandFieldParameters Parameters;
        public NativeArray<float2> Density;
        public float3 Center;
        public float Radius;
        public float Smoothing;
        public int3 Minimum, Maximum;
        private static float SmoothMinimum(float a, float b, float smoothing)
        {
            if (smoothing <= 0.0001f) return math.min(a, b);
            var blend = math.max(smoothing - math.abs(a - b), 0f) / smoothing;
            return math.min(a, b) - blend * blend * smoothing * 0.25f;
        }
        public void Execute()
        {
            for (var z = Minimum.z; z <= Maximum.z; z++)
            for (var y = Minimum.y; y <= Maximum.y; y++)
            for (var x = Minimum.x; x <= Maximum.x; x++)
            {
                var p = new int3(x, y, z);
                var i = Parameters.Index(p);
                var value = Density[i];
                value.x = SmoothMinimum(value.x,
                    math.distance(Parameters.Position(p), Center) - Radius, Smoothing);
                Density[i] = value;
            }
        }
    }

    // Surface Nets places one stable vertex in every intersected cell and joins four neighbouring
    // cells around each sign-changing grid edge. Unlike tetrahedral extraction, repeated spherical
    // digs cannot leave thin diagonal shards along their intersections. Neighbouring chunks query
    // the same halo cells, so their shared edge vertices remain bit-identical.
    [BurstCompile(FloatMode = FloatMode.Strict)]
    public struct IslandMeshJob : IJob
    {
        [ReadOnly] public NativeArray<float2> Density;
        public IslandFieldParameters Parameters;
        public int3 Minimum, Maximum;
        public NativeList<float3> Vertices, Normals;
        public NativeList<Color32> Colors;

        private float Solid(int3 p)
        {
            var v = Density[Parameters.Index(math.clamp(p, int3.zero, Parameters.Points - 1))];
            return math.max(v.x, v.y);
        }
        private float Solid(float3 position)
        {
            var grid = (position - Parameters.Origin) / Parameters.CellSize;
            var point = math.clamp((int3)math.floor(grid), int3.zero, Parameters.Points - 2);
            var t = math.saturate(math.frac(grid));
            var a = math.lerp(Solid(point), Solid(point + new int3(1, 0, 0)), t.x);
            var b = math.lerp(Solid(point + new int3(0, 1, 0)), Solid(point + new int3(1, 1, 0)), t.x);
            var c = math.lerp(Solid(point + new int3(0, 0, 1)), Solid(point + new int3(1, 0, 1)), t.x);
            var d = math.lerp(Solid(point + new int3(0, 1, 1)), Solid(point + new int3(1, 1, 1)), t.x);
            return math.lerp(math.lerp(a, b, t.y), math.lerp(c, d, t.y), t.z);
        }
        private float3 Normal(int3 p) => math.normalizesafe(new float3(
            Solid(p - new int3(1, 0, 0)) - Solid(p + new int3(1, 0, 0)),
            Solid(p - new int3(0, 1, 0)) - Solid(p + new int3(0, 1, 0)),
            Solid(p - new int3(0, 0, 1)) - Solid(p + new int3(0, 0, 1))), new float3(0, 1, 0));

        private struct Sample
        {
            public float3 Position, Normal;
            public float Value, Rock;
        }
        private Sample Read(int3 p)
        {
            var d = Density[Parameters.Index(p)];
            return new Sample { Position = Parameters.Position(p), Normal = Normal(p),
                Value = math.max(d.x, d.y), Rock = d.y > d.x + 0.015f ? 1f : 0f };
        }
        private static Sample Edge(Sample a, Sample b)
        {
            // A dig sphere can pass exactly through a lattice point. An interpolation value of
            // exactly 0/1 then collapses several neighbouring cell vertices onto that point and
            // leaves a tiny triangular slit. Keep the crossing infinitesimally inside the edge.
            var t = math.clamp(a.Value / (a.Value - b.Value), 0.001f, 0.999f);
            return new Sample { Position = math.lerp(a.Position, b.Position, t),
                Normal = math.normalizesafe(math.lerp(a.Normal, b.Normal, t), new float3(0, 1, 0)),
                Rock = math.lerp(a.Rock, b.Rock, t) };
        }
        private void Add(Sample s)
        {
            Vertices.Add(s.Position); Normals.Add(s.Normal);
            Colors.Add(new Color32((byte)(math.saturate(s.Rock) * 255f), 0, 0, 255));
        }
        private static Sample FaceNormal(Sample sample, float3 normal)
        {
            // The density gradient is undefined exactly where the sand/bedrock union changes
            // branch. Keep smooth SDF normals, but never let one point into the solid and turn
            // a whole interpolated triangle dark or make its winding look inverted.
            if (math.dot(sample.Normal, normal) < 0f) sample.Normal = -sample.Normal;
            sample.Normal = math.normalizesafe(sample.Normal, normal);
            return sample;
        }
        private void Triangle(Sample a, Sample b, Sample c, float3 outward)
        {
            var cross = math.cross(b.Position - a.Position, c.Position - a.Position);
            if (math.lengthsq(cross) < 0.000000001f) return;
            // Winding used to depend on interpolated vertex normals. At a freshly exposed
            // sand/rock seam those gradients may point in different directions, producing
            // isolated back-facing triangles. The inside/outside samples of the tetrahedron
            // provide an unambiguous outward direction instead.
            var direction = math.normalizesafe(cross, math.normalizesafe(outward, new float3(0, 1, 0)));
            var center = (a.Position + b.Position + c.Position) / 3f;
            var probe = Parameters.CellSize * 0.12f;
            var positive = Solid(center + direction * probe);
            var negative = Solid(center - direction * probe);
            if (positive > negative + 0.00001f ||
                (math.abs(positive - negative) <= 0.00001f && math.dot(cross, outward) < 0f))
            { var swap = b; b = c; c = swap; cross = -cross; }
            var normal = math.normalizesafe(cross, math.normalizesafe(outward, new float3(0, 1, 0)));
            Add(FaceNormal(a, normal)); Add(FaceNormal(b, normal)); Add(FaceNormal(c, normal));
        }
        private static void Accumulate(ref Sample result, ref int count, Sample a, Sample b)
        {
            if ((a.Value > 0f) == (b.Value > 0f)) return;
            var edge = Edge(a, b);
            result.Position += edge.Position; result.Normal += edge.Normal; result.Rock += edge.Rock; count++;
        }
        private bool CellVertex(int3 p, out Sample result)
        {
            result = default;
            if (math.any(p < 0) || math.any(p >= Parameters.Points - 1)) return false;
            var a = Read(p); var b = Read(p + new int3(1, 0, 0));
            var c = Read(p + new int3(1, 1, 0)); var d = Read(p + new int3(0, 1, 0));
            var e = Read(p + new int3(0, 0, 1)); var f = Read(p + new int3(1, 0, 1));
            var g = Read(p + new int3(1, 1, 1)); var h = Read(p + new int3(0, 1, 1));
            var count = 0;
            Accumulate(ref result, ref count, a, b); Accumulate(ref result, ref count, b, c);
            Accumulate(ref result, ref count, c, d); Accumulate(ref result, ref count, d, a);
            Accumulate(ref result, ref count, e, f); Accumulate(ref result, ref count, f, g);
            Accumulate(ref result, ref count, g, h); Accumulate(ref result, ref count, h, e);
            Accumulate(ref result, ref count, a, e); Accumulate(ref result, ref count, b, f);
            Accumulate(ref result, ref count, c, g); Accumulate(ref result, ref count, d, h);
            if (count == 0) return false;
            result.Position /= count; result.Normal = math.normalizesafe(result.Normal, new float3(0, 1, 0));
            result.Rock /= count;
            return true;
        }
        private void Quad(int3 a, int3 b, int3 c, int3 d, float3 outward)
        {
            if (!CellVertex(a, out var va) || !CellVertex(b, out var vb) ||
                !CellVertex(c, out var vc) || !CellVertex(d, out var vd)) return;
            // Keep the split compact. A fixed diagonal becomes a long visible crease whenever
            // the Surface Nets vertex is pulled towards a corner by overlapping excavations.
            if (math.distancesq(va.Position, vc.Position) <= math.distancesq(vb.Position, vd.Position))
            { Triangle(va, vb, vc, outward); Triangle(va, vc, vd, outward); }
            else
            { Triangle(va, vb, vd, outward); Triangle(vb, vc, vd, outward); }
        }
        public void Execute()
        {
            for (var z = Minimum.z; z < Maximum.z; z++)
            for (var y = Minimum.y; y < Maximum.y; y++)
            for (var x = Minimum.x; x < Maximum.x; x++)
            {
                var p = new int3(x, y, z);
                var value = Solid(p) > 0f;
                if (y > 0 && z > 0 && value != (Solid(p + new int3(1, 0, 0)) > 0f))
                    Quad(p, p - new int3(0, 1, 0), p - new int3(0, 1, 1), p - new int3(0, 0, 1),
                        value ? new float3(1, 0, 0) : new float3(-1, 0, 0));
                if (x > 0 && z > 0 && value != (Solid(p + new int3(0, 1, 0)) > 0f))
                    Quad(p, p - new int3(0, 0, 1), p - new int3(1, 0, 1), p - new int3(1, 0, 0),
                        value ? new float3(0, 1, 0) : new float3(0, -1, 0));
                if (x > 0 && y > 0 && value != (Solid(p + new int3(0, 0, 1)) > 0f))
                    Quad(p, p - new int3(1, 0, 0), p - new int3(1, 1, 0), p - new int3(0, 1, 0),
                        value ? new float3(0, 0, 1) : new float3(0, 0, -1));
            }
        }
    }
}
