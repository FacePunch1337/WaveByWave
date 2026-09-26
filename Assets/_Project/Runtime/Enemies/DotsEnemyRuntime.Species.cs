using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using WaveByWave.Player;
using WaveByWave.Ships;
using Random = Unity.Mathematics.Random;

namespace WaveByWave.Enemies
{
    public sealed partial class DotsEnemyRuntime
    {
        private readonly EnemyHealthBarMode[] _healthBarOverrides = new EnemyHealthBarMode[4];
        public bool SpawnAdminSpecies(EnemyKind kind, Vector3 center, int count, float radius,
            EnemyHealthBarMode healthBar = EnemyHealthBarMode.Profile)
        {
            var catalog = GetCatalog(kind);
            if (!CanSimulate || !AttachServer() || catalog == null || !catalog.IsBaked ||
                !catalog.CanSpawnType(EnemyCombatType.Random) || _byId.Count >= Catalog.MaximumEnemies) return false;
            _spawns.Add(new SpawnRequest { Kind = kind, HealthBar = healthBar,
                Center = center, Radius = Mathf.Clamp(radius, 2, 1000), Remaining = Mathf.Clamp(count, 1, 3000),
                Group = AdminGroup(kind), Type = EnemyCombatType.Random,
                Random = new Random(unchecked((uint)System.Environment.TickCount) | 1u) });
            return true;
        }

        private static int AdminGroup(EnemyKind kind) => -100 - (int)kind;
        public void ClearAdminSpecies()
        {
            if (!CanSimulate || !AttachServer()) return;
            for (var i = 0; i < _species.Length; i++) DespawnGroup(AdminGroup((EnemyKind)i));
        }

        // An override is replicated per enemy; changing one type never enables every other type.
        public void SetSpeciesHealthBars(EnemyKind kind, bool show)
        {
            if (!CanSimulate || !AttachServer()) return;
            _healthBarOverrides[(int)kind] = show ? EnemyHealthBarMode.Show : EnemyHealthBarMode.Hide;
            var manager = _serverWorld.EntityManager;
            using var entities = _enemies.ToEntityArray(Allocator.Temp);
            foreach (var entity in entities)
            {
                var state = manager.GetComponentData<DotsEnemyState>(entity);
                if (state.Kind != kind) continue;
                state.HealthBar = show ? EnemyHealthBarMode.Show : EnemyHealthBarMode.Hide;
                manager.SetComponentData(entity, state);
            }
            foreach (var request in _spawns)
                if (request.Kind == kind) request.HealthBar = show ? EnemyHealthBarMode.Show : EnemyHealthBarMode.Hide;
        }

        private bool TrySpawnPosition(Vector3 candidate, DotsEnemyCatalog catalog, out Vector3 position,
            out RaycastHit ground, out byte swimming)
        {
            ground = default; position = default; swimming = 0;
            if (catalog.Habitat != EnemyHabitat.Water &&
                TryGround(candidate + Vector3.up * 0.6f, 100, catalog, out ground))
            { position = ground.point; return true; }
            if (catalog.Habitat == EnemyHabitat.Land || !TrySwimPosition(candidate, catalog, out position)) return false;
            swimming = 1;
            return true;
        }

        private bool TrySwimPosition(Vector3 point, DotsEnemyCatalog catalog, out Vector3 position)
        {
            position = point;
            if (ShipFlooding.CompartmentAt(point) != null || !_water.TryWaterLevel(point, out var level)) return false;
            var feet = level - catalog.SwimmingDepth;
            var count = Physics.RaycastNonAlloc(new Vector3(point.x, level + 8f, point.z), Vector3.down,
                _hits, 8f + Mathf.Max(catalog.MinimumWaterDepth, catalog.SwimmingDepth + 0.2f),
                catalog.SurfaceLayers, QueryTriggerInteraction.Ignore);
            // Test solid terrain and hulls, including submerged seabed, without treating ocean renderers as ground.
            for (var i = 0; i < count; i++)
            {
                var hit = _hits[i];
                if (!ValidSolid(hit.collider) || IsIslandDecoration(hit.collider)) continue;
                // Amphibians wade over the shallow seabed. Requiring full swim depth here
                // would leave an impassable strip between dry ground and deep water.
                if (catalog.Habitat != EnemyHabitat.Amphibious || hit.point.y > level + 0.05f ||
                    hit.normal.y < Mathf.Cos(catalog.MaximumSlope * Mathf.Deg2Rad)) return false;
                feet = Mathf.Max(feet, hit.point.y);
            }
            position.y = feet;
            return true;
        }

        private bool MoveInWater(ref DotsEnemyState state, ref DotsEnemyBrain brain, float3 displacement,
            float dt, bool wantsToMove, float now, DotsEnemyCatalog catalog)
        {
            var from = (Vector3)state.Position;
            var destination = from + (Vector3)displacement;
            var normalReach = catalog.BodyHeight + catalog.StepHeight;
            var shoreFound = TryGround(destination + Vector3.up * normalReach,
                normalReach + catalog.MaximumDrop, catalog, out var shore);
            // Amphibians share the normal surface controller whenever dry ground is reachable.
            if (catalog.Habitat == EnemyHabitat.Amphibious &&
                shoreFound && shore.point.y <= from.y + normalReach)
            {
                if (state.Swimming == 0) return false;
                // The walkable shore collider is also the first solid seen by the horizontal
                // transition ray. It is the destination support, not an obstacle in front of it.
                if (SolidBetween(from + Vector3.up * 0.7f, shore.point + Vector3.up * 0.7f,
                        false, shore.collider, null, catalog)) return true;
                state.Position = new float3(shore.point.x,
                    Mathf.MoveTowards(from.y, shore.point.y, catalog.SurfaceVerticalSpeed * dt), shore.point.z);
                state.Swimming = 0;
                AttachSurface(ref state, shore.collider);
                UpdateLocomotion(ref state, ref brain, math.distance(state.Position, from), dt, true, wantsToMove, now);
                return true;
            }
            var moving = false;
            var foundWater = false;
            var goal = from;
            // Check intermediate samples so a fast fish cannot cut straight across a narrow island.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var step = attempt == 0 ? (Vector3)displacement :
                    Quaternion.Euler(0, attempt == 1 ? 65 : -65, 0) * (Vector3)displacement;
                var steps = Mathf.Max(1, Mathf.CeilToInt(step.magnitude / 0.4f));
                var valid = true;
                for (var i = 1; i <= steps; i++)
                    if (!TrySwimPosition(from + step * (i / (float)steps), catalog, out goal))
                    { valid = false; break; }
                if (!valid) continue;
                var originHeight = catalog.Habitat == EnemyHabitat.Water ? catalog.BodyHeight * 0.5f : 0.7f;
                if (SolidBetween(from + Vector3.up * originHeight, goal + Vector3.up * originHeight,
                        false, null, null, catalog)) continue;
                foundWater = true;
                moving = step.sqrMagnitude > 0.000001f;
                break;
            }
            if (!foundWater)
            {
                if (state.Swimming == 0 && catalog.Habitat == EnemyHabitat.Amphibious) return false;
                if (TrySwimPosition(from, catalog, out goal)) foundWater = true;
                else goal = from;
            }
            state.Position = new float3(goal.x,
                Mathf.MoveTowards(from.y, goal.y, catalog.SurfaceVerticalSpeed * dt), goal.z);
            state.SupportId = 0;
            state.Swimming = 1;
            UpdateLocal(ref state);
            if (brain.Attacking == 0 && state.StunUntil <= now)
                SetAnimation(ref state, EnemyAnimationState.Swim);
            brain.CrowdBlockedTime = wantsToMove && !moving ? brain.CrowdBlockedTime + dt : 0;
            return true;
        }

        private bool TryBoardPlayerShip(ref DotsEnemyState state, ref DotsEnemyBrain brain,
            float3 displacement, float dt, bool wantsToMove, float now, DotsEnemyCatalog catalog)
        {
            if (catalog.ShipBoardingHeight <= 0f || !wantsToMove || brain.Target < 0 ||
                math.lengthsq(displacement.xz) < 0.000001f) return false;
            var from = (Vector3)state.Position;
            var destination = from + (Vector3)displacement;
            var reach = Mathf.Max(catalog.BodyHeight + catalog.StepHeight, catalog.ShipBoardingHeight);
            if (!TryGround(destination + Vector3.up * reach,
                    reach + catalog.MaximumDrop, catalog, out var deck)) return false;
            var ship = deck.collider.GetComponentInParent<NetworkShipController>();
            if (ship == null || !ship.IsSpawned || state.SupportId == ship.NetworkObjectId + 1 ||
                deck.point.y > from.y + catalog.ShipBoardingHeight) return false;
            // The destination ship's hull surrounds the deck by design. Ignore that one
            // moving hierarchy while retaining occlusion from islands and other ships.
            if (SolidBetween(from + Vector3.up * catalog.BodyHeight * 0.4f,
                    deck.point + Vector3.up * catalog.BodyHeight * 0.4f,
                    false, deck.collider, ship.transform, catalog)) return false;
            state.Position = deck.point;
            state.Swimming = 0;
            AttachSurface(ref state, deck.collider);
            UpdateLocomotion(ref state, ref brain, math.distance(state.Position, from),
                dt, true, wantsToMove, now);
            return true;
        }

        private bool CanSee(DotsEnemyState state, Vector3 target, DotsEnemyCatalog catalog)
        {
            if (catalog.Habitat == EnemyHabitat.Water &&
                (!_water.TryWaterLevel(target, out var level) || target.y >= level - 0.15f)) return false;
            return !SolidBetween((Vector3)state.Position + Vector3.up * catalog.BodyHeight * 0.7f,
                target + Vector3.up * 0.9f, true);
        }

        public static void HitCapsule(DotsEnemyState state, DotsEnemyCatalog catalog,
            out Vector3 bottom, out Vector3 top, out float radius)
        {
            radius = Mathf.Min(catalog.ProjectileHitRadius, catalog.BodyHeight * 0.5f);
            var rotation = (Quaternion)state.Rotation;
            bottom = (Vector3)state.Position + (catalog.CustomHitCapsule
                ? rotation * catalog.HitCapsuleStart : Vector3.up * radius);
            top = (Vector3)state.Position + (catalog.CustomHitCapsule
                ? rotation * catalog.HitCapsuleEnd : Vector3.up * (catalog.BodyHeight - radius));
        }
    }
}
