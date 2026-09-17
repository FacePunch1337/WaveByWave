using NUnit.Framework;
using UnityEngine;
using UColliders;
using UColliders.OBBTree;

namespace UColliders.Tests.Editor
{
    /// <summary>
    /// Unit tests for the SymmetricEigen3x3 Jacobi eigenvalue decomposition.
    /// Verifies correctness, orthogonality, and edge cases that were previously
    /// handled by Math.NET Numerics.
    /// </summary>
    public class SymmetricEigen3x3Tests
    {
        /// <summary>
        /// Helper: reconstruct A from eigenvalues and eigenvectors and check A ≈ V * D * V^T.
        /// </summary>
        static void AssertReconstruction(double[,] original, double[] eigenvalues, double[,] eigenvectors, double tolerance = 1e-6)
        {
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    double sum = 0;
                    for (int k = 0; k < 3; k++)
                        sum += eigenvectors[i, k] * eigenvalues[k] * eigenvectors[j, k];
                    Assert.AreEqual(original[i, j], sum, tolerance,
                        $"Reconstruction failed at [{i},{j}]: expected {original[i, j]}, got {sum}");
                }
            }
        }

        /// <summary>
        /// Helper: check eigenvectors are orthonormal.
        /// </summary>
        static void AssertOrthonormal(double[,] eigenvectors, double tolerance = 1e-6)
        {
            for (int i = 0; i < 3; i++)
            {
                // Unit length
                double len = 0;
                for (int k = 0; k < 3; k++)
                    len += eigenvectors[k, i] * eigenvectors[k, i];
                Assert.AreEqual(1.0, len, tolerance, $"Eigenvector column {i} is not unit length: {len}");

                // Orthogonal to other columns
                for (int j = i + 1; j < 3; j++)
                {
                    double dot = 0;
                    for (int k = 0; k < 3; k++)
                        dot += eigenvectors[k, i] * eigenvectors[k, j];
                    Assert.AreEqual(0.0, dot, tolerance,
                        $"Eigenvectors {i} and {j} are not orthogonal: dot = {dot}");
                }
            }
        }

        // --- Identity matrix ---

        [Test]
        public void Identity_EigenvaluesAreAllOnes()
        {
            double[,] matrix = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            double[] eigenvalues;
            double[,] eigenvectors;
            SymmetricEigen3x3.Decompose(matrix, out eigenvalues, out eigenvectors);

            for (int i = 0; i < 3; i++)
                Assert.AreEqual(1.0, eigenvalues[i], 1e-10);
        }

        [Test]
        public void Identity_EigenvectorsAreOrthonormal()
        {
            double[,] matrix = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            double[] eigenvalues;
            double[,] eigenvectors;
            SymmetricEigen3x3.Decompose(matrix, out eigenvalues, out eigenvectors);
            AssertOrthonormal(eigenvectors);
        }

        // --- Diagonal matrix ---

        [Test]
        public void Diagonal_EigenvaluesSortedAscending()
        {
            double[,] matrix = { { 5, 0, 0 }, { 0, 2, 0 }, { 0, 0, 8 } };
            double[] eigenvalues;
            double[,] eigenvectors;
            SymmetricEigen3x3.Decompose(matrix, out eigenvalues, out eigenvectors);

            Assert.AreEqual(2.0, eigenvalues[0], 1e-10);
            Assert.AreEqual(5.0, eigenvalues[1], 1e-10);
            Assert.AreEqual(8.0, eigenvalues[2], 1e-10);
        }

        [Test]
        public void Diagonal_ReconstructsOriginalMatrix()
        {
            double[,] matrix = { { 5, 0, 0 }, { 0, 2, 0 }, { 0, 0, 8 } };
            double[,] original = (double[,])matrix.Clone();
            double[] eigenvalues;
            double[,] eigenvectors;
            SymmetricEigen3x3.Decompose(matrix, out eigenvalues, out eigenvectors);
            AssertReconstruction(original, eigenvalues, eigenvectors);
        }

        // --- General symmetric matrix ---

        [Test]
        public void GeneralSymmetric_EigenvectorsAreOrthonormal()
        {
            double[,] matrix = { { 2, 1, 0 }, { 1, 3, 1 }, { 0, 1, 2 } };
            double[] eigenvalues;
            double[,] eigenvectors;
            SymmetricEigen3x3.Decompose(matrix, out eigenvalues, out eigenvectors);
            AssertOrthonormal(eigenvectors);
        }

        [Test]
        public void GeneralSymmetric_ReconstructsOriginalMatrix()
        {
            double[,] matrix = { { 2, 1, 0 }, { 1, 3, 1 }, { 0, 1, 2 } };
            double[,] original = (double[,])matrix.Clone();
            double[] eigenvalues;
            double[,] eigenvectors;
            SymmetricEigen3x3.Decompose(matrix, out eigenvalues, out eigenvectors);
            AssertReconstruction(original, eigenvalues, eigenvectors);
        }

        [Test]
        public void GeneralSymmetric_EigenvaluesAreAscending()
        {
            double[,] matrix = { { 2, 1, 0 }, { 1, 3, 1 }, { 0, 1, 2 } };
            double[] eigenvalues;
            double[,] eigenvectors;
            SymmetricEigen3x3.Decompose(matrix, out eigenvalues, out eigenvectors);

            Assert.LessOrEqual(eigenvalues[0], eigenvalues[1]);
            Assert.LessOrEqual(eigenvalues[1], eigenvalues[2]);
        }

        // --- Satisfies Av = λv ---

        [Test]
        public void GeneralSymmetric_EigenvectorEquation()
        {
            double[,] matrix = { { 4, 2, 1 }, { 2, 5, 3 }, { 1, 3, 6 } };
            double[,] original = (double[,])matrix.Clone();
            double[] eigenvalues;
            double[,] eigenvectors;
            SymmetricEigen3x3.Decompose(matrix, out eigenvalues, out eigenvectors);

            for (int col = 0; col < 3; col++)
            {
                for (int row = 0; row < 3; row++)
                {
                    double av = 0;
                    for (int k = 0; k < 3; k++)
                        av += original[row, k] * eigenvectors[k, col];
                    double lambdaV = eigenvalues[col] * eigenvectors[row, col];
                    Assert.AreEqual(lambdaV, av, 1e-6,
                        $"Av != λv for eigenvalue {col} at row {row}");
                }
            }
        }

        // --- Zero matrix ---

        [Test]
        public void ZeroMatrix_AllEigenvaluesZero()
        {
            double[,] matrix = new double[3, 3];
            double[] eigenvalues;
            double[,] eigenvectors;
            SymmetricEigen3x3.Decompose(matrix, out eigenvalues, out eigenvectors);

            for (int i = 0; i < 3; i++)
                Assert.AreEqual(0.0, eigenvalues[i], 1e-10);
        }

        [Test]
        public void ZeroMatrix_EigenvectorsAreOrthonormal()
        {
            double[,] matrix = new double[3, 3];
            double[] eigenvalues;
            double[,] eigenvectors;
            SymmetricEigen3x3.Decompose(matrix, out eigenvalues, out eigenvectors);
            AssertOrthonormal(eigenvectors);
        }

        // --- Repeated eigenvalues ---

        [Test]
        public void RepeatedEigenvalues_StillOrthonormal()
        {
            // 2*I has eigenvalue 2 with multiplicity 3
            double[,] matrix = { { 2, 0, 0 }, { 0, 2, 0 }, { 0, 0, 2 } };
            double[] eigenvalues;
            double[,] eigenvectors;
            SymmetricEigen3x3.Decompose(matrix, out eigenvalues, out eigenvectors);

            for (int i = 0; i < 3; i++)
                Assert.AreEqual(2.0, eigenvalues[i], 1e-10);
            AssertOrthonormal(eigenvectors);
        }

        // --- Covariance matrix from OBB pipeline ---

        [Test]
        public void CubeCovarianceMatrix_MatchesExpectedEigenvalues()
        {
            // Build the same covariance matrix that the OBB pipeline would produce for a unit cube
            Vector3[] cubeVertices = {
                new Vector3(-1, -1, -1), new Vector3(1, -1, -1),
                new Vector3(-1,  1, -1), new Vector3(1,  1, -1),
                new Vector3(-1, -1,  1), new Vector3(1, -1,  1),
                new Vector3(-1,  1,  1), new Vector3(1,  1,  1)
            };
            int[] cubeTriangles = {
                0,2,1, 1,2,3, 4,5,6, 5,7,6,
                0,1,4, 1,5,4, 2,6,3, 3,6,7,
                0,4,2, 2,4,6, 1,3,5, 3,7,5
            };

            var obb = new OBB { triangles = cubeTriangles };
            Vector3 bary = obb.Barycenter(cubeVertices);
            double[,] cov = obb.Covariance(cubeVertices, cubeTriangles, bary);

            double[] eigenvalues;
            double[,] eigenvectors;
            SymmetricEigen3x3.Decompose(cov, out eigenvalues, out eigenvectors);

            // All eigenvalues should be positive (valid covariance matrix)
            for (int i = 0; i < 3; i++)
                Assert.Greater(eigenvalues[i], 0.0, $"Eigenvalue {i} should be positive");
            // Eigenvalues should be sorted ascending
            Assert.LessOrEqual(eigenvalues[0], eigenvalues[1]);
            Assert.LessOrEqual(eigenvalues[1], eigenvalues[2]);
            AssertOrthonormal(eigenvectors);
        }

        [Test]
        public void ElongatedCovarianceMatrix_LargestEigenvalueAlongX()
        {
            // Elongated box: 10x in X, 1x in Y and Z
            Vector3[] vertices = {
                new Vector3(-10, -1, -1), new Vector3(10, -1, -1),
                new Vector3(-10,  1, -1), new Vector3(10,  1, -1),
                new Vector3(-10, -1,  1), new Vector3(10, -1,  1),
                new Vector3(-10,  1,  1), new Vector3(10,  1,  1)
            };
            int[] triangles = {
                0,2,1, 1,2,3, 4,5,6, 5,7,6,
                0,1,4, 1,5,4, 2,6,3, 3,6,7,
                0,4,2, 2,4,6, 1,3,5, 3,7,5
            };

            var obb = new OBB { triangles = triangles };
            Vector3 bary = obb.Barycenter(vertices);
            double[,] cov = obb.Covariance(vertices, triangles, bary);

            double[] eigenvalues;
            double[,] eigenvectors;
            SymmetricEigen3x3.Decompose(cov, out eigenvalues, out eigenvectors);

            // Largest eigenvalue should be much larger than the others
            Assert.Greater(eigenvalues[2], eigenvalues[1] * 5,
                "Largest eigenvalue should be significantly larger for elongated shape");
            // The eigenvector for the largest eigenvalue should align with X axis
            int maxCol = 2; // sorted ascending, so last is largest
            double xComponent = System.Math.Abs(eigenvectors[0, maxCol]);
            Assert.Greater(xComponent, 0.9,
                $"Largest eigenvector should align with X axis, X component = {xComponent}");
        }

        // --- Integration: full OBB.BuildOBB still works ---

        [Test]
        public void OBBBuildOBB_StillProducesValidResults()
        {
            Vector3[] cubeVertices = {
                new Vector3(-1, -1, -1), new Vector3(1, -1, -1),
                new Vector3(-1,  1, -1), new Vector3(1,  1, -1),
                new Vector3(-1, -1,  1), new Vector3(1, -1,  1),
                new Vector3(-1,  1,  1), new Vector3(1,  1,  1)
            };
            int[] cubeTriangles = {
                0,2,1, 1,2,3, 4,5,6, 5,7,6,
                0,1,4, 1,5,4, 2,6,3, 3,6,7,
                0,4,2, 2,4,6, 1,3,5, 3,7,5
            };

            var obb = new OBB();
            obb = obb.BuildOBB(cubeVertices, cubeTriangles);

            // Valid orientation
            float mag = Mathf.Sqrt(
                obb.orientation.x * obb.orientation.x +
                obb.orientation.y * obb.orientation.y +
                obb.orientation.z * obb.orientation.z +
                obb.orientation.w * obb.orientation.w);
            Assert.AreEqual(1f, mag, 0.001f, "Orientation should be unit quaternion");

            // Non-degenerate bounds
            Assert.Greater(obb.bounds.size.x, 0f);
            Assert.Greater(obb.bounds.size.y, 0f);
            Assert.Greater(obb.bounds.size.z, 0f);

            // Encapsulates all vertices
            Quaternion inv = Quaternion.Inverse(obb.orientation);
            foreach (var v in cubeVertices)
            {
                Vector3 local = inv * v;
                Assert.IsTrue(obb.bounds.Contains(local),
                    $"Vertex {v} not contained in OBB bounds");
            }
        }

        // --- Performance: Jacobi converges quickly for typical covariance matrices ---

        [Test]
        public void Performance_LargeRandomMesh_CompletesQuickly()
        {
            // Generate a random set of vertices and verify decomposition completes
            var rng = new System.Random(42);
            int vertexCount = 1000;
            Vector3[] vertices = new Vector3[vertexCount];
            for (int i = 0; i < vertexCount; i++)
                vertices[i] = new Vector3(
                    (float)(rng.NextDouble() * 10 - 5),
                    (float)(rng.NextDouble() * 4 - 2),
                    (float)(rng.NextDouble() * 6 - 3));

            // Create triangle indices (every 3 consecutive vertices)
            int triCount = (vertexCount / 3) * 3;
            int[] triangles = new int[triCount];
            for (int i = 0; i < triCount; i++)
                triangles[i] = i;

            var obb = new OBB { triangles = triangles };
            Vector3 bary = obb.Barycenter(vertices);
            double[,] cov = obb.Covariance(vertices, triangles, bary);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            double[] eigenvalues;
            double[,] eigenvectors;
            SymmetricEigen3x3.Decompose(cov, out eigenvalues, out eigenvectors);
            sw.Stop();

            AssertOrthonormal(eigenvectors);
            Assert.Less(sw.ElapsedMilliseconds, 10, "3x3 EVD should complete in under 10ms");
        }

        // --- Negative eigenvalues (shouldn't happen for covariance, but tests robustness) ---

        [Test]
        public void NegativeDefiniteMatrix_CorrectEigenvalues()
        {
            double[,] matrix = { { -3, 0, 0 }, { 0, -5, 0 }, { 0, 0, -1 } };
            double[] eigenvalues;
            double[,] eigenvectors;
            SymmetricEigen3x3.Decompose(matrix, out eigenvalues, out eigenvectors);

            Assert.AreEqual(-5.0, eigenvalues[0], 1e-10);
            Assert.AreEqual(-3.0, eigenvalues[1], 1e-10);
            Assert.AreEqual(-1.0, eigenvalues[2], 1e-10);
        }

        // --- Dense off-diagonal matrix ---

        [Test]
        public void DenseSymmetricMatrix_ReconstructsCorrectly()
        {
            double[,] matrix = { { 6, 2, 1 }, { 2, 3, 1 }, { 1, 1, 1 } };
            double[,] original = (double[,])matrix.Clone();
            double[] eigenvalues;
            double[,] eigenvectors;
            SymmetricEigen3x3.Decompose(matrix, out eigenvalues, out eigenvectors);

            AssertOrthonormal(eigenvectors);
            AssertReconstruction(original, eigenvalues, eigenvectors);
        }
    }
}
