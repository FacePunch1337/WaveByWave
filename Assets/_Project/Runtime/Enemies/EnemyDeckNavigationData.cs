using System;
using System.Collections.Generic;
using UnityEngine;

namespace WaveByWave.Enemies
{
    [Serializable]
    public struct EnemyDeckNode
    {
        public Vector3 Position, Normal;
        public int Column, FirstLink, LinkCount;
        public bool Boundary;
    }

    [Serializable]
    public struct EnemyDeckColumn { public int FirstNode, Count; }

    [CreateAssetMenu(menuName = "Wave By Wave/Enemies/Baked deck navigation")]
    public sealed class EnemyDeckNavigationData : ScriptableObject
    {
        public string SourceHash;
        public Vector2 Origin;
        public float CellSize = 0.2f;
        public int Width, Depth;
        public float AgentRadius, AgentHeight, MaximumSlope, StepHeight, MaximumDrop;
        public EnemyDeckColumn[] Columns = Array.Empty<EnemyDeckColumn>();
        public EnemyDeckNode[] Nodes = Array.Empty<EnemyDeckNode>();
        public int[] Links = Array.Empty<int>();
        public float[] LinkGaps = Array.Empty<float>();
        public float[] LinkArcs = Array.Empty<float>();
        public bool IsBaked => Nodes.Length > 0 && Columns.Length == Width * Depth;

        private int ColumnAt(Vector3 point)
        {
            var x = Mathf.FloorToInt((point.x - Origin.x) / CellSize);
            var z = Mathf.FloorToInt((point.z - Origin.y) / CellSize);
            return x >= 0 && z >= 0 && x < Width && z < Depth ? z * Width + x : -1;
        }

        private float Height(int node, Vector3 point)
        {
            var value = Nodes[node];
            return value.Position.y - ((point.x - value.Position.x) * value.Normal.x +
                (point.z - value.Position.z) * value.Normal.z) / Mathf.Max(0.001f, value.Normal.y);
        }

        public bool TryLocate(Vector3 point, float maximumHeightDifference, out int node)
        {
            node = -1;
            var column = ColumnAt(point);
            if (!IsBaked || column < 0) return false;
            var values = Columns[column];
            var best = maximumHeightDifference;
            for (var i = values.FirstNode; i < values.FirstNode + values.Count; i++)
            {
                var difference = Mathf.Abs(Height(i, point) - point.y);
                if (difference > best) continue;
                node = i; best = difference;
            }
            return node >= 0;
        }

        public bool Contains(int node, Vector3 point, float maximumHeightDifference) =>
            node >= 0 && node < Nodes.Length && Nodes[node].Column == ColumnAt(point) &&
            Mathf.Abs(Height(node, point) - point.y) <= maximumHeightDifference;

        // Target projection is local to the ship and includes height. A player in the
        // hold must not be projected onto the main deck above the same XZ position.
        public bool TryNearest(Vector3 point, float horizontalRange, float verticalRange, out int node)
        {
            if (TryLocate(point, Mathf.Min(0.4f, verticalRange), out node)) return true;
            node = -1;
            if (!IsBaked) return false;
            var best = float.PositiveInfinity;
            var minX = Mathf.Clamp(Mathf.FloorToInt((point.x - horizontalRange - Origin.x) / CellSize), 0, Width - 1);
            var maxX = Mathf.Clamp(Mathf.FloorToInt((point.x + horizontalRange - Origin.x) / CellSize), 0, Width - 1);
            var minZ = Mathf.Clamp(Mathf.FloorToInt((point.z - horizontalRange - Origin.y) / CellSize), 0, Depth - 1);
            var maxZ = Mathf.Clamp(Mathf.FloorToInt((point.z + horizontalRange - Origin.y) / CellSize), 0, Depth - 1);
            for (var z = minZ; z <= maxZ; z++)
            for (var x = minX; x <= maxX; x++)
            {
                var column = Columns[z * Width + x];
                for (var i = column.FirstNode; i < column.FirstNode + column.Count; i++)
                {
                    var delta = Nodes[i].Position - point;
                    if (Mathf.Abs(delta.y) > verticalRange || delta.x * delta.x + delta.z * delta.z > horizontalRange * horizontalRange) continue;
                    var score = delta.x * delta.x + delta.z * delta.z + delta.y * delta.y * 9;
                    if (score >= best) continue;
                    best = score; node = i;
                }
            }
            return node >= 0;
        }

        private sealed class Route
        {
            public ulong Support;
            public int Target, Goal;
            public float Step, Drop, Gap, TransferHeight, Used;
            public float[] Cost;
            public int[] Next;
        }
        [NonSerialized] private List<Route> _routes;
        [NonSerialized] private int[] _incomingStarts, _incoming, _linkSources, _heap, _heapIndex;
        [NonSerialized] private int[] _boundaryNodes;
        [NonSerialized] private EnemyDeckNode[] _routingNodes;
        [NonSerialized] private int[] _routingLinks;

        public bool TryNearestBoundary(Vector3 point, float verticalRange, out int node)
        {
            node = -1;
            if (!IsBaked) return false;
            EnsureRouting();
            var best = float.PositiveInfinity;
            for (var index = 0; index < _boundaryNodes.Length; index++)
            {
                var candidate = _boundaryNodes[index];
                var delta = Nodes[candidate].Position - point;
                if (Mathf.Abs(delta.y) > verticalRange) continue;
                var score = delta.x * delta.x + delta.z * delta.z + delta.y * delta.y * 9f;
                if (score >= best) continue;
                best = score; node = candidate;
            }
            return node >= 0;
        }

        private void EnsureRouting()
        {
            if (_routingNodes == Nodes && _routingLinks == Links) return;
            _routingNodes = Nodes; _routingLinks = Links;
            _routes = new List<Route>(16);
            _incomingStarts = new int[Nodes.Length + 1];
            _incoming = new int[Links.Length];
            _linkSources = new int[Links.Length];
            _heap = new int[Nodes.Length]; _heapIndex = new int[Nodes.Length];
            var boundaryCount = 0;
            for (var i = 0; i < Nodes.Length; i++)
                if (Nodes[i].Boundary) boundaryCount++;
            _boundaryNodes = new int[boundaryCount];
            for (int i = 0, destination = 0; i < Nodes.Length; i++)
                if (Nodes[i].Boundary) _boundaryNodes[destination++] = i;
            foreach (var link in Links) _incomingStarts[link + 1]++;
            for (var i = 1; i < _incomingStarts.Length; i++) _incomingStarts[i] += _incomingStarts[i - 1];
            var cursor = (int[])_incomingStarts.Clone();
            for (var i = 0; i < Nodes.Length; i++)
                for (var j = Nodes[i].FirstLink; j < Nodes[i].FirstLink + Nodes[i].LinkCount; j++)
                { _incoming[cursor[Links[j]]++] = j; _linkSources[j] = i; }
        }

        // One reverse shortest-path field serves every bot pursuing this target on this
        // ship. Main-thread work is capped by the runtime's shared builds-per-frame budget.
        // No per-bot A*, Physics queries or allocations during movement along the route.
        public bool TryRoute(ulong support, int target, int start, int goal, float step, float drop,
            float now, ref int buildBudget, out int[] next, float maximumGap = 0f, float transferHeight = 0f)
        {
            next = null;
            if (!IsBaked || start < 0 || goal < 0 || start >= Nodes.Length || goal >= Nodes.Length) return false;
            EnsureRouting();
            Route route = null;
            foreach (var entry in _routes)
                if (entry.Support == support && entry.Target == target &&
                    Mathf.Abs(entry.Step - step) < 0.001f && Mathf.Abs(entry.Drop - drop) < 0.001f &&
                    Mathf.Abs(entry.Gap - maximumGap) < 0.001f && Mathf.Abs(entry.TransferHeight - transferHeight) < 0.001f)
                { route = entry; break; }
            // Retain a nearby target cell briefly; a walking player must not rebuild
            // an entire map whenever they cross a 10 cm cell boundary.
            if (route != null && (route.Goal == goal ||
                Mathf.Abs(Nodes[route.Goal].Position.y - Nodes[goal].Position.y) < 0.25f &&
                (Nodes[route.Goal].Position - Nodes[goal].Position).sqrMagnitude < 0.16f))
            { route.Used = now; next = route.Next; return !float.IsPositiveInfinity(route.Cost[start]); }
            if (buildBudget <= 0) return false;
            buildBudget--;
            if (route == null)
            {
                if (_routes.Count < 16)
                {
                    route = new Route { Cost = new float[Nodes.Length], Next = new int[Nodes.Length] };
                    _routes.Add(route);
                }
                else
                {
                    route = _routes[0];
                    foreach (var entry in _routes) if (entry.Used < route.Used) route = entry;
                }
            }
            route.Support = support; route.Target = target; route.Goal = goal;
            route.Step = step; route.Drop = drop; route.Used = now;
            route.Gap = maximumGap; route.TransferHeight = transferHeight;
            Array.Fill(route.Cost, float.PositiveInfinity); Array.Fill(route.Next, -1); Array.Fill(_heapIndex, -1);
            var heapCount = 1;
            _heap[0] = goal; _heapIndex[goal] = 0; route.Cost[goal] = 0; route.Next[goal] = goal;
            void Swap(int a, int b)
            {
                var value = _heap[a]; _heap[a] = _heap[b]; _heap[b] = value;
                _heapIndex[_heap[a]] = a; _heapIndex[_heap[b]] = b;
            }
            while (heapCount > 0)
            {
                var current = _heap[0]; _heapIndex[current] = -2;
                heapCount--;
                if (heapCount > 0)
                {
                    _heap[0] = _heap[heapCount]; _heapIndex[_heap[0]] = 0;
                    var parent = 0;
                    while (parent * 2 + 1 < heapCount)
                    {
                        var child = parent * 2 + 1;
                        if (child + 1 < heapCount && route.Cost[_heap[child + 1]] < route.Cost[_heap[child]]) child++;
                        if (route.Cost[_heap[parent]] <= route.Cost[_heap[child]]) break;
                        Swap(parent, child); parent = child;
                    }
                }
                for (var i = _incomingStarts[current]; i < _incomingStarts[current + 1]; i++)
                {
                    var link = _incoming[i];
                    var predecessor = _linkSources[link];
                    if (_heapIndex[predecessor] == -2) continue;
                    var delta = Nodes[current].Position - Nodes[predecessor].Position;
                    var gap = link < LinkGaps.Length ? LinkGaps[link] : 0f;
                    var arc = link < LinkArcs.Length ? LinkArcs[link] : 0f;
                    if (gap > 0 ? maximumGap <= 0 || gap > maximumGap + 0.001f ||
                        Mathf.Abs(delta.y) + arc > transferHeight + 0.001f :
                        delta.y > step + 0.001f || -delta.y > drop + 0.001f) continue;
                    var cost = route.Cost[current] + delta.magnitude + Mathf.Abs(delta.y) * 0.5f +
                        Mathf.Max(0, -delta.y - step) * 4 + gap * 2 + arc * 3;
                    if (cost >= route.Cost[predecessor]) continue;
                    route.Cost[predecessor] = cost; route.Next[predecessor] = current;
                    var child = _heapIndex[predecessor];
                    if (child < 0) { child = heapCount++; _heap[child] = predecessor; _heapIndex[predecessor] = child; }
                    while (child > 0)
                    {
                        var parent = (child - 1) / 2;
                        if (route.Cost[_heap[parent]] <= cost) break;
                        Swap(parent, child); child = parent;
                    }
                }
            }
            next = route.Next;
            return !float.IsPositiveInfinity(route.Cost[start]);
        }

        public bool MoveAlongRoute(int start, Vector3 from, int[] route, float distance,
            float step, float drop, out Vector3 feet, out Vector3 normal)
        {
            feet = from; normal = Nodes[start].Normal;
            var node = start;
            var moved = false;
            for (var turn = 0; turn < 32 && distance > 0.0001f; turn++)
            {
                var next = route[node];
                if (next < 0 || next == node) break;
                if (TryGap(node, next, out _, out _))
                {
                    // Approach the departure cell before handing off to a committed
                    // surface transfer. Never interpolate through unmapped cells here.
                    if (TryMove(node, feet, Vector3.MoveTowards(feet, Nodes[node].Position, distance),
                            step, drop, out var departure, out var departureNormal))
                    { moved |= (departure - feet).sqrMagnitude > 0.000001f; feet = departure; normal = departureNormal; }
                    break;
                }
                var waypoint = Nodes[next].Position;
                var horizontal = new Vector2(waypoint.x - feet.x, waypoint.z - feet.z).magnitude;
                if (horizontal < 0.0001f) { node = next; continue; }
                var fraction = Mathf.Min(1, distance / horizontal);
                var desired = Vector3.Lerp(feet, waypoint, fraction);
                if (!TryMove(node, feet, desired, step, drop, out var grounded, out var up))
                {
                    // A bot can enter a cell off-centre (boarding, crowd steering or
                    // a previous corner). Head to its centre before turning diagonally;
                    // a direct turn from the edge can cross an unconnected neighbour.
                    var centre = Vector3.MoveTowards(feet, Nodes[node].Position, distance);
                    if (TryMove(node, feet, centre, step, drop, out grounded, out up))
                    { moved |= (grounded - feet).sqrMagnitude > 0.000001f; feet = grounded; normal = up; }
                    break;
                }
                feet = grounded; normal = up; moved = true;
                distance -= horizontal * fraction;
                if (fraction < 1) break;
                node = next;
            }
            return moved;
        }

        public bool TryGap(int from, int to, out float gap, out float arc)
        {
            gap = arc = 0;
            if (from < 0 || from >= Nodes.Length || to < 0) return false;
            var node = Nodes[from];
            for (var i = node.FirstLink; i < node.FirstLink + node.LinkCount; i++)
                if (Links[i] == to && i < LinkGaps.Length && LinkGaps[i] > 0)
                { gap = LinkGaps[i]; arc = i < LinkArcs.Length ? LinkArcs[i] : 0; return true; }
            return false;
        }

        public bool TryMove(int startNode, Vector3 from, Vector3 desired, float stepHeight, float drop,
            out Vector3 grounded, out Vector3 normal)
        {
            grounded = from; normal = Vector3.up;
            if (startNode < 0 || startNode >= Nodes.Length) return false;
            var node = startNode;
            var distance = new Vector2(desired.x - from.x, desired.z - from.z).magnitude;
            var steps = Mathf.Max(1, Mathf.CeilToInt(distance / (CellSize * 0.4f)));
            if (steps > 256) return false;
            for (var i = 1; i <= steps; i++)
            {
                var point = Vector3.Lerp(from, desired, i / (float)steps);
                var column = ColumnAt(point);
                if (column < 0) return false;
                if (Nodes[node].Column != column)
                {
                    var previous = Nodes[node];
                    var next = -1;
                    var nearest = float.PositiveInfinity;
                    for (var link = previous.FirstLink; link < previous.FirstLink + previous.LinkCount; link++)
                    {
                        if (link < LinkGaps.Length && LinkGaps[link] > 0) continue;
                        var candidate = Links[link];
                        if (Nodes[candidate].Column != column) continue;
                        var dy = Nodes[candidate].Position.y - previous.Position.y;
                        if (dy > stepHeight + 0.001f || -dy > drop + 0.001f || Mathf.Abs(dy) >= nearest) continue;
                        next = candidate; nearest = Mathf.Abs(dy);
                    }
                    if (next < 0)
                    {
                        // At a diagonal cell corner, converting moving-ship world floats
                        // back to local coordinates can put a sample a millimetre into
                        // an adjacent column. Accept only an already connected cell whose
                        // footprint contains that sample within this numerical tolerance.
                        var half = CellSize * 0.5f + 0.003f;
                        for (var link = previous.FirstLink; link < previous.FirstLink + previous.LinkCount; link++)
                        {
                            if (link < LinkGaps.Length && LinkGaps[link] > 0) continue;
                            var candidate = Links[link]; var position = Nodes[candidate].Position;
                            var dy = position.y - previous.Position.y;
                            if (Mathf.Abs(position.x - point.x) > half || Mathf.Abs(position.z - point.z) > half ||
                                dy > stepHeight + 0.001f || -dy > drop + 0.001f || Mathf.Abs(dy) >= nearest) continue;
                            next = candidate; nearest = Mathf.Abs(dy);
                        }
                    }
                    if (next < 0) return false;
                    node = next;
                }
                grounded = point;
                grounded.y = Height(node, point);
            }
            normal = Nodes[node].Normal;
            return true;
        }

        public bool TryFollow(int startNode, Vector3 from, Vector3 goal, float distance, float stepHeight,
            float drop, out Vector3 best, out Vector3 normal)
        {
            best = from; normal = Vector3.up;
            var toward = new Vector3(goal.x - from.x, 0, goal.z - from.z);
            if (toward.sqrMagnitude < 0.0001f) return false;
            var direction = toward.normalized;
            var gain = 0.0001f;
            var found = false;
            for (var sample = 0; sample < 17; sample++)
            {
                var angle = sample == 0 ? 0 : ((sample + 1) / 2) * 15f * (sample % 2 == 0 ? -1 : 1);
                var candidate = sample == 13 ? Vector3.right : sample == 14 ? Vector3.left :
                    sample == 15 ? Vector3.forward : sample == 16 ? Vector3.back : Quaternion.AngleAxis(angle, Vector3.up) * direction;
                for (var scale = 1f; scale >= 0.24f; scale *= 0.5f)
                {
                    var point = from + candidate * (distance * scale);
                    var difference = toward.sqrMagnitude - new Vector2(goal.x - point.x, goal.z - point.z).sqrMagnitude;
                    if (difference <= gain || !TryMove(startNode, from, point, stepHeight, drop, out var feet, out var up)) continue;
                    gain = difference; best = feet; normal = up; found = true;
                    break;
                }
            }
            return found;
        }
    }
}
