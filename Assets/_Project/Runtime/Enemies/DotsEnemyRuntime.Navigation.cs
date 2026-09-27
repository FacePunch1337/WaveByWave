using System.Collections.Generic;
using Unity.Mathematics;
using Unity.Collections;
using UnityEngine;
using WaveByWave.Ships;

namespace WaveByWave.Enemies
{
    public sealed partial class DotsEnemyRuntime
    {
        private readonly Dictionary<(ulong, EnemyKind), EnemyDeckNavigationData> _deckMaps = new();
        private readonly Dictionary<(ulong, EnemyKind, int), int> _deckGoals = new();
        private int _deckRouteBuildsRemaining;
        private readonly HashSet<ulong> _defendedShips = new();
        private readonly Dictionary<ulong, ShipHoldSabotage> _undefendedHolds = new();

        private void PrepareHoldObjectives(NativeArray<EnemyTarget> targets)
        {
            _defendedShips.Clear(); _undefendedHolds.Clear(); _deckMaps.Clear();
            foreach (var target in targets)
                if (target.SupportId != 0) _defendedShips.Add(target.SupportId);
            foreach (var hold in ShipHoldSabotage.Active)
            {
                if (hold == null) continue;
                var flooding = hold.GetComponent<ShipFlooding>();
                var support = flooding != null && flooding.IsSpawned ? flooding.NetworkObjectId + 1 : 0;
                var defended = support == 0 || _defendedShips.Contains(support);
                hold.BeginFrame(defended);
                if (!defended && hold.CanSabotage) _undefendedHolds[support] = hold;
            }
        }

        private bool TickHoldObjective(ref DotsEnemyState state, ref DotsEnemyBrain brain, DotsEnemyCatalog catalog, float now)
        {
            if (catalog.UsesShipNavigation && state.Swimming == 0 && catalog.EnableCombat &&
                _undefendedHolds.TryGetValue(state.SupportId, out var hold) &&
                TryGetSurfaceFrame(state.SupportId, true, out var frame) &&
                hold.TryAttackPosition(DeckMap(state.SupportId, state.Kind, frame.lossyScale), state.Id,
                    out var slot, out var localFeet, out var outward))
            {
                if (brain.Sabotaging == 0)
                { brain.Attacking = 0; brain.StrikeAt = 0; SetAnimation(ref state, EnemyAnimationState.Idle); }
                brain.Sabotaging = 1;
                brain.TargetingShip = 0;
                brain.TargetSupport = state.SupportId;
                brain.Target = -2 - slot;
                var map = DeckMap(state.SupportId, state.Kind, frame.lossyScale);
                brain.MoveTarget = frame.MultiplyPoint3x4(localFeet);
                var finalDelta = brain.MoveTarget - state.Position;
                var movementTarget = (Vector3)brain.MoveTarget;
                var localPosition = frame.inverse.MultiplyPoint3x4(state.Position);
                if (!map.TryLocate(localPosition, Mathf.Max(0.5f, map.StepHeight), out _) &&
                    TryDeckRecovery(map, state.SupportId, localPosition, catalog, ref brain, out var recovery))
                    movementTarget = frame.MultiplyPoint3x4(recovery);
                var delta = movementTarget - (Vector3)state.Position;
                brain.TargetDistance = math.length(finalDelta);
                brain.MoveDirection = math.normalizesafe(new float3(delta.x, 0, delta.z));
                brain.Direction = brain.CrowdDirection = brain.MoveDirection;
                if (_surfaceTransfers.ContainsKey(state.Id)) return true;
                if (state.StunUntil > now)
                {
                    brain.Attacking = 0; brain.StrikeAt = 0;
                    SetAnimation(ref state, EnemyAnimationState.Stunned, state.StunUntil - now);
                    return true;
                }
                var localDelta = frame.inverse.MultiplyPoint3x4(state.Position) - localFeet;
                if (new Vector2(localDelta.x, localDelta.z).magnitude > hold.ArrivalDistance || Mathf.Abs(localDelta.y) > 0.35f)
                {
                    brain.Attacking = 0; brain.StrikeAt = 0;
                    return true;
                }
                brain.Direction = frame.MultiplyVector(outward).normalized;
                brain.MoveDirection = brain.CrowdDirection = float3.zero;
                state.Rotation = quaternion.LookRotationSafe(brain.Direction, frame.rotation * Vector3.up);
                UpdateLocal(ref state);
                var strike = brain.Attacking != 0 && brain.StrikeAt > 0 && now >= brain.StrikeAt;
                if (strike) brain.StrikeAt = 0;
                hold.RegisterAttacker((Vector3)state.Position + (Vector3)brain.Direction * 0.5f, strike);
                if (brain.Attacking != 0 && now >= state.AnimationStarted + state.AnimationDuration)
                { brain.Attacking = 0; SetAnimation(ref state, EnemyAnimationState.Idle); }
                if (brain.Attacking == 0 && now >= brain.NextAttack)
                {
                    var animation = catalog.AttackAnimation(EnemyCombatType.Melee);
                    SetAnimation(ref state, animation, catalog.Duration(animation));
                    brain.Attacking = 1;
                    brain.StrikeAt = now + state.AnimationDuration * catalog.AttackHitTime(EnemyCombatType.Melee);
                    brain.NextAttack = now + Mathf.Max(state.AnimationDuration, catalog.AttackCooldown(EnemyCombatType.Melee));
                }
                return true;
            }
            if (brain.Sabotaging != 0)
            {
                brain.Sabotaging = 0; brain.Attacking = 0; brain.StrikeAt = 0; brain.NextAttack = now;
                SetAnimation(ref state, EnemyAnimationState.Idle);
            }
            return false;
        }

        private static bool TryDeckRecovery(EnemyDeckNavigationData map, ulong support, Vector3 localPosition,
            DotsEnemyCatalog catalog, ref DotsEnemyBrain brain, out Vector3 localTarget)
        {
            localTarget = default;
            var node = brain.DeckSupport == support ? brain.DeckNode : -1;
            if (node < 0 || node >= map.Nodes.Length || !map.Nodes[node].Boundary)
            {
                var verticalRange = Mathf.Max(0.8f, Mathf.Min(catalog.StepHeight,
                    Mathf.Max(catalog.SurfaceTransferHeight, map.StepHeight)));
                if (!map.TryNearestBoundary(localPosition, verticalRange, out node)) return false;
                brain.DeckNode = node;
                brain.DeckSupport = support;
            }
            localTarget = map.Nodes[node].Position;
            return true;
        }

        private EnemyDeckNavigationData DeckMap(ulong support, EnemyKind kind, Vector3 scale)
        {
            // Enemy ships retain collider pursuit and boarding, including old instances
            // that still contain a navigation component or a cached map.
            if (support == 0 || IsShipSurface(support) || kind == EnemyKind.Shark) return null;
            var key = (support, kind);
            if (_deckMaps.TryGetValue(key, out var data)) return data;
            var root = ResolveSurface(support);
            var navigation = root != null ? root.GetComponent<EnemyDeckNavigation>() : null;
            var catalog = GetCatalog(kind) ?? Catalog;
            var minimumScale = Mathf.Max(0.001f, Mathf.Min(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
            data = navigation != null ? navigation.MapFor(catalog.DeckAgentRadius / minimumScale,
                catalog.DeckAgentHeight / Mathf.Max(0.001f, Mathf.Abs(scale.y)), catalog.MaximumSlope) : null;
            _deckMaps[key] = data;
            return data;
        }

        private bool MoveOnBakedDeck(ref DotsEnemyState state, ref DotsEnemyBrain brain, Vector3 displacement,
            float deltaTime, bool wantsToMove, float now, ref int edgeBudget)
        {
            var catalog = GetCatalog(state.Kind) ?? Catalog;
            if (!catalog.UsesShipNavigation || state.Swimming != 0 || IsShipSurface(state.SupportId) ||
                !TryGetSurfaceFrame(state.SupportId, true, out var frame)) return false;
            var scale = frame.lossyScale;
            var map = DeckMap(state.SupportId, state.Kind, scale);
            if (map == null || !map.IsBaked) return false;
            var minimumScale = Mathf.Min(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
            if (minimumScale < 0.001f || map.AgentRadius * minimumScale + 0.001f < catalog.DeckAgentRadius ||
                map.AgentHeight * Mathf.Abs(scale.y) + 0.001f < catalog.DeckAgentHeight || map.MaximumSlope > catalog.MaximumSlope + 0.001f)
                return false; // A map baked for a smaller agent cannot guarantee this one's clearance.
            var inverse = frame.inverse;
            var from = inverse.MultiplyPoint3x4(state.Position);
            var node = brain.DeckNode;
            var snapHeight = Mathf.Max(0.5f, map.StepHeight);
            var pursue = (brain.Target >= 0 || brain.Sabotaging != 0 || brain.TargetingShip != 0) &&
                brain.Attacking == 0 && state.StunUntil <= now;
            var target = pursue ? inverse.MultiplyPoint3x4(brain.Target >= 0 && brain.Target < _players.Count
                ? Feet(_players[brain.Target].Player) : (Vector3)brain.MoveTarget) : from;
            var targetSharesSupport = brain.Sabotaging != 0 || (brain.TargetingShip != 0 &&
                brain.TargetSupport == state.SupportId) || brain.Target >= _players.Count ||
                brain.Target >= 0 && _players[brain.Target].SupportId == state.SupportId;
            var targetOnMap = pursue && map.TryLocate(target, 0.8f, out _);
            // Retain the supporting layer while the visible feet ease vertically. Without
            // this hint a lower deck can become the nearest layer midway through a step.
            if (brain.DeckSupport != state.SupportId || !map.Contains(node, from, snapHeight))
                if (!map.TryLocate(from, snapHeight, out node))
                {
                    // Hybrid ship navigation: an enemy already on an unbaked part of this
                    // ship keeps using the ordinary collider controller while pursuing a
                    // player there. This prevents the map recovery from pulling it back at
                    // every frame after it has stepped across the baked boundary.
                    if (pursue && targetSharesSupport && !targetOnMap) return false;
                    // Boarding can land just outside the eroded navigation cells. Recover
                    // onto nearby floor without letting a missing cell select the mast.
                    if (map.TryNearest(from, map.AgentRadius + map.CellSize * 2 + 0.3f, 0.4f, out node))
                    {
                        var recovered = frame.MultiplyPoint3x4(map.Nodes[node].Position);
                        var head = frame.MultiplyVector(Vector3.up).normalized * (catalog.DeckAgentHeight * 0.5f);
                        if (!SolidBetween((Vector3)state.Position + head, recovered + head))
                        {
                            var previous = state.Position;
                            state.Position = Vector3.MoveTowards(previous, recovered, catalog.MoveSpeed * deltaTime);
                            UpdateLocal(ref state);
                            UpdateCrowdSnapshot(in state);
                            UpdateLocomotion(ref state, ref brain, math.distance(previous.xz, state.Position.xz),
                                deltaTime, true, true, now);
                            return true;
                        }
                    }
                    // Farther unbaked surfaces use the bounded collider controller until
                    // they reach a map edge. Once close, the recovery above takes over.
                    return false;
                }
            var desired = inverse.MultiplyPoint3x4((Vector3)state.Position + displacement);
            var step = Mathf.Min(map.StepHeight, catalog.StepHeight / minimumScale);
            var drop = Mathf.Min(map.MaximumDrop, catalog.MaximumDrop / minimumScale);
            var feet = from;
            var normal = map.Nodes[node].Normal;
            var moving = false;
            var evaluated = true;
            if (pursue)
            {
                var goalKey = (state.SupportId, state.Kind, brain.Target);
                if (!_deckGoals.TryGetValue(goalKey, out var goal))
                {
                    // Outside this ship: approach its nearest edge before boarding.
                    // Inside it: project only onto the player's actual floor.
                    if (!map.TryNearest(target, 1.5f, 0.8f, out goal))
                        map.TryNearest(target, Mathf.Max(map.Width, map.Depth) * map.CellSize +
                            Vector3.Distance(from, target), Mathf.Max(0.8f, Mathf.Abs(from.y - target.y) + 0.1f), out goal);
                    _deckGoals[goalKey] = goal;
                }
                var close = (target - from).sqrMagnitude < 4f && Mathf.Abs(target.y - from.y) < 0.35f &&
                    map.TryMove(node, from, target, step, drop, out var direct, out _) &&
                    Mathf.Abs(direct.y - target.y) < 0.35f;
                if (close)
                {
                    if (brain.Sabotaging != 0)
                    {
                        desired = Vector3.MoveTowards(from, target, catalog.MoveSpeed * deltaTime / minimumScale);
                        wantsToMove = Vector3.Distance(from, target) > 0.15f;
                    }
                    moving = map.TryMove(node, from, desired, step, drop, out feet, out normal);
                }
                else if (map.TryRoute(state.SupportId, brain.Target, node, goal, step, drop, now,
                             ref _deckRouteBuildsRemaining, out var route,
                             catalog.EnableSurfaceTransfers ? catalog.MaximumSurfaceGap / minimumScale : 0,
                             catalog.SurfaceTransferHeight / minimumScale))
                {
                    wantsToMove = true;
                    // The closest mapped cell is the hand-off point. From here the normal
                    // collider controller can follow a bowsprit, prop or another valid part
                    // that was deliberately excluded from the baked map. A target on the
                    // map never takes this branch, so ordinary obstacles such as the mast do
                    // not steal navigation while the player remains on a baked floor.
                    if (!targetOnMap && targetSharesSupport && map.Nodes[node].Boundary && route[node] == node)
                    {
                        brain.DeckNode = -1;
                        brain.DeckSupport = 0;
                        return false;
                    }
                    if (map.TryGap(node, route[node], out _, out var arc) &&
                        Mathf.Abs(from.y - map.Nodes[node].Position.y) < 0.08f &&
                        Vector2.Distance(new Vector2(from.x, from.z),
                            new Vector2(map.Nodes[node].Position.x, map.Nodes[node].Position.z)) < map.CellSize * 0.3f &&
                        BeginDeckGap(state, frame.MultiplyPoint3x4(map.Nodes[route[node]].Position), arc * minimumScale))
                    {
                        AdvanceSurfaceTransfer(ref state, ref brain, deltaTime, now);
                        UpdateCrowdSnapshot(in state);
                        return true;
                    }
                    moving = map.MoveAlongRoute(node, from, route,
                        catalog.MoveSpeed * deltaTime / minimumScale, step, drop, out feet, out normal);
                }
                else if (route != null && target.y > from.y + Mathf.Max(1f, map.StepHeight) &&
                         (map.Nodes[Mathf.Max(0, goal)].Position - target).sqrMagnitude < 2.25f)
                    return false; // Deliberate climb to a disconnected mast only when the target is above.
            }
            else moving = map.TryMove(node, from, desired, step, drop, out feet, out normal);

            if (moving && wantsToMove && catalog.EnableCrowdCollisions)
            {
                var routeDelta = frame.MultiplyPoint3x4(feet) - (Vector3)state.Position;
                var steered = SteerCrowdStep(in state, ref brain, routeDelta, deltaTime);
                var crowdPoint = inverse.MultiplyPoint3x4((Vector3)state.Position + (Vector3)steered);
                if (map.TryMove(node, from, crowdPoint, step, drop, out var crowdedFeet, out var crowdedNormal) &&
                    Mathf.Abs(crowdedFeet.y - feet.y) <= map.StepHeight)
                { feet = crowdedFeet; normal = crowdedNormal; }
            }
            if (!moving && pursue &&
                (brain.Target >= 0 && brain.Target < _players.Count || brain.TargetingShip != 0))
            {
                var transferGoal = brain.TargetingShip != 0 ? (Vector3)brain.MoveTarget :
                    Feet(_players[brain.Target].Player);
                if (brain.TargetSupport != state.SupportId &&
                    catalog.EnableSurfaceTransfers && map.Nodes[node].Boundary && catalog.MaximumSurfaceGap > 0)
                {
                    if (edgeBudget > 0)
                    {
                        edgeBudget--;
                        if (SearchSurfaceTransfer(state, ref brain, transferGoal, now))
                        {
                            AdvanceSurfaceTransfer(ref state, ref brain, deltaTime, now);
                            UpdateCrowdSnapshot(in state);
                            return true;
                        }
                    }
                    else evaluated = false;
                }
                // A wall or another floor is handled by the route. Greedy sideways
                // steps here would undo stair/door detours and climb the wrong layer.
            }
            var distance = 0f;
            if (moving)
            {
                var previous = state.Position;
                var worldFeet = frame.MultiplyPoint3x4(feet);
                var up = inverse.transpose.MultiplyVector(normal).normalized;
                // A baked hold floor remains walkable below ocean level: the hull keeps
                // that water outside. Ocean-height rejection stranded bots on the stairs.
                var walkable = up.y >= Mathf.Cos(catalog.MaximumSlope * Mathf.Deg2Rad);
                if (walkable)
                {
                    state.Position = new float3(worldFeet.x,
                        SmoothSurfaceHeight(previous.y, worldFeet.y, catalog.SurfaceVerticalSpeed, deltaTime), worldFeet.z);
                    distance = math.distance(previous.xz, state.Position.xz);
                    if (distance > 0.001f && state.StunUntil <= now && brain.Attacking == 0)
                        state.Rotation = math.slerp(state.Rotation, quaternion.LookRotationSafe(
                            Vector3.ProjectOnPlane(state.Position - previous, up), up), 1f - math.exp(-12f * deltaTime));
                    state.LocalPosition = inverse.MultiplyPoint3x4(state.Position);
                    state.LocalRotation = Quaternion.Inverse(frame.rotation) * (Quaternion)state.Rotation;
                    if (map.TryLocate(feet, 0.02f, out var supportingNode))
                    { brain.DeckNode = supportingNode; brain.DeckSupport = state.SupportId; }
                    ReleaseBoardedCrew(ref brain, state);
                    UpdateCrowdSnapshot(in state);
                }
                evaluated = true;
            }
            UpdateLocomotion(ref state, ref brain, distance, deltaTime, evaluated, wantsToMove, now);
            return true;
        }

        private bool BeginDeckGap(DotsEnemyState state, Vector3 destination, float arc)
        {
            // Both endpoints and full-body clearance were baked on this rigid ship.
            // Recasting a short ray can start inside the next stair riser and select its
            // underside instead. Use the exact shared map, also for ships without a PhysX view.
            var to = state; to.Position = destination; UpdateLocal(ref to);
            _surfaceTransfers[state.Id] = new SurfaceTransfer { From = state, To = to,
                BakedDeck = true, ArcHeight = arc,
                EdgeFromLocal = SurfacePoint(state.SupportId, state.Position, true),
                EdgeToLocal = SurfacePoint(to.SupportId, destination, true) };
            return true;
        }
    }
}
