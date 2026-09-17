# Universal Colliders

Automatic non-convex mesh collider generation for Unity. One component, one click — accurate physics colliders on any mesh in seconds.

[Online Documentation](https://universalcolliders.houmgaor.com/) | [Blog Post](https://www.houmgaor.com/non-convex-mesh-colliders-in-unity/)

## Overview

Universal Colliders decomposes any mesh into tight-fitting colliders — either a hierarchy of oriented bounding boxes (OBBTree) or a set of convex hulls (CoACD). Pure C#, no native plugins, every platform.

**Key features:**

- **Two components** — `UCollidersRoot` (OBBTree) for fast primitive colliders, `ConvexDecomposer` (CoACD, experimental) for high-accuracy convex hulls
- **Primitive and mesh colliders** — outputs Box, Sphere, Capsule, or convex Mesh colliders
- **Animated mesh support** — colliders update in real time with skinned/deformable meshes via bottom-up OBB refit
- **Editor and runtime generation** — generate in the editor or at runtime (sync or async coroutine)
- **Live preview** — visualize colliders in the Scene view with multiple color modes
- **Batch processing** — generate or remove colliders on entire selections at once
- **Manual refinement** — subdivide individual OBB colliders where the algorithm isn't precise enough
- **Based on peer-reviewed research** — OBBTree from [Gottschalk et al. (1996)](https://www.cs.unc.edu/techreports/96-013.pdf), CoACD from [Wei et al. (2022)](https://colin97.github.io/CoACD/)

## Quick Start

1. Select your GameObject with a mesh (ensure **Read/Write Enabled** is checked in the mesh import settings).
2. **Add Component > Physics > Universal Collider** (adds `UCollidersRoot` for OBBTree).
3. Adjust the **Detail Level** slider and press **Generate / Regenerate Colliders**. Changes are
   intentionally queued until the button is pressed so dragging the slider cannot launch several rebuilds.

That's it. Colliders appear as child GameObjects and work with Unity's standard physics.

## Decomposition Methods

### OBBTree — Oriented Bounding Box Tree (`UCollidersRoot`)

Recursively subdivides the mesh into a binary tree of oriented bounding boxes. Each leaf becomes a primitive collider. Fast generation, suitable for animated objects and runtime use.

**Settings:**
- **Detail Level** (-1 to 20) — maximum subdivision depth, not a forced collider count. Filled
  board-like regions stop early as one correctly sized OBB; bends, holes and separated geometry continue splitting.
- **Collider Shape** — Box, Sphere, Capsule, or Mesh per node
- **Enclosure** (Sphere/Capsule only) — 0 = inscribed inside OBB, 1 = circumscribed around OBB
- **Balance Vertices** — rebalances vertex distribution for more uniform subdivision
- **Fill Gaps** — include full triangle coverage for gap-free results (per-level control via Subdivision Path)

OBB settings are applied by **Generate / Regenerate Colliders**. A level-8 build is limited to useful
mesh-balancing samples instead of multiplying every source triangle up to 128 times.

### CoACD — Collision-Aware Convex Decomposition (`ConvexDecomposer`) — Experimental

Produces high-quality convex hull decompositions. Best for static objects where accuracy matters.

**Warning:** CoACD is experimental and computationally expensive — expect one to ten minutes per mesh depending on complexity and settings. Always generate colliders in the editor and save them into your scene or prefab. Runtime generation will freeze the application for the duration of the computation.

**Quality settings:**
- **Precision Threshold** (0.01–1.0) — how closely colliders follow the mesh. Lower = tighter fit, more pieces
- **Max Collider Count** — cap the number of convex hulls (-1 = no limit)
- **Merge Similar Pieces** — reduce collider count by merging adjacent similar hulls

**Performance settings:**
- **Quality Samples**, **Cut Candidates**, **Search Iterations**, **Search Depth** — tune the MCTS-based search for better cuts vs. speed

**Advanced:**
- **Align to Principal Axes** — improves results on elongated or rotated meshes
- **Random Seed** — for reproducible results
- **Mesh Repair** — voxel-based repair for non-manifold meshes (Auto/On/Off with configurable resolution)

## Collider Shapes

| Shape | Best for |
|-------|----------|
| **Box** (default) | General purpose, tightest OBB fit, fastest physics |
| **Sphere** | Round objects, cheapest collision detection |
| **Capsule** | Elongated shapes (limbs, pipes, rails) |
| **Mesh** | Maximum accuracy (convex mesh per node) |

## Animated Mesh Support

For skinned meshes (characters, ragdolls, cloth), just add `UCollidersRoot` — it automatically detects the `SkinnedMeshRenderer` and recomputes colliders each frame to follow bone deformations using an efficient bottom-up OBB refit.

## Batch Processing

Process multiple GameObjects at once via **GameObject > Universal Colliders**:

- **Generate for Selection** — opens a window to configure and generate colliders on all selected objects with a progress bar
- **Remove from Selection** — strips all Universal Colliders components from the selection

Options include method selection, detail level, collider shape, whether to process children, and whether to skip objects that already have colliders.

## Runtime API

Colliders can be generated at runtime (Play mode and standalone builds). Both `UCollidersRoot` and `ConvexDecomposer` share the same API pattern.

**Warning:** CoACD runtime generation blocks the main thread for one to ten minutes. Generate CoACD colliders in the editor and save them into your scene or prefab whenever possible. OBBTree generation is fast and suitable for runtime use.

```csharp
// Synchronous — generates all colliders in a single frame
root.RegenerateColliders();
decomposer.RegenerateColliders();

// Async — spreads work across multiple frames to avoid hitches
StartCoroutine(root.GenerateCollidersAsync());
StartCoroutine(decomposer.GenerateCollidersAsync());

// Completion callback
root.OnCollidersGenerated += (r) => Debug.Log("OBB done!");
decomposer.OnCollidersGenerated += (d) => Debug.Log("CoACD done!");

// Remove colliders
root.DestroyColliders(force: false); // respects "Preserve" flags
root.DeleteCollidersAndData();       // full reset
decomposer.DestroyColliders();
decomposer.DeleteCollidersAndData();

// Convert to plain Unity colliders (remove plugin components)
root.UnpackColliders();
decomposer.UnpackColliders();
```

## Inspector Buttons

| Button | Action |
|--------|--------|
| **Regenerate Colliders** | Rebuilds all colliders from the current settings. Preserves any leaf nodes marked "Preserve on Regenerate". Shows a progress bar for CoACD. |
| **Unpack Colliders** | Converts `UCollidersLeaf` children into plain GameObjects with standard colliders. Useful for final export or removing the plugin dependency. |
| **Delete Colliders and Data** | Removes all generated colliders and resets internal data. |

## Per-Collider Refinement (UCollidersLeaf)

Each generated collider gets a `UCollidersLeaf` component with its own inspector:

- **Collider Shape** — override the shape for this specific collider (e.g., use a sphere on a round section while the rest uses boxes)
- **Subdivide** — manually split this OBB collider into two for extra precision exactly where you need it
- **Preserve on Regenerate** — protect this node from being overwritten when the parent regenerates
- **Enclosure** — per-collider enclosure ratio for Sphere/Capsule shapes

## Editor Preview

Visualize colliders in the Scene view without entering Play mode:

| Mode | Description |
|------|-------------|
| **None** | No preview |
| **Solid** | Wireframe in a fixed color |
| **Random** | Wireframe with distinct colors per collider (golden ratio HSV) |
| **Fill** | Filled volume in a fixed color |
| **RandomFill** | Filled volume with distinct colors per collider |

All editor operations support **full undo/redo**.

## Troubleshooting

### Colliders are too loose
Increase the detail level (OBB) or lower the precision threshold (CoACD). Use **Box** shape for the tightest OBB fit.

### Small gaps between colliders
Enable **Fill Gaps**, or increase the detail level. Gaps are most visible on meshes with long, thin triangles.

### Mesh has uneven subdivision
Enable **Balance Vertices** to rebalance vertex distribution before OBB computation. Alternatively, add more vertices to sparse areas in your modeling tool.

### CoACD produces broken colliders
Try setting **Mesh Repair** to **On** and increasing the **Repair Quality**. This fixes non-manifold geometry before decomposition.

### CoACD is slow
CoACD decomposition takes one to ten minutes per mesh — this is expected. Always generate CoACD colliders in the editor and save the result into your scene or prefab. Avoid relying on runtime generation. To reduce computation time, increase the **Precision Threshold** (less precise but faster) or lower the **Quality Samples** and **Search Iterations**.

### OBBTree freezes while changing Detail Level
Collider generation is explicit rather than automatic. Change all settings first, then press
**Generate / Regenerate Colliders** once. Vertex balancing uses a bounded sampling pass, so high detail
does not create hundreds of thousands of temporary triangles before building the requested OBBs.

### Requirement
Meshes must have **Read/Write Enabled** set to true in Unity's import settings, otherwise the vertices cannot be accessed at runtime.

## How It Works

**OBBTree** implements the algorithm from [S. Gottschalk, M. C. Lin, D. Manocha (1996)](https://www.cs.unc.edu/techreports/96-013.pdf). It computes a covariance matrix of the mesh surface, extracts eigenvectors to determine the optimal box orientation, then recursively splits the mesh along the longest axis. Each leaf of the resulting binary tree becomes a Unity collider.

**CoACD** implements Collision-Aware Convex Decomposition from [Wei et al. (2022)](https://colin97.github.io/CoACD/). It uses MCTS-based search to find optimal cutting planes that minimize concavity while producing a small number of convex pieces. The result is a set of convex mesh colliders that closely follow the original surface.

## Compatibility

- **Unity 6** (6000.3.5) and later
- All render pipelines (Built-in, URP, HDRP)
- All platforms (Windows, macOS, Linux, Android, iOS, WebGL, consoles) — pure C#, no native plugins
- Runtime and Editor collider generation

## Package Contents

- `Scripts/` — runtime components (`UColliders` namespace)
- `Editor/` — custom inspectors and batch tools (`UColliders.EditorScripts` namespace)
- `Tests/` — EditMode and PlayMode test suites
- `Documentation/` — this README, changelog, and licenses
- `Demos/` — demo scenes, scripts, and models

## Support

- **Email:** [universalcolliders@houmgaor.com](mailto:universalcolliders@houmgaor.com)
- **Website:** [houmgaor.com](https://www.houmgaor.com/non-convex-mesh-colliders-in-unity/)

If you find Universal Colliders useful, a review on the Asset Store helps others discover it.
