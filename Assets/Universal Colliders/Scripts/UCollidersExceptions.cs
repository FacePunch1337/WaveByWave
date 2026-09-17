/*! \file 
    \brief Defines the different exceptions used by this project.

    According to Unity guidelines, no general exceptions should 
    be raised. In case this happens, it means that you went through 
    an unexpected scenario. Please make sure you used the code in a
    predicted manner, and let us know if the problem persists.
*/
using System;

namespace UColliders 
{
    /// <summary>
    /// Use the <c>ReadMeshDisabledException</c> exception when the mesh is unreadable.
    /// </summary>
    public class ReadMeshDisabledException : Exception
    {
        /// <summary>
        /// Simple error message without details.
        /// </summary>
        public override string Message
        {
            get
            {
                return "Cannot compute object bounding box while Read/Write" + 
                " is not enabled for this mesh.\n"
                + "Please allow it in the mesh import settings.";
            }
        }
    }

    /// <summary>
    /// Use the <c>TooFewVertices</c> for a badly formatted mesh.
    /// </summary>
    public class TooFewVerticesException : Exception
    {
        /// <summary>
        /// Throw this error when they are two few vertice to build an OBB.
        ///
        /// In 3D this will happen when they are less than 4 vertices
        /// </summary>
        public TooFewVerticesException(int vertices) 
            : base(
                String.Format(
                    "Current mesh contains only {0} vertices. It is too few to build OBBs.", 
                    vertices
                )
            ) {

        }
    }

    /// <summary>
    /// Use the <c>CovarianceNotConvergentException</c> when the covariance matrix cannot be computed.
    /// That usually happens for vertices ill-positioned.
    /// </summary>
    public class CovarianceNotConvergentException : Exception
    {
        /// <summary>
        /// Simple error message, without detail.
        /// </summary>
        public override string Message
        {
            get
            {
                return "Covariance matrix cannot be computed";
            }
        }
    }

    /// <summary>
    /// The <c>NoMeshFilterException</c> should be fired when the current GameObject does not have a MeshFilter associated,
    /// or when we could not find one in his children.
    /// </summary>
    public class NoMeshFilterException : Exception
    {
        /// <summary>
        /// Simple error message with a general description of the problem.
        /// </summary>
        public override string Message
        {
            get
            {
                return "Component MeshFilter not detected on this GameObject!\n"
                + "You should either set the includeChildrenMeshes property to true.";
            }
        }
    }

    /// <summary>
    /// Thrown when CoACD decomposition is not available.
    /// </summary>
    public class CoACDNotAvailableException : Exception
    {
        /// <summary>
        /// Simple error message indicating CoACD is not available.
        /// </summary>
        public override string Message
        {
            get
            {
                return "CoACD decomposition is not available.";
            }
        }
    }

    /// <summary>
    /// This exception is raised when an unbuildable OBB is requested.
    ///
    /// For instance, this happens when we request an OBB of depth
    /// 2 with 4 vertices as the input
    /// </summary>
    public class PathNotComputableException : Exception
    {
        /// <summary>
        /// Throw this exception with unbuildable OBBTree paths.
        /// 
        /// It displays the errored path.
        /// </summary>
        public PathNotComputableException(string path) 
            : base(
                String.Format(
                    "The requested path \"{0}\" is uncomputable.\n"
                    + "Did you flush the data before regenerating UCollidersLeaves?", 
                    path
                )
            ) {

        }
    }

}