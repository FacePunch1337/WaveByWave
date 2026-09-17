using NUnit.Framework;
using UnityEngine;
using UColliders;
using UColliders.OBBTree;

namespace UColliders.Tests.Editor
{
    /// <summary>
    /// Unit tests for the OBB struct and OBBTreeNode class.
    /// </summary>
    public class OBBTests
    {
        // A simple cube: 8 vertices, 12 triangles (36 indices)
        static readonly Vector3[] CubeVertices = new Vector3[]
        {
            new Vector3(-1, -1, -1), new Vector3(1, -1, -1),
            new Vector3(-1,  1, -1), new Vector3(1,  1, -1),
            new Vector3(-1, -1,  1), new Vector3(1, -1,  1),
            new Vector3(-1,  1,  1), new Vector3(1,  1,  1)
        };

        static readonly int[] CubeTriangles = new int[]
        {
            0,2,1, 1,2,3,   // -Z face
            4,5,6, 5,7,6,   // +Z face
            0,1,4, 1,5,4,   // -Y face
            2,6,3, 3,6,7,   // +Y face
            0,4,2, 2,4,6,   // -X face
            1,3,5, 3,7,5    // +X face
        };

        [Test]
        public void Barycenter_ReturnsAreaWeightedCentroid()
        {
            // Two triangles forming a square: (0,0,0)-(4,0,0)-(0,4,0) and (4,0,0)-(4,4,0)-(0,4,0)
            // Both triangles have equal area, centroids at (4/3, 4/3, 0) and (8/3, 8/3, 0)
            // Area-weighted centroid = average of the two centroids = (2, 2, 0)
            var obb = new OBB { triangles = new int[] { 0, 1, 2, 1, 3, 2 } };
            var vertices = new Vector3[]
            {
                new Vector3(0, 0, 0),
                new Vector3(4, 0, 0),
                new Vector3(0, 4, 0),
                new Vector3(4, 4, 0)
            };
            var result = obb.Barycenter(vertices);
            Assert.AreEqual(2f, result.x, 0.001f);
            Assert.AreEqual(2f, result.y, 0.001f);
            Assert.AreEqual(0f, result.z, 0.001f);
        }

        [Test]
        public void Barycenter_WithCube_ReturnsCenterOfMass()
        {
            var obb = new OBB { triangles = CubeTriangles };
            var result = obb.Barycenter(CubeVertices);
            // Symmetric cube centered at origin: barycenter should be near zero
            Assert.AreEqual(0f, result.x, 0.01f);
            Assert.AreEqual(0f, result.y, 0.01f);
            Assert.AreEqual(0f, result.z, 0.01f);
        }

        [Test]
        public void EstimateSurfaceCoverage_BoxLikePart_IsAcceptedAsOneBoard()
        {
            var obb = new OBB().BuildOBB(CubeVertices, CubeTriangles);

            float coverage = obb.EstimateSurfaceCoverage(CubeVertices);

            Assert.GreaterOrEqual(coverage, UCollidersRoot.AdaptiveSurfaceCoverageThreshold,
                $"A filled rectangular part should remain one fitted OBB, coverage was {coverage:F3}.");
        }

        [Test]
        public void EstimateSurfaceCoverage_SeparatedParts_RequireSubdivision()
        {
            var vertices = new Vector3[CubeVertices.Length * 2];
            for (int i = 0; i < CubeVertices.Length; i++)
            {
                vertices[i] = CubeVertices[i] + Vector3.left * 3f;
                vertices[i + CubeVertices.Length] = CubeVertices[i] + Vector3.right * 3f;
            }

            var triangles = new int[CubeTriangles.Length * 2];
            for (int i = 0; i < CubeTriangles.Length; i++)
            {
                triangles[i] = CubeTriangles[i];
                triangles[i + CubeTriangles.Length] = CubeTriangles[i] + CubeVertices.Length;
            }

            var obb = new OBB().BuildOBB(vertices, triangles);
            float coverage = obb.EstimateSurfaceCoverage(vertices);

            Assert.Less(coverage, UCollidersRoot.AdaptiveSurfaceCoverageThreshold,
                $"An OBB spanning empty space between parts must be subdivided, coverage was {coverage:F3}.");
        }

        [Test]
        public void Barycenter_DensityIndependent_UnevenTessellation()
        {
            // One large triangle on the left, many small triangles on the right.
            // Area-weighted barycenter should be near the geometric center,
            // not biased toward the densely tessellated side.
            var vertices = new Vector3[]
            {
                // Large triangle (left half): area = 2
                new Vector3(-2, -1, 0), // 0
                new Vector3( 0, -1, 0), // 1
                new Vector3(-2,  1, 0), // 2
                // Small triangles (right half): 4 triangles, total area = 2
                new Vector3( 0,  1, 0), // 3
                new Vector3( 1, -1, 0), // 4
                new Vector3( 1,  1, 0), // 5
                new Vector3( 2, -1, 0), // 6
                new Vector3( 2,  1, 0), // 7
            };
            var triangles = new int[]
            {
                0, 1, 2,       // large left triangle
                1, 4, 3,       // right quad, lower-left
                4, 5, 3,       // right quad, upper-left
                4, 6, 5,       // right quad, lower-right
                6, 7, 5,       // right quad, upper-right
            };
            var obb = new OBB { triangles = triangles };
            var result = obb.Barycenter(vertices);
            // With equal total area on left and right, centroid x should be near 0
            Assert.AreEqual(0f, result.x, 0.35f, "Area-weighted barycenter should not be biased by tessellation density");
        }

        [Test]
        public void BuildOBB_BoundsEncapsulateAllVertices()
        {
            var obb = new OBB();
            obb = obb.BuildOBB(CubeVertices, CubeTriangles);

            Quaternion inv = Quaternion.Inverse(obb.orientation);
            foreach (var v in CubeVertices)
            {
                Vector3 local = inv * v;
                Assert.IsTrue(
                    obb.bounds.Contains(local),
                    $"Vertex {v} (local: {local}) not contained in bounds {obb.bounds}"
                );
            }
        }

        [Test]
        public void BuildOBB_ProducesNonDegenerateBounds()
        {
            var obb = new OBB();
            obb = obb.BuildOBB(CubeVertices, CubeTriangles);

            Assert.Greater(obb.bounds.size.x, 0f);
            Assert.Greater(obb.bounds.size.y, 0f);
            Assert.Greater(obb.bounds.size.z, 0f);
        }

        [Test]
        public void BuildOBB_SetsValidAxesOrder()
        {
            var obb = new OBB();
            obb = obb.BuildOBB(CubeVertices, CubeTriangles);

            // axesOrder should be a permutation of {0, 1, 2}
            bool has0 = obb.axesOrder[0] == 0 || obb.axesOrder[1] == 0 || obb.axesOrder[2] == 0;
            bool has1 = obb.axesOrder[0] == 1 || obb.axesOrder[1] == 1 || obb.axesOrder[2] == 1;
            bool has2 = obb.axesOrder[0] == 2 || obb.axesOrder[1] == 2 || obb.axesOrder[2] == 2;
            Assert.IsTrue(has0 && has1 && has2,
                $"axesOrder {obb.axesOrder} is not a valid permutation of (0,1,2)");
        }

        [Test]
        public void BuildOBB_SetsTrianglesWhenProvidedAsParameter()
        {
            var obb = new OBB();
            obb = obb.BuildOBB(CubeVertices, CubeTriangles);
            Assert.AreEqual(CubeTriangles.Length, obb.triangles.Length);
        }

        [Test]
        public void BuildOBB_SetsValidOrientation()
        {
            var obb = new OBB();
            obb = obb.BuildOBB(CubeVertices, CubeTriangles);

            // Quaternion should be normalized (unit quaternion)
            float mag = Mathf.Sqrt(
                obb.orientation.x * obb.orientation.x +
                obb.orientation.y * obb.orientation.y +
                obb.orientation.z * obb.orientation.z +
                obb.orientation.w * obb.orientation.w
            );
            Assert.AreEqual(1f, mag, 0.001f);
        }

        [Test]
        public void ComputeChildren_SplitsCubeIntoTwoChildren()
        {
            var obb = new OBB();
            obb = obb.BuildOBB(CubeVertices, CubeTriangles);

            Vector3[] verts = (Vector3[])CubeVertices.Clone();
            var children = obb.ComputeChildren(ref verts, "");
            Assert.AreEqual(2, children.Length);
            Assert.IsNotNull(children[0]);
            Assert.IsNotNull(children[1]);
            Assert.Greater(children[0].obb.triangles.Length, 0);
            Assert.Greater(children[1].obb.triangles.Length, 0);
        }

        [Test]
        public void ComputeChildren_ChildrenHaveDistinctIds()
        {
            var obb = new OBB();
            obb = obb.BuildOBB(CubeVertices, CubeTriangles);

            Vector3[] verts = (Vector3[])CubeVertices.Clone();
            var children = obb.ComputeChildren(ref verts, "");
            Assert.AreNotEqual(children[0].id, children[1].id);
        }

        [Test]
        public void ComputeChildren_ChildrenHaveValidBounds()
        {
            var obb = new OBB();
            obb = obb.BuildOBB(CubeVertices, CubeTriangles);

            Vector3[] verts = (Vector3[])CubeVertices.Clone();
            var children = obb.ComputeChildren(ref verts, "");
            for (int c = 0; c < 2; c++)
            {
                Vector3 size = children[c].obb.bounds.size;
                // At least 2 dimensions must be non-zero (children may be planar
                // when the split plane is parallel to a face, e.g. on a symmetric cube
                // where area-weighted covariance produces an axis-aligned OBB).
                int nonZero = (size.x > 0f ? 1 : 0) + (size.y > 0f ? 1 : 0) + (size.z > 0f ? 1 : 0);
                Assert.GreaterOrEqual(nonZero, 2,
                    $"Child {c} bounds {size} should have at least 2 non-zero dimensions");
            }
        }

        [Test]
        public void InnerSubdivide_BothSidesReceiveTriangles()
        {
            var obb = new OBB();
            obb = obb.BuildOBB(CubeVertices, CubeTriangles);

            Quaternion inv = Quaternion.Inverse(obb.orientation);
            var result = obb.InnerSubdivide(CubeVertices, obb.axesOrder[0], inv, obb.bounds.center[obb.axesOrder[0]]);

            Assert.Greater(result[0].Count, 0, "Positive side should have triangles");
            Assert.Greater(result[1].Count, 0, "Negative side should have triangles");
        }

        [Test]
        public void InnerSubdivide_MayDuplicateStraddlingTriangles()
        {
            var obb = new OBB();
            obb = obb.BuildOBB(CubeVertices, CubeTriangles);

            Quaternion inv = Quaternion.Inverse(obb.orientation);
            var result = obb.InnerSubdivide(CubeVertices, obb.axesOrder[0], inv, obb.bounds.center[obb.axesOrder[0]]);

            // InnerSubdivide can assign a triangle to both sides if it straddles the plane
            Assert.GreaterOrEqual(result[0].Count + result[1].Count, CubeTriangles.Length);
        }

        [Test]
        public void OuterSubdivide_BothSidesReceiveTriangles()
        {
            var obb = new OBB();
            obb = obb.BuildOBB(CubeVertices, CubeTriangles);

            Quaternion inv = Quaternion.Inverse(obb.orientation);
            var result = obb.OuterSubdivide(CubeVertices, obb.axesOrder[0], inv, obb.bounds.center[obb.axesOrder[0]]);

            Assert.Greater(result[0].Count, 0, "Positive side should have triangles");
            Assert.Greater(result[1].Count, 0, "Negative side should have triangles");
        }

        [Test]
        public void OuterSubdivide_DoesNotDuplicateTriangles()
        {
            var obb = new OBB();
            obb = obb.BuildOBB(CubeVertices, CubeTriangles);

            Quaternion inv = Quaternion.Inverse(obb.orientation);
            var result = obb.OuterSubdivide(CubeVertices, obb.axesOrder[0], inv, obb.bounds.center[obb.axesOrder[0]]);

            // OuterSubdivide only assigns a triangle if ALL vertices are on one side
            Assert.LessOrEqual(result[0].Count + result[1].Count, CubeTriangles.Length);
        }

        [Test]
        public void OuterSubdivide_TriangleIndicesAreMultiplesOfThree()
        {
            var obb = new OBB();
            obb = obb.BuildOBB(CubeVertices, CubeTriangles);

            Quaternion inv = Quaternion.Inverse(obb.orientation);
            var result = obb.OuterSubdivide(CubeVertices, obb.axesOrder[0], inv, obb.bounds.center[obb.axesOrder[0]]);

            Assert.AreEqual(0, result[0].Count % 3, "Positive side triangle count should be multiple of 3");
            Assert.AreEqual(0, result[1].Count % 3, "Negative side triangle count should be multiple of 3");
        }

        // --- Covariance tests ---

        [Test]
        public void Covariance_ReturnsSymmetricMatrix()
        {
            var obb = new OBB { triangles = CubeTriangles };
            var bary = obb.Barycenter(CubeVertices);
            var cov = obb.Covariance(CubeVertices, CubeTriangles, bary);

            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    Assert.AreEqual(cov[i, j], cov[j, i], 0.0001,
                        $"Covariance matrix not symmetric at [{i},{j}]");
        }

        [Test]
        public void Covariance_DiagonalIsNonNegative()
        {
            var obb = new OBB { triangles = CubeTriangles };
            var bary = obb.Barycenter(CubeVertices);
            var cov = obb.Covariance(CubeVertices, CubeTriangles, bary);

            for (int i = 0; i < 3; i++)
                Assert.GreaterOrEqual(cov[i, i], 0.0,
                    $"Covariance diagonal [{i},{i}] should be non-negative");
        }

        [Test]
        public void GetMainAxes_ReturnsThreeOrthogonalVectors()
        {
            var obb = new OBB { triangles = CubeTriangles };
            var bary = obb.Barycenter(CubeVertices);
            var axes = obb.GetMainAxes(CubeVertices, bary);

            Assert.AreEqual(3, axes.Length);
            // Check orthogonality
            Assert.AreEqual(0f, Vector3.Dot(axes[0], axes[1]), 0.01f, "axes[0] and axes[1] should be orthogonal");
            Assert.AreEqual(0f, Vector3.Dot(axes[0], axes[2]), 0.01f, "axes[0] and axes[2] should be orthogonal");
            Assert.AreEqual(0f, Vector3.Dot(axes[1], axes[2]), 0.01f, "axes[1] and axes[2] should be orthogonal");
        }

        [Test]
        public void GetMainAxes_VectorsAreUnitLength()
        {
            var obb = new OBB { triangles = CubeTriangles };
            var bary = obb.Barycenter(CubeVertices);
            var axes = obb.GetMainAxes(CubeVertices, bary);

            for (int i = 0; i < 3; i++)
                Assert.AreEqual(1f, axes[i].magnitude, 0.01f,
                    $"Axis {i} should be unit length, got {axes[i].magnitude}");
        }

        // --- Non-cubic geometry (elongated shape) ---

        static readonly Vector3[] ElongatedVertices = new Vector3[]
        {
            new Vector3(-10, -1, -1), new Vector3(10, -1, -1),
            new Vector3(-10,  1, -1), new Vector3(10,  1, -1),
            new Vector3(-10, -1,  1), new Vector3(10, -1,  1),
            new Vector3(-10,  1,  1), new Vector3(10,  1,  1)
        };

        [Test]
        public void BuildOBB_ElongatedShape_LongestAxisIsX()
        {
            var obb = new OBB();
            obb = obb.BuildOBB(ElongatedVertices, CubeTriangles);

            // The longest axis (axesOrder[0]) should correspond to the
            // largest extent in the OBB's local frame
            Vector3 extents = obb.bounds.extents;
            float longest = Mathf.Max(extents.x, Mathf.Max(extents.y, extents.z));
            Assert.AreEqual(longest, extents[obb.axesOrder[0]], 0.01f,
                "axesOrder[0] should index the longest extent");
        }

        [Test]
        public void BuildOBB_ElongatedShape_EncapsulatesAllVertices()
        {
            var obb = new OBB();
            obb = obb.BuildOBB(ElongatedVertices, CubeTriangles);

            Quaternion inv = Quaternion.Inverse(obb.orientation);
            foreach (var v in ElongatedVertices)
            {
                Vector3 local = inv * v;
                Assert.IsTrue(obb.bounds.Contains(local),
                    $"Vertex {v} (local: {local}) not contained in bounds {obb.bounds}");
            }
        }

        // --- ComputeChildren edge cases ---

        [Test]
        public void ComputeChildren_WithTooFewTriangles_ReturnsEmptyArray()
        {
            // A single triangle (3 indices) should not be subdividable
            var vertices = new Vector3[]
            {
                new Vector3(0, 0, 0),
                new Vector3(1, 0, 0),
                new Vector3(0, 1, 0),
                new Vector3(0.5f, 0.5f, 0)
            };
            var triangles = new int[] { 0, 1, 2 };

            var obb = new OBB();
            obb = obb.BuildOBB(vertices, triangles);
            var children = obb.ComputeChildren(ref vertices, "");

            Assert.AreEqual(0, children.Length,
                "Should return empty when triangles too few to split");
        }

        [Test]
        public void MinTriangleIndicesPerChild_IsPositive()
        {
            Assert.Greater(OBB.MinTriangleIndicesPerChild, 0);
        }

        [Test]
        public void ComputeChildren_WithInnerSubdivide_ProducesValidChildren()
        {
            var obb = new OBB();
            obb = obb.BuildOBB(CubeVertices, CubeTriangles);

            Vector3[] verts = (Vector3[])CubeVertices.Clone();
            var children = obb.ComputeChildren(ref verts, "", outerSubdivide: false);
            Assert.AreEqual(2, children.Length);
            Assert.Greater(children[0].obb.triangles.Length, 0);
            Assert.Greater(children[1].obb.triangles.Length, 0);
        }

        [Test]
        public void ComputeChildren_ChildTrianglesCoverParent()
        {
            var obb = new OBB();
            obb = obb.BuildOBB(CubeVertices, CubeTriangles);

            Vector3[] verts = (Vector3[])CubeVertices.Clone();
            var children = obb.ComputeChildren(ref verts, "");
            int totalChildTriangles = children[0].obb.triangles.Length + children[1].obb.triangles.Length;

            // SplittingSubdivide clips straddling triangles, so total may exceed parent count
            Assert.GreaterOrEqual(totalChildTriangles, CubeTriangles.Length,
                "Children should cover at least all parent triangles (clipped triangles add more)");
        }

        // --- Subdivide triangle count multiples ---

        [Test]
        public void InnerSubdivide_ResultCountsAreMultiplesOfThree()
        {
            var obb = new OBB();
            obb = obb.BuildOBB(CubeVertices, CubeTriangles);

            Quaternion inv = Quaternion.Inverse(obb.orientation);
            var result = obb.InnerSubdivide(CubeVertices, obb.axesOrder[0], inv, obb.bounds.center[obb.axesOrder[0]]);

            Assert.AreEqual(0, result[0].Count % 3, "Positive side should have triangle count multiple of 3");
            Assert.AreEqual(0, result[1].Count % 3, "Negative side should have triangle count multiple of 3");
        }
    }

    /// <summary>
    /// Unit tests for the OBBTreeNode class.
    /// </summary>
    public class OBBTreeNodeTests
    {
        [Test]
        public void Constructor_SetsId()
        {
            var node = new OBBTreeNode("01");
            Assert.AreEqual("01", node.id);
        }

        [Test]
        public void Constructor_InitializesChildrenToNullPair()
        {
            var node = new OBBTreeNode("root");
            Assert.AreEqual(2, node.children.Length);
            Assert.IsNull(node.children[0]);
            Assert.IsNull(node.children[1]);
        }

        [Test]
        public void Constructor_InitializesDefaultOBB()
        {
            var node = new OBBTreeNode("");
            // Default OBB struct should have null triangles
            Assert.IsNull(node.obb.triangles);
        }

        [Test]
        public void Constructor_EmptyId_IsValid()
        {
            var node = new OBBTreeNode("");
            Assert.AreEqual("", node.id);
            Assert.AreEqual(2, node.children.Length);
        }

        [Test]
        public void Children_CanBeReassigned()
        {
            var parent = new OBBTreeNode("");
            var child0 = new OBBTreeNode("0");
            var child1 = new OBBTreeNode("1");
            parent.children = new OBBTreeNode[] { child0, child1 };

            Assert.AreSame(child0, parent.children[0]);
            Assert.AreSame(child1, parent.children[1]);
        }

        [Test]
        public void Children_CanBeSetToEmptyArray()
        {
            var node = new OBBTreeNode("");
            node.children = new OBBTreeNode[0];
            Assert.AreEqual(0, node.children.Length);
        }
    }

    /// <summary>
    /// Unit tests for custom exception classes.
    /// </summary>
    public class UCollidersExceptionTests
    {
        [Test]
        public void ReadMeshDisabledException_HasDescriptiveMessage()
        {
            var ex = new ReadMeshDisabledException();
            Assert.IsTrue(ex.Message.Contains("Read/Write"),
                "Message should mention Read/Write setting");
        }

        [Test]
        public void TooFewVerticesException_IncludesVertexCount()
        {
            var ex = new TooFewVerticesException(2);
            Assert.IsTrue(ex.Message.Contains("2"),
                "Message should include vertex count");
        }

        [Test]
        public void CovarianceNotConvergentException_HasMessage()
        {
            var ex = new CovarianceNotConvergentException();
            Assert.IsNotEmpty(ex.Message);
        }

        [Test]
        public void NoMeshFilterException_HasMessage()
        {
            var ex = new NoMeshFilterException();
            Assert.IsTrue(ex.Message.Contains("MeshFilter"),
                "Message should mention MeshFilter");
            Assert.IsTrue(ex.Message.Contains("not detected"),
                "Message should say 'not detected'");
        }

        [Test]
        public void PathNotComputableException_IncludesPath()
        {
            var ex = new PathNotComputableException("0010");
            Assert.IsTrue(ex.Message.Contains("0010"),
                "Message should include the path");
        }
    }
}
