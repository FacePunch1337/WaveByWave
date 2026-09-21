# DOTS enemy ships

Enemy ships use an ECS ghost for authoritative movement/combat state and a lightweight GameObject view for rendering and PhysX hull contacts. The server simulates broadside orbiting, wind-assisted sailing, wave alignment, avoidance, cannon trajectories and damage. The same ghost drives the view on clients.

## Content

- `Assets/_Project/Resources/EnemyShipDefinition.asset` contains gameplay, sailing, wave, collision, broadside, projectile and crew tuning.
- `Assets/_Project/Prefabs/Enemies/EnemyShip.prefab` is the replaceable hybrid view. Keep `EnemyShipView`, a kinematic `Rigidbody`, and at least one solid hull collider on its root.
- `Assets/_Project/Prefabs/Enemies/EnemyShipSpawnPoint.prefab` is placed in the Ocean scene to create fleets.

The default view copies the current player ship appearance but removes all player/network behaviours and child colliders. It adds one authoritative box hull, six broadside hardpoints and eight crew slots. Artists may replace the `Appearance` child freely. Add `EnemyShipCrewSlot` markers to control exact skeleton spawn positions.

The prefab and definition are saved project assets and are never rebuilt or edited on startup. The content generator is an explicit editor command only: `Tools > Wave by Wave > Enemies > Create default DOTS enemy ship content`. Run it only when you intentionally want to recreate missing default content.

## Admin spawning

The host admin panel (`F2`) has enemy-ship count (up to 1000) and radius controls plus `Заспавнить корабли`. One click queues the selected number around the player. `Maximum Ships` in `EnemyShipDefinition.asset` is the authoritative simultaneous-ship cap (1000 in the default asset). The default radius is 600 m so a large fleet has enough clear water between hulls; a smaller radius can exhaust placement attempts before the requested count fits.

## Tactics

Each ship deterministically chooses a port or starboard orbit. It corrects toward `Preferred Broadside Range`, follows the tangent around the target, and fires only when the target is inside `Broadside Fire Angle`. Burst steering supplies fleet separation. A persistent spatial index sweeps conservative hull-enclosing circles against neighbouring ships, including ships without physical views. Non-allocating box sweeps handle the player ship and scenery. The player ship also queries the fleet index because its existing triangle collision world contains static scenery only. Circle clearance is deliberately conservative during turns and wave tilt.

## Crew

When the server creates the ship entity, it spawns `Crew Count` existing DOTS skeleton entities on the deck, independently of view distance. They use a deterministic ship ID and retain local positions and rotations even when no GameObject view exists. Crew is removed when its ship sinks. The skeleton catalog's `Maximum Enemies` still applies to all skeletons; if it is full, crew creation retries when capacity becomes available.

## Ship-local passengers and loot

Player presentation now composes local position and rotation with the current ship pose on both the host and clients. KCC physics uses the completed simulation pose. The camera retains deck-relative yaw and full ship tilt. DOTS-ship passengers replicate their ship ID and local pose atomically; boarding, jumping and scene transitions attach or release that frame.

Skeletons compose their stored local pose with the server ship frame before combat, and with the rendered frame for presentation. Idle crew no longer repeatedly raycast onto an older rendered hull, which previously could shift their local anchor.

Both ordinary `WorldItem` placement and DOTS loot support enemy ship IDs. DOTS loot packets include server-authored local position, rotation, flight start and arc direction. Clients never reconstruct these from a delayed world packet. This applies to late-join snapshots as well. Pickup checks use the authoritative ship frame.

## Fleet performance

`EnemyShipDefinition.asset` exposes `Simulation Rate` (20 Hz), `Distant Simulation Rate` (5 Hz), `Detailed Simulation Distance` (100 m), `Physics View Distance` (100 m), `View Creations Per Frame` (2), and `Instance Distant Ships`.

Nearby ships instantiate the saved prefab for physical interaction. Distant ships render its active static meshes through shared instanced draw batches, with frustum culling and no distant shadow casting. Material copies are private runtime resources; source materials and prefabs are never edited. Skinned prefabs or unsupported GPU instancing fall back to normal prefab views. Custom scripts/animation on the appearance run only while that detailed view exists.

Batches contain at most 511 instances to support arbitrary authored scales and Unity's default matrix pair per instance ([Unity API](https://docs.unity3d.com/6000.2/Documentation/ScriptReference/Graphics.RenderMeshInstanced.html)). Ships continue simulating and retaining crew when their detailed view is absent. A 30 m hysteresis band prevents repeated creation at the view boundary. Damage-flash material blocks update only when the flash changes.

Profiler markers: `WaveByWave.Fleet.Simulation` and `WaveByWave.Fleet.Presentation`. Validate FPS in the actual target scene/build; the standalone contact regression in `Tools/ShipFleetChecks` measures CPU contact math only. No in-game FPS improvement is claimed without a before/after capture.
