# DOTS enemy ships

Enemy ships use an ECS ghost for authoritative movement/combat state and a lightweight GameObject view for rendering and PhysX hull contacts. The server simulates broadside orbiting, wind-assisted sailing, wave alignment, avoidance, cannon trajectories and damage. The same ghost drives the view on clients.

## Content

- `Assets/_Project/Resources/EnemyShipDefinition.asset` contains gameplay, sailing, wave, collision, broadside, projectile and crew tuning.
- `Assets/_Project/Prefabs/Enemies/EnemyShip.prefab` is the replaceable hybrid view. Keep `EnemyShipView`, a kinematic `Rigidbody`, and at least one solid hull collider on its root.
- `Assets/_Project/Prefabs/Enemies/EnemyShipSpawnPoint.prefab` is placed in the Ocean scene to create fleets.

The default view copies the current player ship appearance but removes all player/network behaviours and child colliders. It adds one authoritative box hull, six broadside hardpoints and eight crew slots. Artists may replace the `Appearance` child freely. Add `EnemyShipCrewSlot` markers to control exact skeleton spawn positions.

The prefab and definition are saved project assets and are never rebuilt or edited on startup. The content generator is an explicit editor command only: `Tools > Wave by Wave > Enemies > Create default DOTS enemy ship content`. Run it only when you intentionally want to recreate missing default content.

## Admin spawning

The host admin panel (`F2`) has enemy-ship count (up to 1000) and radius controls plus `Заспавнить корабли`. One click queues the selected number around the player. `Maximum Ships` in `EnemyShipDefinition.asset` is the authoritative simultaneous-ship cap (1000 in the default asset).

## Tactics

Each ship deterministically chooses a port or starboard orbit. It corrects toward `Preferred Broadside Range`, follows the tangent around the target, and fires only when the target is inside `Broadside Fire Angle`. `Avoidance Radius` supplies DOTS fleet separation; a swept hull box prevents accepted movement through another ship, the player ship, or world geometry.

## Crew

When the server creates the hybrid hull, it immediately spawns `Crew Count` existing DOTS skeleton entities on the deck. They use the ship as a deterministic moving surface (`EnemySurfaceAnchor`) and are removed when their ship sinks.
