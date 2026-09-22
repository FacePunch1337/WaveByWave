using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace WaveByWave.Enemies
{
    public sealed partial class DotsEnemyRuntime
    {
        private readonly Dictionary<ulong, EnemyDeckNavigationData> _deckMaps = new();

        private EnemyDeckNavigationData DeckMap(ulong support)
        {
            if (support == 0) return null;
            if (_deckMaps.TryGetValue(support, out var data)) return data;
            var root = ResolveSurface(support);
            var navigation = root != null ? root.GetComponent<EnemyDeckNavigation>() : null;
            // A shared prefab map also works for DOTS ships without a nearby PhysX view.
            if (navigation == null && IsShipSurface(support))
            {
                var definition = DotsEnemyShipRuntime.Instance != null ? DotsEnemyShipRuntime.Instance.Definition : null;
                if (definition != null && definition.ViewPrefab != null)
                    navigation = definition.ViewPrefab.GetComponent<EnemyDeckNavigation>();
            }
            data = navigation != null ? navigation.Data : null;
            _deckMaps[support] = data;
            return data;
        }

        private bool MoveOnBakedDeck(ref DotsEnemyState state, ref DotsEnemyBrain brain, Vector3 displacement,
            float deltaTime, bool wantsToMove, float now, NativeArray<DotsEnemyState> crowd, ref int edgeBudget)
        {
            if (!Catalog.UseBakedDeckNavigation) return false;
            var map = DeckMap(state.SupportId);
            if (map == null || !map.IsBaked || !TryGetSurfaceFrame(state.SupportId, true, out var frame)) return false;
            var scale = frame.lossyScale;
            var minimumScale = Mathf.Min(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
            if (minimumScale < 0.001f || map.AgentRadius * minimumScale + 0.001f < Catalog.BodyRadius ||
                map.AgentHeight * Mathf.Abs(scale.y) + 0.001f < Catalog.BodyHeight || map.MaximumSlope > Catalog.MaximumSlope + 0.001f)
                return false; // A map baked for a smaller agent cannot guarantee this one's clearance.
            var inverse = frame.inverse;
            var from = inverse.MultiplyPoint3x4(state.Position);
            var node = brain.DeckNode;
            var snapHeight = Mathf.Max(0.5f, map.StepHeight);
            // Retain the supporting layer while the visible feet ease vertically. Without
            // this hint a lower deck can become the nearest layer midway through a step.
            if (brain.DeckSupport != state.SupportId || !map.Contains(node, from, snapHeight))
                if (!map.TryLocate(from, snapHeight, out node)) return false;
            var desired = inverse.MultiplyPoint3x4((Vector3)state.Position + displacement);
            var step = Mathf.Min(map.StepHeight, Catalog.StepHeight / minimumScale);
            var drop = Mathf.Min(map.MaximumDrop, Catalog.MaximumDrop / minimumScale);
            var moving = map.TryMove(node, from, desired, step, drop, out var feet, out var normal);
            var evaluated = true;
            if (!moving && brain.Target >= 0 && brain.Target < _players.Count && brain.Attacking == 0 && state.StunUntil <= now)
            {
                var goal = Feet(_players[brain.Target].Player);
                if (Catalog.EnableSurfaceTransfers && map.Nodes[node].Boundary && Catalog.MaximumSurfaceGap > 0)
                {
                    if (edgeBudget > 0)
                    {
                        edgeBudget--;
                        if (TryBeginSurfaceTransfer(state, goal))
                        {
                            AdvanceSurfaceTransfer(ref state, ref brain, deltaTime, now);
                            UpdateCrowdSnapshot(crowd, state);
                            return true;
                        }
                    }
                    else evaluated = false;
                }
                moving = Catalog.EnableSurfaceEdgeFollowing && map.TryFollow(node, from, inverse.MultiplyPoint3x4(goal),
                    Catalog.MoveSpeed * deltaTime / minimumScale, step, drop, out feet, out normal);
            }
            var distance = 0f;
            if (moving)
            {
                var previous = state.Position;
                var worldFeet = frame.MultiplyPoint3x4(feet);
                var up = inverse.transpose.MultiplyVector(normal).normalized;
                var walkable = up.y >= Mathf.Cos(Catalog.MaximumSlope * Mathf.Deg2Rad) &&
                    (!_water.TryWaterLevel(worldFeet, out var waterHeight) || worldFeet.y >= waterHeight - 0.05f);
                if (walkable)
                {
                    var accepted = LimitCrowdStep(state.Id, state.SupportId, previous, worldFeet, crowd,
                        Catalog.CrowdSeparationRadius, Catalog.BodyRadius, Catalog.BodyHeight);
                    // A collision-shortened step still has to remain on the same connected deck.
                    if (map.TryMove(node, from, inverse.MultiplyPoint3x4(accepted), step, drop, out var limited, out normal))
                    {
                        worldFeet = frame.MultiplyPoint3x4(limited);
                        state.Position = new float3(worldFeet.x,
                            SmoothSurfaceHeight(previous.y, worldFeet.y, Catalog.SurfaceVerticalSpeed, deltaTime), worldFeet.z);
                        distance = math.distance(previous.xz, state.Position.xz);
                        if (distance > 0.001f && state.StunUntil <= now && brain.Attacking == 0)
                            state.Rotation = math.slerp(state.Rotation, quaternion.LookRotationSafe(
                                Vector3.ProjectOnPlane(state.Position - previous, up), up), 1f - math.exp(-12f * deltaTime));
                        state.LocalPosition = inverse.MultiplyPoint3x4(state.Position);
                        state.LocalRotation = Quaternion.Inverse(frame.rotation) * (Quaternion)state.Rotation;
                        if (map.TryLocate(limited, 0.02f, out var supportingNode))
                        { brain.DeckNode = supportingNode; brain.DeckSupport = state.SupportId; }
                        ReleaseBoardedCrew(ref brain, state);
                        UpdateCrowdSnapshot(crowd, state);
                    }
                }
                evaluated = true;
            }
            UpdateLocomotion(ref state, ref brain, distance, deltaTime, evaluated, wantsToMove, now);
            return true;
        }
    }
}
