# Ship navigation and hold attacks

Only the player `Ship.prefab` uses `EnemyDeckNavigation`. Its maps live in
`Assets/_Project/Data/Enemies`. Positions are local to the ship: sailing, yaw and
waves do not require a rebake. Each map keeps separate floor layers and body clearance.
Agents can use `Additional Agent Maps` for different navigation dimensions.

Enable or disable this movement with `Use Baked Deck Navigation` in
each species' own `EnemyCatalog`. Amphibians and trolls do not depend on the skeleton's switch.
Water-only enemies and sharks are excluded from deck navigation, boarding and hold attacks.
The original collider movement and arch passage behavior remain available when off.
Enemy ships always keep their original collider movement and boarding, regardless of this switch.

`Ship Navigation Radius` and `Ship Navigation Height` override clearance only on the player
ship. Zero uses the normal `Body Radius` / `Body Height`. Amphibians and trolls are configured
with radius 0.1 m and height 1.7 m, like skeletons, to reach the same stairs and hold passages.
Their models and projectile hit capsules keep their original dimensions. In particular, the
roughly 3 m troll is deliberately allowed to intersect low ceilings; its attack visibility
origin uses the smaller clearance in the ship so those ceilings do not block all attacks.

After changing ship geometry or enemy navigation dimensions, run
`Tools > Wave by Wave > Enemies > Bake player ship routes and enable navigation` outside Play Mode.
This refreshes the player ship's skeleton, amphibian and troll maps.
For another player ship, add `EnemyDeckNavigation`, select its solid colliders in `Sources`
(empty means all fixed solid colliders), and bake from its Inspector.
`Show Navigation` draws the selected ship's cells.

`Maximum Gap` and `Transfer Height` on the navigation component limit the bridges
created during baking. At runtime, `Enable Surface Transfers`, `Maximum Surface Gap`
and `Surface Transfer Height` in the individual enemy profile also limit these bridges.
Increasing the runtime gap beyond the baked value requires rebaking.
Bridge clearance is checked offline against the body, railings, walls and ceilings.
The same ship-local route field is shared by bots pursuing the same target; route
rebuilds are limited to one per movement batch. Inter-ship boarding keeps its existing
moving-surface checks and per-frame search budget.
When a target stands on a valid part of the same player ship outside the baked cells,
the route leads to the closest boundary and hands control to the ordinary collider
movement. Moving back toward a baked floor automatically returns control to the map.
If that player leaves the ship, enemies already on the unbaked bow cache the nearest
deck-boundary node, walk back to it with collider movement, and then resume the shared
baked route to the hold. Boundary-node lists are built once per map; this adds no
per-frame Physics queries or per-enemy path searches.

`ShipHoldSabotage` on the player ship controls attacks on an undefended hold:

- `Enable Sabotage`: enable this objective. Requires baked deck navigation.
- `Hold Bounds`: ship-local volume around the hold floor, excluding upper decks.
  Select the component to see the orange box. Positions are chosen on the navigation
  boundary near the two hull sides; bots face outward when attacking.
- `Attack Positions`: shared destinations, from 2 to 8.
- `Grace Seconds`: delay after enemies reach their attack positions.
- `Seconds Per Breach`: time for one attacker to open a breach (default 12 seconds).
- `Maximum Contributors`: maximum number that speeds up damage (default 4).
- `Fastest Breach Interval`: hard ship-wide speed limit. The effective interval is the
  larger of this value and `Seconds Per Breach / active contributors`. For example,
  `1 / 4` with a fastest interval of `3` still means one breach every 3 seconds.
- `Leak Multiplier`: size of the leak created by each successful breach.

Living players aboard take priority over hold attacks. Returning to defend stops and
resets the shared damage budget. Each breach requires an attack animation's hit event;
it bypasses the normal damage breach chance and uses the existing replicated hole list.
If no valid free hull sites remain, no extra holes are created.

Enemies capable of hold sabotage choose targets by surface. A player on the same dry
island or ship is attacked first. Otherwise a player aboard the player ship is attacked;
with nobody aboard, the ship itself becomes the objective. Players swimming or standing
on another island do not pull these enemies away from the ship. Procedural islands use
stable IDs, and player ground is sampled once per frame; target selection stays in the
parallel ECS job and performs no Physics queries per enemy.
