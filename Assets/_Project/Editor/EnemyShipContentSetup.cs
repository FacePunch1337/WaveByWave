using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using WaveByWave.Enemies;
using Object = UnityEngine.Object;

namespace WaveByWave.Editor
{
    public static class EnemyShipContentSetup
    {
        public const string DefinitionPath = "Assets/_Project/Resources/EnemyShipDefinition.asset";
        public const string ViewPath = "Assets/_Project/Prefabs/Enemies/EnemyShip.prefab";
        public const string SpawnPointPath = "Assets/_Project/Prefabs/Enemies/EnemyShipSpawnPoint.prefab";
        private const string PlayerShipPath = "Assets/_Project/Prefabs/Ship.prefab";

        [MenuItem("Tools/Wave by Wave/Enemies/Create default DOTS enemy ship content")]
        public static void CreateContent()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Stop Play Mode before creating enemy ship content.");
            Directory.CreateDirectory("Assets/_Project/Prefabs/Enemies");
            Directory.CreateDirectory("Assets/_Project/Resources");
            AssetDatabase.Refresh();

            var view = AssetDatabase.LoadAssetAtPath<GameObject>(ViewPath);
            if (!IsClean(view))
            {
                AssetDatabase.DeleteAsset(ViewPath);
                view = null;
            }
            if (view == null) view = CreateView();
            if (AssetDatabase.LoadAssetAtPath<GameObject>(SpawnPointPath) == null)
            {
                var point = new GameObject("EnemyShipSpawnPoint", typeof(EnemyShipSpawnPoint));
                PrefabUtility.SaveAsPrefabAsset(point, SpawnPointPath);
                Object.DestroyImmediate(point);
            }

            var definition = AssetDatabase.LoadAssetAtPath<EnemyShipDefinition>(DefinitionPath);
            if (definition == null)
            {
                definition = ScriptableObject.CreateInstance<EnemyShipDefinition>();
                AssetDatabase.CreateAsset(definition, DefinitionPath);
            }
            definition.ViewPrefab = view;
            definition.ProjectilePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/_Project/Prefabs/Effects/CannonProjectile.prefab");
            definition.MuzzleEffectPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/_Project/Prefabs/Effects/CannonMuzzle.prefab");
            definition.ImpactEffectPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/_Project/Prefabs/Effects/CannonImpact.prefab");
            definition.WaterImpactPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/Stylized Water 3/Prefabs/Particles/BigSplash.prefab");
            definition.DeathEffectPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/_Project/Prefabs/Effects/DeathDust.prefab");
            definition.WaterProfile = AssetDatabase.LoadAssetAtPath<WaveByWave.Generation.OceanGenerationSettings>(
                "Assets/_Project/Resources/OceanGeneration.asset")?.WaterProfile;
            EditorUtility.SetDirty(definition);
            AssetDatabase.SaveAssets();
            Debug.Log("[Enemy ships] Default DOTS ship prefab, spawn point and definition are ready.", definition);
        }

        private static GameObject CreateView()
        {
            var root = new GameObject("EnemyShip", typeof(Rigidbody), typeof(BoxCollider),
                typeof(EnemySurfaceAnchor), typeof(EnemyShipView));
            var body = root.GetComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
            body.mass = 100f;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            var hull = root.GetComponent<BoxCollider>();
            hull.center = new Vector3(0f, 1.2f, 0f);
            hull.size = new Vector3(6.4f, 3f, 15f);

            var source = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerShipPath);
            if (source != null)
            {
                var appearance = (GameObject)PrefabUtility.InstantiatePrefab(source);
                PrefabUtility.UnpackPrefabInstance(appearance, PrefabUnpackMode.Completely,
                    InteractionMode.AutomatedAction);
                appearance.name = "Appearance";
                appearance.transform.SetParent(root.transform, false);
                appearance.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
                appearance.transform.localScale = Vector3.one;
                // Remove dependants before their RequireComponent dependencies. A single
                // forward pass leaves NetworkObject/Rigidbody behind on the copied player ship.
                for (var pass = 0; pass < 4; pass++)
                {
                    var behaviours = appearance.GetComponentsInChildren<MonoBehaviour>(true);
                    if (behaviours.Length == 0) break;
                    for (var i = behaviours.Length - 1; i >= 0; i--)
                        if (behaviours[i] != null) Object.DestroyImmediate(behaviours[i]);
                }
                foreach (var node in appearance.GetComponentsInChildren<Transform>(true))
                    GameObjectUtility.RemoveMonoBehavioursWithMissingScript(node.gameObject);
                foreach (var collider in appearance.GetComponentsInChildren<Collider>(true))
                    Object.DestroyImmediate(collider);
                foreach (var rigidbody in appearance.GetComponentsInChildren<Rigidbody>(true))
                    Object.DestroyImmediate(rigidbody);
            }
            else CreatePlaceholder(root.transform);

            var hardpoints = new List<EnemyShipHardpoint>();
            for (var i = 0; i < 3; i++)
            {
                var z = Mathf.Lerp(-4.5f, 4.5f, i / 2f);
                hardpoints.Add(CreateHardpoint(root.transform, EnemyShipSide.Port,
                    new Vector3(-3.2f, 1.8f, z)));
                hardpoints.Add(CreateHardpoint(root.transform, EnemyShipSide.Starboard,
                    new Vector3(3.2f, 1.8f, z)));
            }
            var crew = new[]
            {
                new Vector3(-1.5f, 2.75f, -4f), new Vector3(1.5f, 2.75f, -4f),
                new Vector3(-1.5f, 2.75f, -1.3f), new Vector3(1.5f, 2.75f, -1.3f),
                new Vector3(-1.5f, 2.75f, 1.4f), new Vector3(1.5f, 2.75f, 1.4f),
                new Vector3(-1.2f, 2.75f, 4.1f), new Vector3(1.2f, 2.75f, 4.1f)
            };
            var crewSlots = new List<EnemyShipCrewSlot>();
            foreach (var position in crew)
            {
                var slot = new GameObject("Crew Slot", typeof(EnemyShipCrewSlot));
                slot.transform.SetParent(root.transform, false);
                slot.transform.localPosition = position;
                crewSlots.Add(slot.GetComponent<EnemyShipCrewSlot>());
            }
            root.GetComponent<EnemyShipView>().ConfigurePrefabReferences(
                body,
                new Collider[] { hull },
                root.GetComponentsInChildren<Renderer>(true),
                hardpoints.ToArray(),
                crewSlots.ToArray());
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, ViewPath);
            Object.DestroyImmediate(root);
            return prefab;
        }

        private static bool IsClean(GameObject view)
        {
            if (view == null || view.GetComponent<EnemyShipView>() == null) return false;
            if (view.GetComponentsInChildren<EnemyShipHardpoint>(true).Length < 2 ||
                view.GetComponentsInChildren<EnemyShipCrewSlot>(true).Length < 1) return false;
            var appearance = view.transform.Find("Appearance");
            return appearance == null ||
                   appearance.GetComponentsInChildren<MonoBehaviour>(true).Length == 0 &&
                   appearance.GetComponentsInChildren<Rigidbody>(true).Length == 0 &&
                   appearance.GetComponentsInChildren<Collider>(true).Length == 0;
        }

        private static EnemyShipHardpoint CreateHardpoint(Transform parent, EnemyShipSide side, Vector3 position)
        {
            var marker = new GameObject($"{side} Cannon", typeof(EnemyShipHardpoint));
            marker.transform.SetParent(parent, false);
            marker.transform.localPosition = position;
            marker.transform.localRotation = Quaternion.LookRotation(side == EnemyShipSide.Port ?
                Vector3.left : Vector3.right, Vector3.up);
            var hardpoint = marker.GetComponent<EnemyShipHardpoint>();
            hardpoint.Side = side;
            return hardpoint;
        }

        private static void CreatePlaceholder(Transform parent)
        {
            var hull = GameObject.CreatePrimitive(PrimitiveType.Cube);
            hull.name = "Placeholder Hull";
            Object.DestroyImmediate(hull.GetComponent<Collider>());
            hull.transform.SetParent(parent, false);
            hull.transform.localPosition = new Vector3(0, 0.5f, 0);
            hull.transform.localScale = new Vector3(5.5f, 2f, 14f);
            var mast = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            mast.name = "Placeholder Mast";
            Object.DestroyImmediate(mast.GetComponent<Collider>());
            mast.transform.SetParent(parent, false);
            mast.transform.localPosition = new Vector3(0, 5f, 0);
            mast.transform.localScale = new Vector3(0.25f, 4f, 0.25f);
        }
    }
}
