using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using WaveByWave.Enemies;
using WaveByWave.Generation;
using WaveByWave.Items;
using Object = UnityEngine.Object;

namespace WaveByWave.Editor
{
    [InitializeOnLoad]
    public static class EnemyContentSetup
    {
        public const string CatalogPath = "Assets/_Project/Resources/SkeletonEnemyCatalog.asset";
        private const string Folder = "Assets/_Project/Prefabs/Enemies";
        private const string BakeFolder = "Assets/_Project/Data/Enemies";
        private static bool _started;
        static EnemyContentSetup()
        {
            EditorApplication.update += Ensure;
            EditorApplication.playModeStateChanged += state =>
            {
                if (state != PlayModeStateChange.ExitingEditMode) return;
                var catalog = AssetDatabase.LoadAssetAtPath<DotsEnemyCatalog>(CatalogPath);
                if (catalog == null || catalog.BakeSourceHash == SourceHash(catalog)) return;
                try { Bake(catalog); }
                catch (Exception exception) { EditorApplication.isPlaying = false; Debug.LogException(exception); }
            };
        }

        private static void Ensure()
        {
            if (_started || EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode) return;
            _started = true;
            EditorApplication.update -= Ensure;
            var catalog = AssetDatabase.LoadAssetAtPath<DotsEnemyCatalog>(CatalogPath);
            if (catalog != null && catalog.IsBaked && catalog.BakeSourceHash?.StartsWith("vat4:") == true) return;
            try { CreateContent(); }
            catch (Exception exception) { Debug.LogException(exception); }
        }

        [MenuItem("Tools/Wave by Wave/Enemies/Create content and bake animations")]
        public static void CreateContent()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Stop Play Mode before baking enemy content.");
            Directory.CreateDirectory(Folder);
            Directory.CreateDirectory(BakeFolder);
            AssetDatabase.Refresh();
            var catalog = AssetDatabase.LoadAssetAtPath<DotsEnemyCatalog>(CatalogPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<DotsEnemyCatalog>();
                AssetDatabase.CreateAsset(catalog, CatalogPath);
                catalog.BakingRigPrefab = LoadPrefab("Characters/skeleton_01.prefab");
                catalog.IdleClip = Clip("pirate_idle_01");
                catalog.RunClip = Clip("pirate_run_forward");
                catalog.MeleeAttackClip = Clip("pirate_attack_01");
                catalog.PistolAttackClip = Clip("pirate_shoot_01");
                catalog.RifleAttackClip = Clip("pirate_shoot_02");
                catalog.SpawnSmokePrefab = catalog.DeathSmokePrefab =
                    AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/Effects/DeathDust.prefab");
                catalog.WaterProfile = AssetDatabase.LoadAssetAtPath<OceanGenerationSettings>(
                    "Assets/_Project/Resources/OceanGeneration.asset")?.WaterProfile;
                catalog.SkeletonMaterials = Enumerable.Range(1, 4).Select(i => AssetDatabase.LoadAssetAtPath<Material>(
                    $"Assets/Pirates/Materials/Characters/skeleton_0{i}.mat")).ToArray();
                catalog.LootDrops = new[] { "Item_food", "Item_plank", "Item_cannonball", "Item_cutlass", "Item_musket" }
                    .Select(name => AssetDatabase.LoadAssetAtPath<ItemDefinition>($"Assets/_Project/Data/{name}.asset")).ToArray();
                Add(catalog, EnemyBakedPartCategory.Body, "Characters/skeleton_01.prefab");
                foreach (var name in new[] { "Bandana_01", "Bandana_02", "Bandana_03" })
                    Add(catalog, EnemyBakedPartCategory.Bandana, $"Clothes/{name}.prefab", "CATHead",
                        new Vector3(0, 0.015f, 0.003f), new Vector3(0, 90, 90));
                foreach (var name in new[] { "Hat_01", "Hat_02", "Hat_03", "Hat_skull_01", "Hat_skull_02", "Hat_skull_03" })
                    Add(catalog, EnemyBakedPartCategory.Hat, $"Clothes/{name}.prefab", "CATHead",
                        new Vector3(0, 0.015f, 0.003f), new Vector3(0, 90, 90));
                for (var i = 1; i <= 3; i++) Add(catalog, EnemyBakedPartCategory.Coat, $"Clothes/Coat_0{i}.prefab");
                for (var i = 1; i <= 4; i++)
                {
                    Add(catalog, EnemyBakedPartCategory.GloveLeft, $"Clothes/Glove_Left_0{i}.prefab", pair: i);
                    Add(catalog, EnemyBakedPartCategory.GloveRight, $"Clothes/Glove_Right_0{i}.prefab", pair: i);
                    Add(catalog, EnemyBakedPartCategory.BootLeft, $"Clothes/Boot_Left_0{i}.prefab", pair: i);
                    Add(catalog, EnemyBakedPartCategory.BootRight, $"Clothes/Boot_Right_0{i}.prefab", pair: i);
                }
                Add(catalog, EnemyBakedPartCategory.WoodenLegLeft, "Clothes/Wooden_Leg_Left.prefab", pair: 5);
                Add(catalog, EnemyBakedPartCategory.WoodenLegRight, "Clothes/Wooden_Leg_Right.prefab", pair: 5);
                // Rigid accessory prefabs use their bind-pose position; the baker converts them
                // into the head's coordinate space, preserving the pack's authored alignment.
                Add(catalog, EnemyBakedPartCategory.EyePatch, "Clothes/Eye_patch.prefab", "CATHead");
                Add(catalog, EnemyBakedPartCategory.Earring, "Clothes/Earring_01.prefab", "CATHead");
                Add(catalog, EnemyBakedPartCategory.Earring, "Clothes/Earring_02.prefab", "CATHead");
                foreach (var name in new[] { "Sabre_01", "Sabre_02", "Sabre_03", "Sword_01", "Sword_02", "Sword_03" })
                    Add(catalog, EnemyBakedPartCategory.MeleeWeapon, $"Weapons/{name}.prefab", "CATRHand",
                        Vector3.zero, new Vector3(0, 0, -90));
                Add(catalog, EnemyBakedPartCategory.PistolWeapon, "Weapons/Gun_01.prefab", "CATRHand",
                    Vector3.zero, new Vector3(0, 0, -90));
                Add(catalog, EnemyBakedPartCategory.RifleWeapon, "Weapons/Rifle_01.prefab", "CATRHand",
                    Vector3.zero, new Vector3(0, 0, -90));
            }
            // Migrate the initial generated catalog only; never rewrite tuned attachments on a rebake.
            if (catalog.BakeSourceHash?.StartsWith("vat4:") != true)
            {
                var orientation = new Quaternion(-0.5f, 0.5f, 0.5f, 0.5f).eulerAngles;
                foreach (var source in catalog.PartSources)
                {
                    if (source.Category is EnemyBakedPartCategory.MeleeWeapon or EnemyBakedPartCategory.PistolWeapon or EnemyBakedPartCategory.RifleWeapon)
                        UseAuthoredGrip(source);
                    if (source.Category is EnemyBakedPartCategory.Hat or EnemyBakedPartCategory.Bandana or
                        EnemyBakedPartCategory.EyePatch or EnemyBakedPartCategory.Earring)
                    {
                        source.LocalEulerAngles = orientation;
                        source.LocalPosition = new Vector3(0, 0.007279564f, -0.00063627237f) +
                            (source.Category == EnemyBakedPartCategory.EyePatch ? new Vector3(0.019f, 0.049f, -0.002f) :
                                new Vector3(0, 0.015f, 0.003f)) * 1.048375f;
                        source.LocalScale = Vector3.one * 1.048375f;
                    }
                }
                if (!catalog.PartSources.Any(p => p.Category == EnemyBakedPartCategory.HookLeft))
                    Add(catalog, EnemyBakedPartCategory.HookLeft, "Clothes/Hook_Left.prefab");
            }
            CreatePrefabs(catalog);
            Bake(catalog);
        }

        public static string SourceHash(DotsEnemyCatalog catalog)
        {
            var text = new StringBuilder("vat4:").Append(catalog.BakeFramesPerSecond);
            void Asset(Object obj) => text.Append('|').Append(obj != null ?
                AssetDatabase.GetAssetDependencyHash(AssetDatabase.GetAssetPath(obj)).ToString() : "null");
            Asset(catalog.BakingRigPrefab);
            for (var i = 0; i < 5; i++) Asset(catalog.Clip((EnemyAnimationState)i));
            foreach (var source in catalog.PartSources)
            {
                Asset(source.Prefab);
                text.Append((int)source.Category).Append('|').Append(source.Pair).Append('|').Append(source.AttachBone)
                    .Append(JsonUtility.ToJson(source.LocalPosition)).Append(JsonUtility.ToJson(source.LocalEulerAngles))
                    .Append(JsonUtility.ToJson(source.LocalScale));
            }
            return "vat4:" + Hash128.Compute(text.ToString());
        }
        private static void UseAuthoredGrip(EnemyPartSource source)
        {
            // Read the asset pack's original weapon sockets, including its non-zero palm offset.
            var presetIndex = source.Category == EnemyBakedPartCategory.PistolWeapon ? 3 :
                source.Category == EnemyBakedPartCategory.RifleWeapon ? 5 : source.Prefab.name.StartsWith("Sword") ? 9 : 2;
            var preset = LoadPrefab($"Characters/skeleton_{presetIndex:00}.prefab");
            var nodes = preset.GetComponentsInChildren<Transform>(true);
            var prefix = source.Category == EnemyBakedPartCategory.PistolWeapon ? "Gun_" :
                source.Category == EnemyBakedPartCategory.RifleWeapon ? "Rifle_" : source.Prefab.name.StartsWith("Sword") ? "Sword_" : "Sabre_";
            var weapon = nodes.First(t => t.name.StartsWith(prefix));
            var hand = weapon.parent;
            while (hand != null && hand.name != "CATRHand" && hand.name != "CATLHand") hand = hand.parent;
            if (hand == null) throw new InvalidOperationException("Source weapon is not attached to a hand: " + source.Prefab.name);
            var matrix = hand.worldToLocalMatrix * weapon.localToWorldMatrix;
            source.AttachBone = hand.name;
            source.LocalPosition = matrix.GetColumn(3);
            source.LocalEulerAngles = matrix.rotation.eulerAngles;
            source.LocalScale = matrix.lossyScale;
        }
        private static void Add(DotsEnemyCatalog catalog, EnemyBakedPartCategory category, string path,
            string bone = null, Vector3 position = default, Vector3 euler = default, int pair = 0)
        {
            var prefab = LoadPrefab(path);
            if (prefab != null) catalog.PartSources.Add(new EnemyPartSource
            { Category = category, Prefab = prefab, AttachBone = bone, LocalPosition = position, LocalEulerAngles = euler, Pair = pair });
        }
        private static GameObject LoadPrefab(string name) => AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Pirates/Prefabs/" + name);
        private static AnimationClip Clip(string name) =>
            AssetDatabase.LoadAllAssetsAtPath($"Assets/Pirates/Animations/generic/{name}.FBX")
                .OfType<AnimationClip>().First(c => !c.name.StartsWith("__preview__"));

        private static void CreatePrefabs(DotsEnemyCatalog catalog)
        {
            for (var i = 0; i < 2; i++)
            {
                var path = $"{Folder}/{(i == 0 ? "SkeletonSpawn_OnAppearance" : "SkeletonSpawn_OnPlayerRadius")}.prefab";
                if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null) continue;
                var root = new GameObject(Path.GetFileNameWithoutExtension(path), typeof(EnemySpawnPoint));
                root.GetComponent<EnemySpawnPoint>().Mode = (EnemySpawnMode)i;
                PrefabUtility.SaveAsPrefabAsset(root, path);
                Object.DestroyImmediate(root);
            }
            var starPath = Folder + "/SkeletonStunDot.prefab";
            if (AssetDatabase.LoadAssetAtPath<GameObject>(starPath) == null)
            {
                var star = GameObject.CreatePrimitive(PrimitiveType.Cube);
                star.name = "SkeletonStunDot";
                Object.DestroyImmediate(star.GetComponent<Collider>());
                var material = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { enableInstancing = true };
                material.SetColor("_BaseColor", new Color(1f, 0.8f, 0.02f));
                AssetDatabase.CreateAsset(material, BakeFolder + "/StunDot.mat");
                star.GetComponent<Renderer>().sharedMaterial = material;
                PrefabUtility.SaveAsPrefabAsset(star, starPath);
                Object.DestroyImmediate(star);
            }
            if (catalog.StunEffectPrefab == null) catalog.StunEffectPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(starPath);
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
        }

        public static void Bake(DotsEnemyCatalog catalog)
        {
            if (catalog.BakingRigPrefab == null) throw new InvalidOperationException("Assign a skeleton baking rig.");
            var shader = Shader.Find("WaveByWave/EnemyVertexAnimation");
            if (shader == null) throw new InvalidOperationException("EnemyVertexAnimation shader is missing.");
            var clips = Enumerable.Range(0, 5).Select(i => catalog.Clip((EnemyAnimationState)i)).ToArray();
            if (clips.Any(c => c == null)) throw new InvalidOperationException("Assign all five animation clips.");
            var layouts = new EnemyAnimationFrames[5];
            var rows = 0;
            for (var i = 0; i < clips.Length; i++)
            {
                layouts[i] = new EnemyAnimationFrames { FirstRow = rows,
                    Count = Mathf.Max(2, Mathf.CeilToInt(clips[i].length * catalog.BakeFramesPerSecond) + 1), Duration = clips[i].length };
                rows += layouts[i].Count;
            }
            // Sources stay editable. Only derived meshes, textures and materials are replaced.
            var output = new List<EnemyBakedPart>();
            var preview = EditorSceneManager.NewPreviewScene();
            try
            {
                for (var sourceIndex = 0; sourceIndex < catalog.PartSources.Count; sourceIndex++)
                {
                    var source = catalog.PartSources[sourceIndex];
                    if (source.Prefab == null) continue;
                    EditorUtility.DisplayProgressBar("Baking DOTS skeleton animation", source.Prefab.name,
                        sourceIndex / (float)catalog.PartSources.Count);
                    var rig = Object.Instantiate(catalog.BakingRigPrefab);
                    UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(rig, preview);
                    rig.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                    rig.transform.localScale = Vector3.one;
                    foreach (var animator in rig.GetComponentsInChildren<Animator>(true)) Object.DestroyImmediate(animator);
                    foreach (var animation in rig.GetComponentsInChildren<Animation>(true)) Object.DestroyImmediate(animation);
                    var bones = new Dictionary<string, Transform>();
                    foreach (var node in rig.GetComponentsInChildren<Transform>(true)) bones.TryAdd(node.name, node);
                    GameObject partRoot = rig;
                    if (source.Category != EnemyBakedPartCategory.Body)
                    {
                        var attachment = new GameObject(source.Prefab.name + " Attachment").transform;
                        attachment.SetParent(!string.IsNullOrEmpty(source.AttachBone) && bones.TryGetValue(source.AttachBone, out var bone)
                            ? bone : rig.transform, false);
                        attachment.localPosition = source.LocalPosition;
                        attachment.localRotation = Quaternion.Euler(source.LocalEulerAngles);
                        attachment.localScale = source.LocalScale;
                        // Preserve the source prefab's own transform beneath its configurable socket.
                        partRoot = Object.Instantiate(source.Prefab, attachment, false);
                        foreach (var renderer in partRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                        {
                            renderer.bones = renderer.bones.Select(b => b != null && bones.TryGetValue(b.name, out var mapped) ? mapped : b).ToArray();
                            if (renderer.rootBone != null && bones.TryGetValue(renderer.rootBone.name, out var root)) renderer.rootBone = root;
                            renderer.updateWhenOffscreen = true;
                        }
                    }
                    var curves = clips.Select(c => new ClipSampler(c, bones)).ToArray();
                    foreach (var renderer in partRoot.GetComponentsInChildren<Renderer>(true))
                    {
                        if (!renderer.gameObject.activeInHierarchy || !renderer.enabled || renderer is ParticleSystemRenderer) continue;
                        var mesh = renderer is SkinnedMeshRenderer skinned ? skinned.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
                        if (mesh == null || mesh.vertexCount == 0) continue;
                        if (mesh.vertexCount > 16384) throw new InvalidOperationException($"Mesh {mesh.name} exceeds the VAT texture width.");
                        var scratch = new Mesh();
                        var baked = Object.Instantiate(mesh);
                        var width = Mathf.NextPowerOfTwo(mesh.vertexCount);
                        var positions = new Color[width * rows];
                        var normals = new Color[width * rows];
                        var bounds = new Bounds();
                        var first = true;
                        for (var clipIndex = 0; clipIndex < clips.Length; clipIndex++)
                        {
                            var layout = layouts[clipIndex];
                            for (var frame = 0; frame < layout.Count; frame++)
                            {
                                curves[clipIndex].Sample(frame * layout.Duration / (layout.Count - 1));
                                var sourceMesh = mesh;
                                if (renderer is SkinnedMeshRenderer skin) { skin.BakeMesh(scratch, false); sourceMesh = scratch; }
                                var vertices = sourceMesh.vertices;
                                var ns = sourceMesh.normals;
                                var matrix = rig.transform.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
                                var normalMatrix = matrix.inverse.transpose;
                                for (var v = 0; v < vertices.Length; v++)
                                {
                                    var p = matrix.MultiplyPoint3x4(vertices[v]);
                                    var n = normalMatrix.MultiplyVector(ns.Length > v ? ns[v] : Vector3.up).normalized;
                                    if (first) { bounds = new Bounds(p, Vector3.zero); first = false; } else bounds.Encapsulate(p);
                                    var pixel = (layout.FirstRow + frame) * width + v;
                                    positions[pixel] = new Color(p.x, p.y, p.z, 1);
                                    normals[pixel] = new Color(n.x, n.y, n.z, 1);
                                }
                                if (clipIndex == 0 && frame == 0)
                                {
                                    var vertices0 = new Vector3[vertices.Length];
                                    var normals0 = new Vector3[vertices.Length];
                                    for (var v = 0; v < vertices.Length; v++)
                                    {
                                        vertices0[v] = matrix.MultiplyPoint3x4(vertices[v]);
                                        normals0[v] = normalMatrix.MultiplyVector(ns.Length > v ? ns[v] : Vector3.up).normalized;
                                    }
                                    baked.vertices = vertices0; baked.normals = normals0;
                                }
                            }
                        }
                        var lookup = new List<Vector2>(mesh.vertexCount);
                        for (var v = 0; v < mesh.vertexCount; v++) lookup.Add(new Vector2(v, 0));
                        baked.SetUVs(3, lookup);
                        bounds.Expand(0.1f);
                        baked.bounds = bounds;
                        baked.name = $"Enemy_{sourceIndex}_{renderer.name}";
                        var path = $"{BakeFolder}/{baked.name}.asset";
                        // Keep GUIDs stable when rebaking so inspector references are retained.
                        var old = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                        if (old == null) AssetDatabase.CreateAsset(baked, path);
                        else { EditorUtility.CopySerialized(baked, old); Object.DestroyImmediate(baked); baked = old; }
                        var positionTexture = SaveTexture(path, "Positions", width, rows, positions);
                        var normalTexture = SaveTexture(path, "Normals", width, rows, normals);
                        var materials = new Material[renderer.sharedMaterials.Length];
                        for (var m = 0; m < materials.Length; m++)
                        {
                            var matName = $"Material_{m}";
                            var material = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Material>().FirstOrDefault(x => x.name == matName);
                            if (material == null) { material = new Material(shader) { name = matName }; AssetDatabase.AddObjectToAsset(material, path); }
                            material.shader = shader; material.enableInstancing = true;
                            var original = renderer.sharedMaterials[m];
                            if (original != null)
                            {
                                material.SetTexture("_BaseMap", original.HasProperty("_BaseMap") ? original.GetTexture("_BaseMap") : original.mainTexture);
                                material.SetColor("_BaseColor", original.HasProperty("_BaseColor") ? original.GetColor("_BaseColor") : original.color);
                            }
                            material.SetTexture("_PositionFrames", positionTexture); material.SetTexture("_NormalFrames", normalTexture);
                            EditorUtility.SetDirty(material);
                            materials[m] = material;
                        }
                        EditorUtility.SetDirty(baked);
                        output.Add(new EnemyBakedPart { Name = baked.name, Mesh = baked, Positions = positionTexture,
                            Normals = normalTexture, Materials = materials, Clips = layouts, Category = source.Category,
                            Pair = source.Pair, SourceIndex = sourceIndex, BodySlot = renderer.name });
                        Object.DestroyImmediate(scratch);
                    }
                    Object.DestroyImmediate(rig);
                }
                if (output.Count == 0) throw new InvalidOperationException("No enemy meshes were baked.");
                catalog.BakedParts = output;
                catalog.BakeSourceHash = SourceHash(catalog);
                catalog.CombinedVariants.Clear();
                // Combined variants are explicit derived assets, never assembled during gameplay.
                if (catalog.UseCombinedVariants) EnemyCombinedVariantBaker.Bake(catalog);
                EditorUtility.SetDirty(catalog);
                AssetDatabase.SaveAssets();
                Debug.Log($"[Enemies] Baked {output.Count} mesh parts, {rows} animation rows. Runtime uses NFE ghosts and GPU vertex animation.");
            }
            finally { EditorUtility.ClearProgressBar(); EditorSceneManager.ClosePreviewScene(preview); }
        }
        private static Texture2D SaveTexture(string path, string name, int width, int height, Color[] pixels)
        {
            var texture = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Texture2D>().FirstOrDefault(t => t.name == name);
            if (texture == null)
            {
                texture = new Texture2D(width, height, TextureFormat.RGBAHalf, false, true) { name = name };
                AssetDatabase.AddObjectToAsset(texture, path);
            }
            else texture.Reinitialize(width, height, TextureFormat.RGBAHalf, false);
            texture.filterMode = FilterMode.Point; texture.wrapMode = TextureWrapMode.Clamp;
            texture.SetPixels(pixels); texture.Apply(false, false);
            EditorUtility.SetDirty(texture);
            return texture;
        }
        internal sealed class ClipSampler
        {
            private sealed class BoneCurves
            {
                public Transform Bone;
                public Vector3 Position, Scale, Euler;
                public Quaternion Rotation;
                public readonly Dictionary<string, AnimationCurve> Curves = new();
            }
            private readonly List<BoneCurves> _bones = new();
            public ClipSampler(AnimationClip clip, Dictionary<string, Transform> bones)
            {
                var map = new Dictionary<Transform, BoneCurves>();
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    if (binding.type != typeof(Transform)) continue;
                    var name = binding.path.Substring(binding.path.LastIndexOf('/') + 1);
                    if (!bones.TryGetValue(name, out var bone)) continue;
                    if (!map.TryGetValue(bone, out var curves))
                    {
                        curves = new BoneCurves { Bone = bone, Position = bone.localPosition, Rotation = bone.localRotation,
                            Scale = bone.localScale, Euler = bone.localEulerAngles };
                        map.Add(bone, curves); _bones.Add(curves);
                    }
                    curves.Curves[binding.propertyName] = AnimationUtility.GetEditorCurve(clip, binding);
                }
                if (_bones.Count < 12) throw new InvalidOperationException($"Clip '{clip.name}' does not contain matching generic skeleton curves.");
            }
            public void Sample(float time)
            {
                foreach (var b in _bones)
                {
                    float Value(string key, float fallback) => b.Curves.TryGetValue(key, out var curve) ? curve.Evaluate(time) : fallback;
                    b.Bone.localPosition = new Vector3(Value("m_LocalPosition.x", b.Position.x),
                        Value("m_LocalPosition.y", b.Position.y), Value("m_LocalPosition.z", b.Position.z));
                    b.Bone.localScale = new Vector3(Value("m_LocalScale.x", b.Scale.x),
                        Value("m_LocalScale.y", b.Scale.y), Value("m_LocalScale.z", b.Scale.z));
                    b.Bone.localRotation = new Quaternion(Value("m_LocalRotation.x", b.Rotation.x),
                        Value("m_LocalRotation.y", b.Rotation.y), Value("m_LocalRotation.z", b.Rotation.z),
                        Value("m_LocalRotation.w", b.Rotation.w)).normalized;
                    if (b.Curves.ContainsKey("localEulerAnglesRaw.x"))
                        b.Bone.localEulerAngles = new Vector3(Value("localEulerAnglesRaw.x", b.Euler.x),
                            Value("localEulerAnglesRaw.y", b.Euler.y), Value("localEulerAnglesRaw.z", b.Euler.z));
                }
            }
        }
    }

    [CustomEditor(typeof(DotsEnemyCatalog))]
    public sealed class DotsEnemyCatalogEditor : UnityEditor.Editor
    {
        private string _combinedHash;
        private bool _dirty = true;
        private void MarkDirty() { _dirty = true; Repaint(); }
        private void OnEnable() => EditorApplication.projectChanged += MarkDirty;
        private void OnDisable() => EditorApplication.projectChanged -= MarkDirty;
        public override void OnInspectorGUI()
        {
            if (DrawDefaultInspector()) _dirty = true;
            if (!((DotsEnemyCatalog)target).CanSpawnType(EnemyCombatType.Random))
                EditorGUILayout.HelpBox("All skeleton types are disabled. No new skeletons will spawn until a type is enabled.", MessageType.Warning);
            EditorGUILayout.HelpBox("Source prefab and attachment changes are baked before Play Mode. You can also bake manually below. Runtime uses ECS + GPU animation; no Animator or NavMesh.", MessageType.Info);
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                if (GUILayout.Button("Bake prefab meshes and animation textures"))
                    EnemyContentSetup.Bake((DotsEnemyCatalog)target);
                if (GUILayout.Button("Bake combined skeleton variants"))
                    EnemyCombinedVariantBaker.Bake((DotsEnemyCatalog)target);
            }
            var catalog = (DotsEnemyCatalog)target;
            EditorGUILayout.HelpBox("Performance diagnostics: переключатели действуют на существующих скелетов во время игры, без повторного спавна. Отключайте по одному и возвращайте перед следующей проверкой. На клиенте без сервера переключатели серверного движения не влияют на симуляцию.", MessageType.Info);
            if (catalog.EnableTargetSlots)
                EditorGUILayout.HelpBox("Target Slots: каждому скелету назначена постоянная точка в спирали вокруг игрока. Режим не строит сетку и не ищет соседей, поэтому сам по себе не гарантирует столкновения. Для дешёвого A/B оставьте три Crowd-переключателя выключенными и меняйте только Enable Target Slots.", MessageType.Info);
            if (catalog.EnableCrowdCollisions)
                EditorGUILayout.HelpBox("Crowd Collisions теперь является мягким режимом личного пространства: постоянный локальный индекс не перестраивает всю толпу, а плавно отфильтрованное давление считается только для реально двигавшихся скелетов. Жёсткой остановки и проверки пересечения шага нет. Crowd Separation Radius задаёт дистанцию давления даже при выключенном Crowd Separation.", MessageType.Info);
            if (!catalog.EnableCrowdCollisions)
                EditorGUILayout.HelpBox("Crowd Collisions выключен: мягкое личное пространство не рассчитывается, поэтому скелеты могут свободно пересекаться.", MessageType.Warning);
            if (!catalog.EnableSurfaceContinuityChecks)
                EditorGUILayout.HelpBox("Без Surface Continuity Checks физическая ветка может срезать путь через воду. Запечённая карта палубы сохраняет свои проверки связности. Target Slots не заменяет проверку поверхности.", MessageType.Warning);
            if (catalog.EnableCrowdSeparation && catalog.CrowdSeparationRadius <= 0)
                EditorGUILayout.HelpBox("Мягкое расхождение уже отключено нулевым Crowd Separation Radius. Для текущего каталога отдельно сравните Crowd Avoidance и Crowd Collisions.", MessageType.Info);
            if (catalog.UseCombinedVariants && catalog.CombinedVariants.Count == 0)
                EditorGUILayout.HelpBox("Combined variants have not been baked. Runtime will use the original modular skeletons until you bake them.", MessageType.Warning);
            else if (catalog.UseCombinedVariants)
            {
                if (_dirty) { _combinedHash = EnemyCombinedVariantBaker.SourceHash(catalog); _dirty = false; }
                if (_combinedHash != catalog.CombinedSourceHash)
                    EditorGUILayout.HelpBox("Combined variants are out of date. Bake again after changing appearance or variant count.", MessageType.Warning);
            }
        }
    }
}
