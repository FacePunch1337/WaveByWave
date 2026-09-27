using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using WaveByWave.Enemies;

namespace WaveByWave.Editor
{
    public static class EnemyDeckNavigationBaker
    {
        public static void ConfigurePlayerHold()
        {
            const string path = "Assets/_Project/Prefabs/Ship.prefab";
            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                if (root.GetComponent<WaveByWave.Ships.ShipHoldSabotage>() == null)
                {
                    root.AddComponent<WaveByWave.Ships.ShipHoldSabotage>();
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                }
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        public static void RebakeAssignedMaps(EnemyDeckNavigation nav)
        {
            Bake(nav);
            var primary = nav.Data;
            var radius = nav.AgentRadius; var height = nav.AgentHeight; var slope = nav.MaximumSlope;
            try
            {
                foreach (var map in nav.AdditionalAgentMaps ?? Array.Empty<EnemyDeckNavigationData>())
                {
                    if (map == null || map == primary) continue;
                    nav.Data = map; nav.AgentRadius = map.AgentRadius;
                    nav.AgentHeight = map.AgentHeight; nav.MaximumSlope = map.MaximumSlope;
                    Bake(nav);
                }
            }
            finally
            {
                nav.Data = primary; nav.AgentRadius = radius; nav.AgentHeight = height; nav.MaximumSlope = slope;
                EditorUtility.SetDirty(nav); PrefabUtility.RecordPrefabInstancePropertyModifications(nav);
            }
        }
        public static bool BakingEnabled
        {
            get
            {
                var catalog = AssetDatabase.LoadAssetAtPath<DotsEnemyCatalog>(EnemyContentSetup.CatalogPath);
                return catalog == null || catalog.UseBakedDeckNavigation;
            }
        }

        private struct Triangle { public Vector3 A, B, C, Normal; }
        private sealed class Geometry
        {
            public readonly List<Triangle> Triangles = new();
            public readonly Dictionary<int, List<int>> Buckets = new();
            public readonly List<Collider> ConvexSources = new();
            public Matrix4x4 ToWorld, ToLocal;
            public float MinimumY, MaximumY;
            public Vector2 Origin;
            public float Cell;
            public int Width, Depth;

            public int Column(Vector3 point)
            {
                var x = Mathf.FloorToInt((point.x - Origin.x) / Cell);
                var z = Mathf.FloorToInt((point.z - Origin.y) / Cell);
                return x >= 0 && z >= 0 && x < Width && z < Depth ? z * Width + x : -1;
            }

            public void Heights(Vector3 point, List<(float Height, Vector3 Normal)> hits)
            {
                hits.Clear();
                if (Buckets.TryGetValue(Column(point), out var triangles))
                foreach (var index in triangles)
                {
                    var t = Triangles[index];
                    var ab = new Vector2(t.B.x - t.A.x, t.B.z - t.A.z);
                    var ac = new Vector2(t.C.x - t.A.x, t.C.z - t.A.z);
                    var ap = new Vector2(point.x - t.A.x, point.z - t.A.z);
                    var denominator = ab.x * ac.y - ab.y * ac.x;
                    if (Mathf.Abs(denominator) < 0.0000001f) continue;
                    var u = (ap.x * ac.y - ap.y * ac.x) / denominator;
                    var v = (ab.x * ap.y - ab.y * ap.x) / denominator;
                    if (u < -0.0001f || v < -0.0001f || u + v > 1.0001f) continue;
                    hits.Add((t.A.y + u * (t.B.y - t.A.y) + v * (t.C.y - t.A.y), t.Normal));
                }
                // Convex MeshCollider uses a cooked hull, not sharedMesh's original
                // concave triangles. Query that exact shape while baking in the editor.
                var start = ToWorld.MultiplyPoint3x4(new Vector3(point.x, MaximumY + 1, point.z));
                var down = ToWorld.MultiplyVector(Vector3.down * (MaximumY - MinimumY + 2));
                var ray = new Ray(start, down.normalized);
                foreach (var collider in ConvexSources)
                    if (collider.Raycast(ray, out var hit, down.magnitude))
                        hits.Add((ToLocal.MultiplyPoint3x4(hit.point).y,
                            ToWorld.transpose.MultiplyVector(hit.normal).normalized));
            }

            public bool Blocked(Vector3 from, Vector3 to)
            {
                var worldFrom = ToWorld.MultiplyPoint3x4(from);
                var worldTo = ToWorld.MultiplyPoint3x4(to);
                var worldDelta = worldTo - worldFrom;
                if (worldDelta.sqrMagnitude > 0.000001f)
                    foreach (var collider in ConvexSources)
                        if ((collider.ClosestPoint(worldFrom) - worldFrom).sqrMagnitude < 0.0000001f ||
                            collider.Raycast(new Ray(worldFrom, worldDelta.normalized), out _, worldDelta.magnitude) ||
                            collider.Raycast(new Ray(worldTo, -worldDelta.normalized), out _, worldDelta.magnitude)) return true;
                var minX = Mathf.Clamp(Mathf.FloorToInt((Mathf.Min(from.x, to.x) - Origin.x) / Cell), 0, Width - 1);
                var maxX = Mathf.Clamp(Mathf.FloorToInt((Mathf.Max(from.x, to.x) - Origin.x) / Cell), 0, Width - 1);
                var minZ = Mathf.Clamp(Mathf.FloorToInt((Mathf.Min(from.z, to.z) - Origin.y) / Cell), 0, Depth - 1);
                var maxZ = Mathf.Clamp(Mathf.FloorToInt((Mathf.Max(from.z, to.z) - Origin.y) / Cell), 0, Depth - 1);
                var direction = to - from;
                for (var z = minZ; z <= maxZ; z++)
                for (var x = minX; x <= maxX; x++)
                {
                    if (!Buckets.TryGetValue(z * Width + x, out var triangles)) continue;
                    foreach (var index in triangles)
                    {
                        var t = Triangles[index];
                        var ab = t.B - t.A; var ac = t.C - t.A;
                        var p = Vector3.Cross(direction, ac);
                        var determinant = Vector3.Dot(ab, p);
                        if (Mathf.Abs(determinant) < 0.0000001f) continue;
                        var delta = from - t.A;
                        var u = Vector3.Dot(delta, p) / determinant;
                        if (u < 0 || u > 1) continue;
                        var q = Vector3.Cross(delta, ab);
                        var v = Vector3.Dot(direction, q) / determinant;
                        if (v < 0 || u + v > 1) continue;
                        var fraction = Vector3.Dot(ac, q) / determinant;
                        if (fraction > 0.001f && fraction < 0.999f) return true;
                    }
                }
                return false;
            }
        }

        private static Collider[] Sources(EnemyDeckNavigation nav)
        {
            var rootBody = nav.GetComponent<Rigidbody>();
            var explicitSources = nav.Sources != null && nav.Sources.Length > 0;
            var colliders = explicitSources ? nav.Sources : nav.GetComponentsInChildren<Collider>(true);
            return colliders.Where(c => c != null && c.enabled && !c.isTrigger &&
                (explicitSources || c.gameObject.activeInHierarchy &&
                    (c.attachedRigidbody == null || c.attachedRigidbody == rootBody)) &&
                (c is MeshCollider || c is BoxCollider)).ToArray();
        }

        public static string SourceHash(EnemyDeckNavigation nav)
        {
            var text = new StringBuilder("deck-v4-routing|").Append(nav.CellSize).Append('|').Append(nav.AgentRadius)
                .Append('|').Append(nav.AgentHeight).Append('|').Append(nav.MaximumSlope).Append('|')
                .Append(nav.StepHeight).Append('|').Append(nav.MaximumDrop).Append('|').Append(nav.MaximumGap).Append('|').Append(nav.TransferHeight);
            foreach (var collider in Sources(nav))
            {
                text.Append('|').Append((nav.transform.worldToLocalMatrix * collider.transform.localToWorldMatrix).ToString("R"));
                if (collider is MeshCollider mesh && mesh.sharedMesh != null)
                {
                    text.Append(AssetDatabase.GetAssetDependencyHash(AssetDatabase.GetAssetPath(mesh.sharedMesh)));
                    text.Append(mesh.sharedMesh.name).Append(mesh.sharedMesh.vertexCount);
                    text.Append(mesh.convex);
                }
                if (collider is BoxCollider box) text.Append(box.center.ToString("R")).Append(box.size.ToString("R"));
            }
            return Hash128.Compute(text.ToString()).ToString();
        }

        public static void Bake(EnemyDeckNavigation nav, string assetPath = null, bool isolatedSetup = false)
        {
            if (nav.GetComponent<EnemyShipView>() != null)
                throw new InvalidOperationException("Enemy ships use collider movement; bake navigation only for the player ship.");
            if (EditorApplication.isPlayingOrWillChangePlaymode &&
                !(isolatedSetup && EditorSceneManager.IsPreviewSceneObject(nav.gameObject)))
                throw new InvalidOperationException("Bake navigation outside Play Mode.");
            if (!BakingEnabled && !isolatedSetup)
            {
                Debug.Log("[Enemies] Deck-map baking is disabled in SkeletonEnemyCatalog. Ship movement uses colliders.", nav);
                return;
            }
            var sources = Sources(nav);
            if (sources.Length == 0) throw new InvalidOperationException("No solid MeshColliders or BoxColliders were selected.");
            var geometry = new Geometry { Cell = Mathf.Clamp(nav.CellSize, 0.1f, 1f),
                ToWorld = nav.transform.localToWorldMatrix, ToLocal = nav.transform.worldToLocalMatrix };
            Physics.SyncTransforms();
            var bounds = new Bounds(); var hasBounds = false;
            foreach (var collider in sources)
            {
                var matrix = nav.transform.worldToLocalMatrix * collider.transform.localToWorldMatrix;
                Vector3[] vertices; int[] indices;
                if (collider is MeshCollider mesh)
                {
                    if (mesh.sharedMesh == null) continue;
                    vertices = mesh.sharedMesh.vertices; indices = mesh.sharedMesh.triangles;
                }
                else
                {
                    var box = (BoxCollider)collider;
                    vertices = new Vector3[8];
                    for (var i = 0; i < 8; i++) vertices[i] = box.center + Vector3.Scale(box.size * 0.5f,
                        new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    indices = new[] { 0,1,5,0,5,4, 2,6,7,2,7,3, 0,4,6,0,6,2,
                        1,3,7,1,7,5, 0,2,3,0,3,1, 4,5,7,4,7,6 };
                }
                for (var i = 0; i < vertices.Length; i++)
                {
                    vertices[i] = matrix.MultiplyPoint3x4(vertices[i]);
                    if (!hasBounds) { bounds = new Bounds(vertices[i], Vector3.zero); hasBounds = true; }
                    else bounds.Encapsulate(vertices[i]);
                }
                if (collider is MeshCollider convex && convex.convex)
                { geometry.ConvexSources.Add(collider); continue; }
                for (var i = 0; i + 2 < indices.Length; i += 3)
                {
                    var a = vertices[indices[i]]; var b = vertices[indices[i + 1]]; var c = vertices[indices[i + 2]];
                    var normal = Vector3.Cross(b - a, c - a).normalized;
                    if (normal.sqrMagnitude > 0.1f) geometry.Triangles.Add(new Triangle { A = a, B = b, C = c, Normal = normal });
                }
            }
            if (!hasBounds || geometry.Triangles.Count == 0 && geometry.ConvexSources.Count == 0)
                throw new InvalidOperationException("The selected geometry is empty.");
            geometry.MinimumY = bounds.min.y; geometry.MaximumY = bounds.max.y;
            geometry.Origin = new Vector2(bounds.min.x - geometry.Cell, bounds.min.z - geometry.Cell);
            geometry.Width = Mathf.CeilToInt(bounds.size.x / geometry.Cell) + 2;
            geometry.Depth = Mathf.CeilToInt(bounds.size.z / geometry.Cell) + 2;
            if ((long)geometry.Width * geometry.Depth > 250000) throw new InvalidOperationException("Navigation map is too large. Increase Cell Size or select only deck colliders.");
            for (var i = 0; i < geometry.Triangles.Count; i++)
            {
                var t = geometry.Triangles[i];
                var min = Vector3.Min(t.A, Vector3.Min(t.B, t.C));
                var max = Vector3.Max(t.A, Vector3.Max(t.B, t.C));
                var low = geometry.Column(min); var high = geometry.Column(max);
                for (var z = low / geometry.Width; z <= high / geometry.Width; z++)
                for (var x = low % geometry.Width; x <= high % geometry.Width; x++)
                {
                    var key = z * geometry.Width + x;
                    if (!geometry.Buckets.TryGetValue(key, out var bucket)) geometry.Buckets.Add(key, bucket = new List<int>());
                    bucket.Add(i);
                }
            }
            var nodes = new List<EnemyDeckNode>();
            var columns = new EnemyDeckColumn[geometry.Width * geometry.Depth];
            var hits = new List<(float Height, Vector3 Normal)>();
            var nearby = new List<(float Height, Vector3 Normal)>();
            var minimumNormal = Mathf.Cos(nav.MaximumSlope * Mathf.Deg2Rad);
            try
            {
                for (var column = 0; column < columns.Length; column++)
                {
                    if (!isolatedSetup && column % 256 == 0 && EditorUtility.DisplayCancelableProgressBar("Bake deck navigation", "Sampling floors and clearance", column / (float)columns.Length * 0.7f))
                        throw new OperationCanceledException();
                    var center = new Vector3(geometry.Origin.x + (column % geometry.Width + 0.5f) * geometry.Cell, 0,
                        geometry.Origin.y + (column / geometry.Width + 0.5f) * geometry.Cell);
                    geometry.Heights(center, hits);
                    hits.Sort((a, b) => a.Height.CompareTo(b.Height));
                    var first = nodes.Count;
                    foreach (var hit in hits)
                    {
                        if (hit.Normal.y < minimumNormal || nodes.Count > first && Mathf.Abs(nodes[nodes.Count - 1].Position.y - hit.Height) < 0.02f) continue;
                        center.y = hit.Height;
                        if (!Clearance(geometry, nav, center, hit.Normal, nearby, minimumNormal)) continue;
                        nodes.Add(new EnemyDeckNode { Position = center, Normal = hit.Normal, Column = column });
                    }
                    columns[column] = new EnemyDeckColumn { FirstNode = first, Count = nodes.Count - first };
                }
                var links = new List<int>();
                var gaps = new List<float>(); var arcs = new List<float>();
                for (var i = 0; i < nodes.Count; i++)
                {
                    var node = nodes[i]; node.FirstLink = links.Count;
                    var x = node.Column % geometry.Width; var z = node.Column / geometry.Width;
                    var connectedDirections = 0;
                    for (var dz = -1; dz <= 1; dz++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dz == 0 || x + dx < 0 || x + dx >= geometry.Width || z + dz < 0 || z + dz >= geometry.Depth) continue;
                        var column = columns[(z + dz) * geometry.Width + x + dx];
                        var connected = false;
                        for (var j = column.FirstNode; j < column.FirstNode + column.Count; j++)
                        {
                            var to = nodes[j]; var dy = to.Position.y - node.Position.y;
                            if (dy > nav.StepHeight + 0.001f || -dy > nav.MaximumDrop + 0.001f) continue;
                            // Check intermediate support and chest clearance, including diagonal corners.
                            var valid = true;
                            for (var sample = 1; sample < 4 && valid; sample++)
                            {
                                var point = Vector3.Lerp(node.Position, to.Position, sample / 4f);
                                geometry.Heights(point, nearby);
                                valid = nearby.Any(h => h.Normal.y >= minimumNormal && Mathf.Abs(h.Height - point.y) <= nav.StepHeight + 0.02f);
                            }
                            var clearance = Vector3.up * Mathf.Min(nav.AgentHeight * 0.5f, nav.StepHeight + 0.05f);
                            if (!valid || geometry.Blocked(node.Position + clearance, to.Position + clearance)) continue;
                            links.Add(j); gaps.Add(0); arcs.Add(0); connected = true;
                        }
                        if (connected) { connectedDirections++; continue; }
                        // Offline only: bridge missing cells over railings, steps and gaps.
                        // Every bridge checks the whole body sweep; a cabin wall or ceiling
                        // cannot become a shortcut to another deck.
                        var stride = geometry.Cell * Mathf.Sqrt(dx * dx + dz * dz);
                        for (var span = 1; !connected && span * stride <= nav.MaximumGap; span++)
                        {
                            var nx = x + dx * span; var nz = z + dz * span;
                            if (nx < 0 || nz < 0 || nx >= geometry.Width || nz >= geometry.Depth) break;
                            var destination = columns[nz * geometry.Width + nx];
                            for (var j = destination.FirstNode; j < destination.FirstNode + destination.Count; j++)
                            {
                                var to = nodes[j].Position;
                                if (Mathf.Abs(to.y - node.Position.y) > nav.TransferHeight) continue;
                                if (!BridgeClear(geometry, nav, node.Position, to, out var arc)) continue;
                                links.Add(j); gaps.Add(span * stride); arcs.Add(arc); connected = true;
                            }
                        }
                    }
                    node.LinkCount = links.Count - node.FirstLink;
                    node.Boundary = connectedDirections < 8;
                    nodes[i] = node;
                }
                if (nodes.Count == 0) throw new InvalidOperationException("No walkable deck found. Check collider selection, agent clearance and slope settings.");
                var data = nav.Data;
                if (data == null)
                {
                    if (string.IsNullOrEmpty(assetPath)) assetPath = EditorUtility.SaveFilePanelInProject("Save deck navigation", nav.name + "_Navigation", "asset", "Choose a project asset path.");
                    if (string.IsNullOrEmpty(assetPath)) return;
                    Directory.CreateDirectory(Path.GetDirectoryName(assetPath));
                    data = AssetDatabase.LoadAssetAtPath<EnemyDeckNavigationData>(assetPath);
                    if (data == null) { data = ScriptableObject.CreateInstance<EnemyDeckNavigationData>(); AssetDatabase.CreateAsset(data, assetPath); }
                }
                Undo.RecordObject(nav, "Assign baked deck navigation");
                data.Origin = geometry.Origin; data.CellSize = geometry.Cell; data.Width = geometry.Width; data.Depth = geometry.Depth;
                data.AgentRadius = nav.AgentRadius; data.AgentHeight = nav.AgentHeight; data.MaximumSlope = nav.MaximumSlope;
                data.StepHeight = nav.StepHeight; data.MaximumDrop = nav.MaximumDrop;
                data.Columns = columns; data.Nodes = nodes.ToArray(); data.Links = links.ToArray();
                data.LinkGaps = gaps.ToArray(); data.LinkArcs = arcs.ToArray(); data.SourceHash = SourceHash(nav);
                nav.Data = data;
                EditorUtility.SetDirty(data); EditorUtility.SetDirty(nav);
                PrefabUtility.RecordPrefabInstancePropertyModifications(nav);
                AssetDatabase.SaveAssetIfDirty(data);
                Debug.Log($"[Enemies] Baked {data.Nodes.Length} deck nodes, {data.Links.Length} connections for {nav.name}.");
            }
            finally { EditorUtility.ClearProgressBar(); }
        }

        [MenuItem("Tools/Wave by Wave/Enemies/Bake player ship routes and enable navigation")]
        public static void BakeProjectShipRoutes()
        {
            ConfigurePlayerHold();
            var profilePaths = new[] { EnemyContentSetup.CatalogPath,
                "Assets/_Project/Resources/Enemies/AmphibianEnemyCatalog.asset",
                "Assets/_Project/Resources/Enemies/TrollEnemyCatalog.asset" };
            const string name = "PlayerShip";
            const string prefabPath = "Assets/_Project/Prefabs/Ship.prefab";
            {
                var root = PrefabUtility.LoadPrefabContents(prefabPath);
                try
                {
                    var nav = root.GetComponent<EnemyDeckNavigation>();
                    if (nav == null) nav = root.AddComponent<EnemyDeckNavigation>();
                    var originalRadius = nav.AgentRadius; var originalHeight = nav.AgentHeight;
                    var originalSlope = nav.MaximumSlope;
                    var scale = Mathf.Max(0.001f, Mathf.Min(Mathf.Abs(root.transform.lossyScale.x),
                        Mathf.Abs(root.transform.lossyScale.y), Mathf.Abs(root.transform.lossyScale.z)));
                    var maps = new List<EnemyDeckNavigationData>();
                    foreach (var profilePath in profilePaths)
                    {
                        // Opening/importing prefab contents can release unused assets;
                        // acquire each profile after opening, immediately before use.
                        var profile = AssetDatabase.LoadAssetAtPath<DotsEnemyCatalog>(profilePath);
                        if (profile == null) throw new InvalidOperationException("Missing enemy profile: " + profilePath);
                        var suffix = profilePath == EnemyContentSetup.CatalogPath ? "" : "_" + profile.Kind;
                        var path = "Assets/_Project/Data/Enemies/" + name + "Navigation" + suffix + ".asset";
                        nav.AgentRadius = profile.DeckAgentRadius / scale;
                        nav.AgentHeight = profile.DeckAgentHeight / Mathf.Max(0.001f, Mathf.Abs(root.transform.lossyScale.y));
                        nav.MaximumSlope = Mathf.Min(originalSlope, profile.MaximumSlope);
                        nav.Data = AssetDatabase.LoadAssetAtPath<EnemyDeckNavigationData>(path);
                        Bake(nav, path, true);
                        maps.Add(nav.Data);
                    }
                    nav.AgentRadius = originalRadius; nav.AgentHeight = originalHeight; nav.MaximumSlope = originalSlope;
                    nav.Data = maps[0];
                    nav.AdditionalAgentMaps = maps.Skip(1).ToArray();
                    PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            var skeleton = AssetDatabase.LoadAssetAtPath<DotsEnemyCatalog>(EnemyContentSetup.CatalogPath);
            skeleton.UseBakedDeckNavigation = true;
            EditorUtility.SetDirty(skeleton);
            AssetDatabase.SaveAssetIfDirty(skeleton);
        }

        private static bool BridgeClear(Geometry geometry, EnemyDeckNavigation nav, Vector3 from, Vector3 to, out float arc)
        {
            var side = Vector3.Cross(Vector3.up, to - from).normalized * nav.AgentRadius;
            var samples = Mathf.Max(4, Mathf.CeilToInt(Vector3.Distance(from, to) / 0.15f));
            for (var attempt = 0; attempt < 7; attempt++)
            {
                arc = attempt == 0 ? 0 : 0.125f * (1 << attempt);
                if (Mathf.Abs(to.y - from.y) + arc > nav.TransferHeight) break;
                var clear = true;
                var previous = from;
                for (var sample = 1; clear && sample <= samples; sample++)
                {
                    var fraction = sample / (float)samples;
                    var point = Vector3.Lerp(from, to, fraction) + Vector3.up * (Mathf.Sin(fraction * Mathf.PI) * arc);
                    for (var lane = -1; clear && lane <= 1; lane++)
                    for (var height = 0; clear && height < 3; height++)
                    {
                        var offset = side * lane + Vector3.up * (height == 0 ? 0.06f : height == 1 ? nav.AgentHeight * 0.5f : nav.AgentHeight - 0.02f);
                        clear = !geometry.Blocked(previous + offset, point + offset);
                    }
                    previous = point;
                }
                if (clear) return true;
            }
            arc = 0; return false;
        }

        private static bool Clearance(Geometry geometry, EnemyDeckNavigation nav, Vector3 center, Vector3 normal,
            List<(float Height, Vector3 Normal)> hits, float minimumNormal)
        {
            var radius = nav.AgentRadius + geometry.Cell * 0.71f;
            for (var sample = 0; sample < 9; sample++)
            {
                var angle = sample * Mathf.PI * 0.25f;
                var offset = sample == 8 ? Vector3.zero : new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * radius;
                var point = center + offset;
                point.y = center.y - (offset.x * normal.x + offset.z * normal.z) / normal.y;
                geometry.Heights(point, hits);
                var supportingHeight = float.PositiveInfinity;
                var difference = nav.StepHeight + 0.02f;
                foreach (var hit in hits)
                {
                    var delta = Mathf.Abs(hit.Height - point.y);
                    if (hit.Normal.y < minimumNormal || delta > difference) continue;
                    difference = delta; supportingHeight = hit.Height;
                }
                if (float.IsPositiveInfinity(supportingHeight)) return false;
                // Adjacent samples on stairs stand on different treads. Checking their
                // headroom from the centre tread treated the next step's underside as
                // a low ceiling and disconnected every staircase from the decks.
                point.y = supportingHeight;
                if (hits.Any(h => h.Height > point.y + 0.05f && h.Height < point.y + nav.AgentHeight &&
                    (h.Normal.y < 0 || h.Height > point.y + nav.StepHeight + 0.02f))) return false;
                var up = Vector3.up * Mathf.Min(nav.AgentHeight * 0.5f, nav.StepHeight + 0.05f);
                if (geometry.Blocked(center + up, point + up) ||
                    geometry.Blocked(center + Vector3.up * (nav.AgentHeight - 0.05f), point + Vector3.up * (nav.AgentHeight - 0.05f))) return false;
            }
            return true;
        }
    }

    [CustomEditor(typeof(EnemyDeckNavigation))]
    public sealed class EnemyDeckNavigationEditor : UnityEditor.Editor
    {
        private string _hash;
        private bool _dirty = true;
        private void MarkDirty() { _dirty = true; Repaint(); }
        private void OnEnable()
        {
            EditorApplication.hierarchyChanged += MarkDirty;
            EditorApplication.projectChanged += MarkDirty;
            Undo.undoRedoPerformed += MarkDirty;
        }
        private void OnDisable()
        {
            EditorApplication.hierarchyChanged -= MarkDirty;
            EditorApplication.projectChanged -= MarkDirty;
            Undo.undoRedoPerformed -= MarkDirty;
        }
        public override void OnInspectorGUI()
        {
            var nav = (EnemyDeckNavigation)target;
            if (DrawDefaultInspector()) _dirty = true;
            if (!EnemyDeckNavigationBaker.BakingEnabled)
            {
                EditorGUILayout.HelpBox("Запечённая навигация отключена. Боты перемещаются по текущим коллайдерам корабля; карта палубы не используется и не запекается. Сохранённые карты можно снова включить через Use Baked Deck Navigation в SkeletonEnemyCatalog.", MessageType.Info);
                if (GUILayout.Button("Открыть настройки движения"))
                    Selection.activeObject = AssetDatabase.LoadAssetAtPath<DotsEnemyCatalog>(EnemyContentSetup.CatalogPath);
                return;
            }
            if (_dirty)
            { _hash = EnemyDeckNavigationBaker.SourceHash(nav); _dirty = false; }
            if (nav.Data == null || !nav.Data.IsBaked)
                EditorGUILayout.HelpBox("No baked map. Skeletons use the existing collider-based movement until you bake.", MessageType.Warning);
            else if (nav.Data.SourceHash != _hash)
                EditorGUILayout.HelpBox("Geometry or navigation settings changed. Re-bake the deck map.", MessageType.Warning);
            else EditorGUILayout.HelpBox($"{nav.Data.Nodes.Length} nodes. Shared ship-local data; sailing and waves do not require rebaking.", MessageType.Info);
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                if (GUILayout.Button("Bake deck navigation / Запечь карту палубы"))
                { EnemyDeckNavigationBaker.RebakeAssignedMaps(nav); _hash = EnemyDeckNavigationBaker.SourceHash(nav); }
                if (GUILayout.Button("Check geometry changes")) _hash = EnemyDeckNavigationBaker.SourceHash(nav);
            }
        }
    }
}
