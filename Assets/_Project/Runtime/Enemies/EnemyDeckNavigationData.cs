using System;
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
                        var candidate = Links[link];
                        if (Nodes[candidate].Column != column) continue;
                        var dy = Nodes[candidate].Position.y - previous.Position.y;
                        if (dy > stepHeight + 0.001f || -dy > drop + 0.001f || Mathf.Abs(dy) >= nearest) continue;
                        next = candidate; nearest = Mathf.Abs(dy);
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
