# Changelog
All notable changes to the Universal Colliders project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [2.0.0] - 2026-03-12

### Added

- **CoACD decomposition method (experimental)** — Collision-Aware Convex Decomposition as an alternative to OBBTree, producing higher-quality convex hulls via the standalone `ConvexDecomposer` component. Computationally expensive (1–10 minutes per mesh); best used in the editor.
- **`ConvexDecomposer` component** — standalone MonoBehaviour for CoACD-based convex decomposition, with its own inspector, preview, and async API.
- **Voxel-based mesh repair** for non-manifold input meshes, allowing CoACD to handle imperfect geometry.
- **Batch collider generation** editor window (`BatchGeneratorWindow`) to generate colliders for multiple objects at once.
- **Colliders for animated objects** (SkinnedMeshRenderer support) with bottom-up OBB refit for stable skinned mesh colliders.
- **Runtime collider generation** — colliders can now be generated in standalone builds, not just in the editor.
- `GenerateCollidersAsync()` coroutine on both `UCollidersRoot` and `ConvexDecomposer` to spread generation across multiple frames and avoid spikes on large meshes.
- `OnCollidersGenerated` event on both components, fired after collider generation completes (both sync and async paths).

### Changed

- Upgraded Unity version from 2021.3.16 to Unity 6 (6000.3.5).
- **Extracted CoACD into standalone `ConvexDecomposer` component** — `UCollidersRoot` is now OBB-only; removed `DecompositionMethod` enum. Users add `ConvexDecomposer` for CoACD or `UCollidersRoot` for OBBTree.
- **Replaced Math.NET Numerics** with a lightweight built-in Jacobi eigenvalue solver (`SymmetricEigen3x3`), removing the external dependency.
- `[ExecuteInEditMode]` replaced with `[ExecuteAlways]` on `UCollidersRoot`, `UCollidersLeaf`, and `ConvexDecomposer` for build compatibility.
- Migrated documentation from Doxygen to DocFX.
- Reorganized package structure with assembly definitions (`UniversalColliders`, `UniversalColliders.Editor`, plus test assemblies).
- Golden ratio HSV coloring for more distinct collider preview colors.
- Renamed inspector labels for clarity: Recursion Level → Detail Level, Shape → Collider Shape, Optimize Mesh → Balance Vertices, Encapsulate Triangles → Fill Gaps, Encapsulation Path → Subdivision Path, Preserve UCollider → Preserve on Regenerate, Colliders Preview → Gizmo Display.
- OBB colliders auto-regenerate on inspector changes.
- CoACD pipeline optimized for ~3× faster decomposition.
- Bottom-up OBB refit and profiling improvements for the skinned mesh path.
- `GetMesh()` always uses `sharedMesh` to avoid per-instance mesh copy leaks at runtime.
- `OBB.BuildOBB` no longer allocates a `HashSet` for bounds computation.
- `RecomputeSkinnedMesh` caches the `SkinnedMeshRenderer` instead of calling `GetComponent` twice per frame.

### Fixed

- `_vertices` not updated after `BalanceMesh` in the combined-mesh path.
- OBB tree refit instead of full rebuild for skinned mesh stability.
- `DestroyColliders` was destroying the root `GameObject` instead of child collider objects at runtime.
- `InnerSubdivide` and `OuterSubdivide` skipped the last triangle due to off-by-one loop bounds.
- `SetEncapsulationPath` used unsafe `Substring` that could throw `IndexOutOfRangeException`.
- `ForceOBBRecursionLevel` had an off-by-one error in its depth bounds check.
- `SubdivideGameObject` caused serialization errors by destroying objects during inspector callbacks; now defers destruction via `EditorApplication.delayCall`.
- `DrawMesh` allocated a new `Mesh` every frame; now caches a `_gizmoMesh` field.
- `RegenerateCollider` used `DestroyImmediate` at runtime, causing errors in builds; added `#if UNITY_EDITOR` guard.
- `CheckAndRebuild` and `Subdivide` could crash after `GetOBBAtPath` returned null.
- `GetOBBAtPath` could crash with `IndexOutOfRangeException` when `ComputeChildren` returned an empty array.
- `OnDrawGizmosSelected` could crash with `NullReferenceException` when `_vertices` was uninitialized.
- `UCollidersLeaf.Subdivide` could crash accessing `children[0]` on a node with no children.
- Removed non-functional `createCollidersContainer` field.
- Made all custom exception classes consistently `public`.
- Fixed typos in `UCollidersExceptions.cs`.

### Removed

- **Math.NET Numerics** external dependency (replaced by built-in eigen solver).

## [1.2.0] - 2021-12-23

Online documentation: [universalcolliders.houmgaor.com](https://universalcolliders.houmgaor.com/)

### Added

- New preview modes for UColliders: Fill and Random 
Fill, to view colliders more easily.
- New field "encapsulationPath" in UCollidersRoot that will
help you view encpsulate triangles/vertices path.
- The "Unpack Colliders" button makes UColliderLeaf
game objects independent.

### Changed

- AddVerticesRecursively is now 820 times faster! Composite meshes got rebuffed!
- ComputeMesh is 40% faster!
- Renamed "Chainmail(2)" to "Chainmail".
- When no MeshFilter component is detected, "includeChildrenMeshes" 
is set to true on Awake.
- Root node do no longer need to have a mesh filter when 
"includeChildrenMeshes" is true.
- It is quicker to compute composite mesh.
- Composite mesh is now more precise.
- Colors changed when previewing UColliders. 
They are also more vivid.
- Statistics are now above buttons.

### Fixed

- Rare bug in which some OBBs were marked as *unbuildable* but were buildable. 
In the test, of 741 unbuildable OBB, 4 were in fact buildable.
- Chain ring was broken.
- Disabled box colliders was drawing a wrong 
square around chainmails.


## [1.1.0] - 2021-11-24
Online documentation: [universalcolliders.houmgaor.com/v1.1.0](https://universalcolliders.houmgaor.com/v1.1.0)
### Added
- Include Children Meshes toggle, to use mesh hierarchy.
- mesh used is now displayed in the UCollidersRoot and can be modified.
- Some statistics are displayed for UCollidersLeafNode.

### Changed
- UCollidersNodeEditor serves as the base namespace for editor scripts.
- Universal Colliders will no longer require universal memory! We brought drastic 
change to how the UColliderLeaf nodes store data, using position in tree instead 
of ... keeping track of all the triangles. In the demo scene, we reduced data usage
by 1 MB! The memory use of the main scene was reduced of 20%.
- New namespace "UColliders.EditorScripts" for the editor scripts.
- Chains have larger radius, they were too thin, thus causing tunneling issues.
- UCollidersLeaf path attribute is now the only information needed (with the mesh) 
to rebuild the Leaf Node. 

### Fixed
- Ranking up in the Unite Package publisher queue: corrected warnings.

## [1.0.0] - No public release
Online documentation: [universalcolliders.houmgaor.com/v1.0.0](https://universalcolliders.houmgaor.com/v1.0.0)
### Added
- Support for oriented bounding boxes.
- Support for mesh collider.
- Different volumes for OBBs (box, sphere, capsule, mesh).
- Sphere and mesh capsule have the same visualization, but not the same collider.
- Boundicity parameter for sphere and capsule colliders.
- Three choices in collider preview, between None, Random and Solid.
- Ability to subdivide an individual OBB.
- "Delete Colliders and Data" button, as well as "Regenerate Colliders" button.
- LeaveHoles toggle for enclosing triangles.
- Randomization of positions for the vertices on the plane.
- Added README.md, CHANGELOG.md, LICENSE.md, Third Party Notices.md, and one-file documentation.
