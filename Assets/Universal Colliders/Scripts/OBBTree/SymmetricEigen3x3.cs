/** \file
    \brief Eigenvalue decomposition for 3x3 symmetric matrices using Jacobi iteration.
*/
using System;
using UColliders.CoACD;

namespace UColliders.OBBTree
{
    /// <summary>
    /// Eigenvalue decomposition for 3×3 real symmetric matrices using the classical Jacobi method.
    /// Replaces the Math.NET Numerics dependency for OBB covariance decomposition.
    /// </summary>
    public static class SymmetricEigen3x3
    {
        /// <summary>
        /// Maximum number of Jacobi sweeps before declaring non-convergence.
        /// Each sweep performs up to 3 rotations (one per off-diagonal pair).
        /// </summary>
        const int MaxSweeps = 50;

        /// <summary>
        /// Convergence threshold for off-diagonal elements.
        /// </summary>
        const double Epsilon = 1e-12;

        /// <summary>
        /// Computes eigenvalues and eigenvectors of a 3×3 real symmetric matrix
        /// using Jacobi iteration (cyclic Givens rotations).
        /// </summary>
        /// <param name="matrix">A 3×3 symmetric matrix stored as double[3,3]. Modified in place.</param>
        /// <param name="eigenvalues">Output: three eigenvalues in ascending order.</param>
        /// <param name="eigenvectors">Output: 3×3 matrix where column i is the eigenvector for eigenvalues[i].</param>
        /// <exception cref="CovarianceNotConvergentException">Thrown if the iteration fails to converge.</exception>
        public static void Decompose(double[,] matrix, out double[] eigenvalues, out double[,] eigenvectors)
        {
            var tEigen = CoACDProfiler.Begin();
            // Work on a copy so we don't destroy the input
            double a00 = matrix[0, 0], a01 = matrix[0, 1], a02 = matrix[0, 2];
            double a11 = matrix[1, 1], a12 = matrix[1, 2];
            double a22 = matrix[2, 2];

            // Eigenvector matrix starts as identity
            eigenvectors = new double[3, 3];
            eigenvectors[0, 0] = 1; eigenvectors[1, 1] = 1; eigenvectors[2, 2] = 1;

            for (int sweep = 0; sweep < MaxSweeps; sweep++)
            {
                // Check convergence: sum of squares of off-diagonal elements
                double offDiag = a01 * a01 + a02 * a02 + a12 * a12;
                if (offDiag < Epsilon)
                {
                    eigenvalues = new double[] { a00, a11, a22 };
                    SortByEigenvalue(eigenvalues, eigenvectors);
                    CoACDProfiler.End("OBB.EigenDecompose", tEigen);
                    return;
                }

                // Rotate (0,1)
                if (Math.Abs(a01) > Epsilon)
                    JacobiRotation(ref a00, ref a11, ref a22, ref a01, ref a02, ref a12, 0, 1, eigenvectors);

                // Rotate (0,2)
                if (Math.Abs(a02) > Epsilon)
                    JacobiRotation(ref a00, ref a11, ref a22, ref a01, ref a02, ref a12, 0, 2, eigenvectors);

                // Rotate (1,2)
                if (Math.Abs(a12) > Epsilon)
                    JacobiRotation(ref a00, ref a11, ref a22, ref a01, ref a02, ref a12, 1, 2, eigenvectors);
            }

            CoACDProfiler.End("OBB.EigenDecompose", tEigen);
            throw new CovarianceNotConvergentException();
        }

        /// <summary>
        /// Applies a single Jacobi rotation to zero out the (p,q) off-diagonal element.
        /// </summary>
        static void JacobiRotation(
            ref double a00, ref double a11, ref double a22,
            ref double a01, ref double a02, ref double a12,
            int p, int q,
            double[,] eigenvectors)
        {
            // Get the relevant elements
            double app, aqq, apq;
            GetElements(a00, a11, a22, a01, a02, a12, p, q, out app, out aqq, out apq);

            if (Math.Abs(apq) < Epsilon)
                return;

            // Compute rotation angle
            double tau = (aqq - app) / (2.0 * apq);
            double t;
            if (tau >= 0)
                t = 1.0 / (tau + Math.Sqrt(1.0 + tau * tau));
            else
                t = -1.0 / (-tau + Math.Sqrt(1.0 + tau * tau));

            double c = 1.0 / Math.Sqrt(1.0 + t * t);
            double s = t * c;

            // Apply rotation to the symmetric matrix stored in 6 scalars
            ApplyRotation(ref a00, ref a11, ref a22, ref a01, ref a02, ref a12, p, q, c, s);

            // Update eigenvector columns
            for (int i = 0; i < 3; i++)
            {
                double vp = eigenvectors[i, p];
                double vq = eigenvectors[i, q];
                eigenvectors[i, p] = c * vp - s * vq;
                eigenvectors[i, q] = s * vp + c * vq;
            }
        }

        /// <summary>
        /// Extracts diagonal and off-diagonal elements for a given (p,q) pair.
        /// </summary>
        static void GetElements(
            double a00, double a11, double a22,
            double a01, double a02, double a12,
            int p, int q,
            out double app, out double aqq, out double apq)
        {
            // Diagonal
            if (p == 0) app = a00;
            else if (p == 1) app = a11;
            else app = a22;

            if (q == 0) aqq = a00;
            else if (q == 1) aqq = a11;
            else aqq = a22;

            // Off-diagonal (p,q) where p < q
            if (p == 0 && q == 1) apq = a01;
            else if (p == 0 && q == 2) apq = a02;
            else apq = a12;
        }

        /// <summary>
        /// Applies the Jacobi rotation to the 6 independent elements of the 3×3 symmetric matrix.
        /// </summary>
        static void ApplyRotation(
            ref double a00, ref double a11, ref double a22,
            ref double a01, ref double a02, ref double a12,
            int p, int q, double c, double s)
        {
            // Build full symmetric matrix, rotate, extract back
            double[,] a = {
                { a00, a01, a02 },
                { a01, a11, a12 },
                { a02, a12, a22 }
            };

            // A' = G^T * A * G where G is Givens rotation in (p,q) plane
            // First: B = A * G
            double[] colP = new double[3];
            double[] colQ = new double[3];
            for (int i = 0; i < 3; i++)
            {
                colP[i] = c * a[i, p] - s * a[i, q];
                colQ[i] = s * a[i, p] + c * a[i, q];
            }
            for (int i = 0; i < 3; i++)
            {
                a[i, p] = colP[i];
                a[i, q] = colQ[i];
            }

            // Then: A' = G^T * B (affect rows p and q)
            double[] rowP = new double[3];
            double[] rowQ = new double[3];
            for (int j = 0; j < 3; j++)
            {
                rowP[j] = c * a[p, j] - s * a[q, j];
                rowQ[j] = s * a[p, j] + c * a[q, j];
            }
            for (int j = 0; j < 3; j++)
            {
                a[p, j] = rowP[j];
                a[q, j] = rowQ[j];
            }

            // Extract back
            a00 = a[0, 0]; a11 = a[1, 1]; a22 = a[2, 2];
            a01 = a[0, 1]; a02 = a[0, 2]; a12 = a[1, 2];
        }

        /// <summary>
        /// Sorts eigenvalues in ascending order and reorders eigenvector columns to match.
        /// </summary>
        static void SortByEigenvalue(double[] eigenvalues, double[,] eigenvectors)
        {
            // Simple insertion sort for 3 elements
            for (int i = 0; i < 2; i++)
            {
                int minIdx = i;
                for (int j = i + 1; j < 3; j++)
                {
                    if (eigenvalues[j] < eigenvalues[minIdx])
                        minIdx = j;
                }
                if (minIdx != i)
                {
                    // Swap eigenvalues
                    double tmp = eigenvalues[i];
                    eigenvalues[i] = eigenvalues[minIdx];
                    eigenvalues[minIdx] = tmp;

                    // Swap eigenvector columns
                    for (int k = 0; k < 3; k++)
                    {
                        tmp = eigenvectors[k, i];
                        eigenvectors[k, i] = eigenvectors[k, minIdx];
                        eigenvectors[k, minIdx] = tmp;
                    }
                }
            }
        }
    }
}
