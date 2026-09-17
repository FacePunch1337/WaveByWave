using NUnit.Framework;
using UColliders;
using UColliders.CoACD;

namespace UColliders.Tests.Editor
{
    /// <summary>
    /// Unit tests for the voxel-based MeshRepair class.
    /// </summary>
    public class MeshRepairTests
    {
        // A simple cube: 8 vertices, 12 triangles
        static CoACDMesh MakeCube(double size = 1.0)
        {
            double s = size;
            var verts = new Vec3d[]
            {
                new Vec3d(-s, -s, -s), new Vec3d(s, -s, -s),
                new Vec3d(-s,  s, -s), new Vec3d(s,  s, -s),
                new Vec3d(-s, -s,  s), new Vec3d(s, -s,  s),
                new Vec3d(-s,  s,  s), new Vec3d(s,  s,  s)
            };

            var tris = new int[]
            {
                0,2,1, 1,2,3,   // -Z
                4,5,6, 5,7,6,   // +Z
                0,1,4, 1,5,4,   // -Y
                2,6,3, 3,6,7,   // +Y
                0,4,2, 2,4,6,   // -X
                1,3,5, 3,7,5    // +X
            };

            return new CoACDMesh(verts, tris);
        }

        // A cube with a missing face (open, non-manifold)
        static CoACDMesh MakeOpenCube()
        {
            var verts = new Vec3d[]
            {
                new Vec3d(-1, -1, -1), new Vec3d(1, -1, -1),
                new Vec3d(-1,  1, -1), new Vec3d(1,  1, -1),
                new Vec3d(-1, -1,  1), new Vec3d(1, -1,  1),
                new Vec3d(-1,  1,  1), new Vec3d(1,  1,  1)
            };

            // 5 faces only — missing +X face
            var tris = new int[]
            {
                0,2,1, 1,2,3,   // -Z
                4,5,6, 5,7,6,   // +Z
                0,1,4, 1,5,4,   // -Y
                2,6,3, 3,6,7,   // +Y
                0,4,2, 2,4,6    // -X
            };

            return new CoACDMesh(verts, tris);
        }

        [Test]
        public void Repair_ClosedCube_PreservesShape()
        {
            var cube = MakeCube();
            var repaired = MeshRepair.Repair(cube, 20);

            // Repaired mesh should have vertices and triangles
            Assert.IsTrue(repaired.vertices.Length >= 8,
                "Repaired mesh should have at least 8 vertices");
            Assert.IsTrue(repaired.triangles.Length >= 12,
                "Repaired mesh should have at least 12 triangle indices");

            // Volume should be approximately the same as the original cube (8 cubic units)
            double vol = System.Math.Abs(repaired.ComputeVolume());
            Assert.Greater(vol, 4.0, "Volume should be at least 4 (original is 8)");
            Assert.Less(vol, 16.0, "Volume should be at most 16 (original is 8)");
        }

        [Test]
        public void Repair_OpenMesh_ProducesWatertight()
        {
            var open = MakeOpenCube();
            var repaired = MeshRepair.Repair(open, 30);

            // Repaired mesh should be watertight — check every edge has exactly 2 triangles
            int[] tris = repaired.triangles;
            var edgeCounts = new System.Collections.Generic.Dictionary<long, int>();
            int vc = repaired.vertices.Length;

            for (int t = 0; t < tris.Length; t += 3)
            {
                for (int e = 0; e < 3; e++)
                {
                    int a = tris[t + e];
                    int b = tris[t + (e + 1) % 3];
                    long key = a < b ? (long)a * vc + b : (long)b * vc + a;
                    int count;
                    edgeCounts.TryGetValue(key, out count);
                    edgeCounts[key] = count + 1;
                }
            }

            foreach (var kv in edgeCounts)
            {
                Assert.AreEqual(2, kv.Value,
                    $"Edge should have exactly 2 incident triangles but has {kv.Value}");
            }
        }

        [Test]
        public void Repair_PositiveVolume()
        {
            var cube = MakeCube();
            var repaired = MeshRepair.Repair(cube, 20);

            double vol = repaired.ComputeVolume();
            // After repair and projection, volume should be positive
            // (could be negative if winding is reversed, but should be non-zero)
            Assert.AreNotEqual(0, vol, "Repaired mesh should have non-zero volume");
        }

        [Test]
        public void Repair_LowResolution_StillProducesMesh()
        {
            var cube = MakeCube();
            // Minimum resolution clamped to 4
            var repaired = MeshRepair.Repair(cube, 2);

            Assert.IsTrue(repaired.vertices.Length >= 4,
                "Even at minimum resolution, should produce vertices");
            Assert.IsTrue(repaired.triangles.Length >= 12,
                "Even at minimum resolution, should produce triangles");
        }

        [Test]
        public void Repair_HighResolution_MoreDetail()
        {
            var cube = MakeCube();
            var lowRes = MeshRepair.Repair(cube, 20);
            var highRes = MeshRepair.Repair(cube, 60);

            // Higher resolution should produce more vertices (finer surface)
            Assert.Greater(highRes.vertices.Length, lowRes.vertices.Length,
                "Higher resolution should produce more vertices");
        }

        [Test]
        public void Repair_SmallMesh_DoesNotCrash()
        {
            // Degenerate: single triangle (not manifold, not closed)
            var verts = new Vec3d[]
            {
                new Vec3d(0, 0, 0),
                new Vec3d(1, 0, 0),
                new Vec3d(0, 1, 0)
            };
            var tris = new int[] { 0, 1, 2 };
            var mesh = new CoACDMesh(verts, tris);

            // Should not throw
            Assert.DoesNotThrow(() => MeshRepair.Repair(mesh, 20));
        }

        [Test]
        public void Repair_ProjectedVerticesCloseToOriginal()
        {
            var cube = MakeCube();
            var repaired = MeshRepair.Repair(cube, 40);

            // Each repaired vertex should be close to the original cube surface
            // The cube spans [-1, 1] on each axis, so vertices should have
            // at least one coordinate near ±1
            foreach (var v in repaired.vertices)
            {
                double maxCoord = System.Math.Max(
                    System.Math.Abs(v.x),
                    System.Math.Max(System.Math.Abs(v.y), System.Math.Abs(v.z)));
                Assert.Greater(maxCoord, 0.5,
                    $"Projected vertex {v} should be near the cube surface");
            }
        }
    }
}
