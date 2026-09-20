using System;
using System.Collections.Generic;
using System.IO;
using Netcode.Transports.Facepunch;
using StylizedWater3;
using StylizedWater3.UnderwaterRendering;
using Unity.Netcode;
using Unity.Netcode.Components;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using WaveByWave.Core;
using WaveByWave.Items;
using WaveByWave.Networking;
using WaveByWave.Player;
using WaveByWave.Ships;
using WaveByWave.Simulation;
using WaveByWave.UI;
using Object = UnityEngine.Object;

namespace WaveByWave.Editor
{
    public static class WaveByWaveProjectGenerator
    {
        private const string Root = "Assets/_Project";
        private const string Generated = Root + "/Generated";
        private const string Scenes = Root + "/Scenes";
        private const string Prefabs = Root + "/Prefabs";
        private const string Data = Root + "/Data";
        private const string Materials = Generated + "/Materials";
        private const string Animations = Generated + "/Animations";
        private const string PortScenePath = Scenes + "/Port.unity";
        private const string OceanScenePath = Scenes + "/Ocean.unity";

        private static Font _font;

        [MenuItem("Tools/Wave by Wave/Regenerate Vertical Slice")]
        public static void Generate()
        {
            EnsureFolders();
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            try
            {
                var palette = CreateMaterials();
                var catalog = CreateItemCatalog();
                var controller = CreateAnimatorController();
                var worldItem = CreateWorldItemPrefab(AssetDatabase.LoadAssetAtPath<Material>(Materials + "/LootSurface.mat")
                    ?? palette.Gold, catalog);
                var player = CreatePlayerPrefab(catalog, controller, worldItem, palette.Player);
                var ship = CreateShipPrefab(palette, AssetDatabase.LoadAssetAtPath<WaveProfile>(
                    "Assets/Stylized Water 3/Profiles/Ocean Wave Profile.asset"));
                var networkPrefabs = CreateNetworkPrefabList(player, worldItem);

                ConfigureWaterRenderer();
                CreatePortScene(player, networkPrefabs, palette);
                CreateOceanScene(ship, palette);
                ConfigureBuildSettings();

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                EditorSceneManager.OpenScene(PortScenePath, OpenSceneMode.Single);
                Debug.Log("[Wave by Wave] Vertical slice generated successfully.");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }

        private static void EnsureFolders()
        {
            EnsureFolder(Root);
            EnsureFolder(Generated);
            EnsureFolder(Scenes);
            EnsureFolder(Prefabs);
            EnsureFolder(Prefabs + "/Items");
            EnsureFolder(Prefabs + "/Items/Visuals");
            EnsureFolder(Data);
            EnsureFolder(Materials);
            EnsureFolder(Animations);
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
                return;

            var parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            var name = Path.GetFileName(path);
            if (!string.IsNullOrEmpty(parent))
                EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, name);
        }

        private readonly struct MaterialPalette
        {
            public readonly Material Sand;
            public readonly Material Wood;
            public readonly Material DarkWood;
            public readonly Material Player;
            public readonly Material Gold;
            public readonly Material Sail;

            public MaterialPalette(Material sand, Material wood, Material darkWood, Material player, Material gold, Material sail)
            {
                Sand = sand;
                Wood = wood;
                DarkWood = darkWood;
                Player = player;
                Gold = gold;
                Sail = sail;
            }
        }

        private static MaterialPalette CreateMaterials()
        {
            return new MaterialPalette(
                CreateMaterial("Sand", new Color(0.72f, 0.56f, 0.31f)),
                CreateMaterial("Wood", new Color(0.34f, 0.16f, 0.06f)),
                CreateMaterial("DarkWood", new Color(0.11f, 0.045f, 0.018f)),
                CreateMaterial("Player", new Color(0.78f, 0.2f, 0.12f)),
                CreateMaterial("Gold", new Color(1f, 0.56f, 0.04f), true),
                CreateMaterial("Sail", new Color(0.82f, 0.74f, 0.57f)));
        }

        private static Material CreateMaterial(string name, Color color, bool emission = false)
        {
            var path = $"{Materials}/{name}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name };
                AssetDatabase.CreateAsset(material, path);
            }

            material.color = color;
            if (emission)
            {
                material.EnableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", color * 1.6f);
            }
            EditorUtility.SetDirty(material);
            return material;
        }

        private static ItemCatalog CreateItemCatalog()
        {
            var definitions = new List<ItemDefinition>
            {
                CreateItem("cutlass", "Сабля", "Основная атака: удар. Особое действие: парирование.", ItemCategory.Weapon),
                CreateItem("musket", "Мушкет", "Основная атака: выстрел. Особое действие: прицеливание.", ItemCategory.Weapon),
                CreateItem("hook", "Крюк", "Вытягивает плавающие предметы к кораблю.", ItemCategory.Tool),
                CreateItem("bucket", "Ведро", "Вычерпывает воду из трюма.", ItemCategory.Tool),
                CreateItem("shovel", "Лопата", "Выкапывает отмеченные клады.", ItemCategory.Tool)
            };
            // Preserve authored loot variants and supply settings when rebuilding
            // prototype scenes instead of replacing the catalog with five tools.
            foreach (var assetGuid in AssetDatabase.FindAssets("t:ItemDefinition", new[] { Data }))
            {
                var definition = AssetDatabase.LoadAssetAtPath<ItemDefinition>(AssetDatabase.GUIDToAssetPath(assetGuid));
                if (definition != null && !definitions.Contains(definition)) definitions.Add(definition);
            }

            var path = Data + "/ItemCatalog.asset";
            var catalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>(path);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<ItemCatalog>();
                AssetDatabase.CreateAsset(catalog, path);
            }

            var serialized = new SerializedObject(catalog);
            var items = serialized.FindProperty("items");
            items.arraySize = definitions.Count;
            for (var i = 0; i < definitions.Count; i++)
                items.GetArrayElementAtIndex(i).objectReferenceValue = definitions[i];
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return catalog;
        }

        private static ItemDefinition CreateItem(string id, string displayName, string description, ItemCategory category)
        {
            var path = $"{Data}/Item_{id}.asset";
            var item = AssetDatabase.LoadAssetAtPath<ItemDefinition>(path);
            if (item == null)
            {
                item = ScriptableObject.CreateInstance<ItemDefinition>();
                AssetDatabase.CreateAsset(item, path);
            }

            var serialized = new SerializedObject(item);
            serialized.FindProperty("id").stringValue = id;
            serialized.FindProperty("displayName").stringValue = displayName;
            serialized.FindProperty("description").stringValue = description;
            serialized.FindProperty("category").enumValueIndex = (int)category;
            serialized.FindProperty("rarity").enumValueIndex = (int)ItemRarity.Common;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return item;
        }

        private static AnimatorController CreateAnimatorController()
        {
            var idle = CreateClip("Capsule_Idle", new AnimationCurve(
                new Keyframe(0f, 1f), new Keyframe(0.6f, 1.025f), new Keyframe(1.2f, 1f)), "m_LocalScale.y");
            idle.wrapMode = WrapMode.Loop;
            var run = CreateClip("Capsule_Run", new AnimationCurve(
                new Keyframe(0f, 1f), new Keyframe(0.16f, 1.08f), new Keyframe(0.32f, 1f)), "m_LocalPosition.y");
            run.wrapMode = WrapMode.Loop;
            var action = CreateClip("Capsule_Action", new AnimationCurve(
                new Keyframe(0f, 0f), new Keyframe(0.12f, -18f), new Keyframe(0.3f, 0f)), "localEulerAnglesRaw.x");

            var path = Animations + "/Player.controller";
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            if (controller != null)
                return controller;

            controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
            controller.AddParameter("Grounded", AnimatorControllerParameterType.Bool);
            controller.AddParameter("VerticalSpeed", AnimatorControllerParameterType.Float);
            controller.AddParameter("Action", AnimatorControllerParameterType.Trigger);

            var stateMachine = controller.layers[0].stateMachine;
            var idleState = stateMachine.AddState("Idle");
            idleState.motion = idle;
            stateMachine.defaultState = idleState;
            var runState = stateMachine.AddState("Run");
            runState.motion = run;
            var actionState = stateMachine.AddState("Action");
            actionState.motion = action;

            var toRun = idleState.AddTransition(runState);
            toRun.hasExitTime = false;
            toRun.duration = 0.12f;
            toRun.AddCondition(AnimatorConditionMode.Greater, 0.1f, "Speed");
            var toIdle = runState.AddTransition(idleState);
            toIdle.hasExitTime = false;
            toIdle.duration = 0.12f;
            toIdle.AddCondition(AnimatorConditionMode.Less, 0.1f, "Speed");
            var anyAction = stateMachine.AddAnyStateTransition(actionState);
            anyAction.hasExitTime = false;
            anyAction.duration = 0.04f;
            anyAction.AddCondition(AnimatorConditionMode.If, 0f, "Action");
            var actionExit = actionState.AddTransition(idleState);
            actionExit.hasExitTime = true;
            actionExit.exitTime = 0.95f;
            actionExit.duration = 0.08f;
            return controller;
        }

        private static AnimationClip CreateClip(string name, AnimationCurve curve, string property)
        {
            var path = $"{Animations}/{name}.anim";
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (clip == null)
            {
                clip = new AnimationClip { name = name };
                clip.SetCurve(string.Empty, typeof(Transform), property, curve);
                AssetDatabase.CreateAsset(clip, path);
            }
            return clip;
        }

        private static WorldItem CreateWorldItemPrefab(Material material, ItemCatalog catalog)
        {
            var root = GameObject.CreatePrimitive(PrimitiveType.Cube);
            root.name = "Cannonball";
            root.transform.localScale = Vector3.one * 0.45f;
            root.GetComponent<MeshRenderer>().sharedMaterial = material;
            root.GetComponent<Collider>().isTrigger = true;
            root.AddComponent<NetworkObject>().SynchronizeTransform = false;
            var item = root.AddComponent<WorldItem>();
            SetObjectReference(item, "catalog", catalog);
            SetObjectReference(item, "rarityEffectPrefab",
                AssetDatabase.LoadAssetAtPath<GameObject>(Prefabs + "/Effects/LootRarity.prefab"));

            var path = Prefabs + "/Items/Cannonball.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return prefab.GetComponent<WorldItem>();
        }

        private static GameObject CreatePlayerPrefab(ItemCatalog catalog, AnimatorController animatorController,
            WorldItem worldItem, Material material)
        {
            var root = new GameObject("Player");
            var networkObject = root.AddComponent<NetworkObject>();
            networkObject.SyncOwnerTransformWhenParented = false;
            networkObject.AllowOwnerToParent = false;
            var bodyCollider = root.AddComponent<CapsuleCollider>();
            bodyCollider.center = new Vector3(0f, 0.8f, 0f);
            bodyCollider.height = 1.7f;
            bodyCollider.radius = 0.35f;
            var body = root.AddComponent<Rigidbody>();
            body.mass = 75f;
            body.useGravity = false;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            body.constraints = RigidbodyConstraints.FreezeRotation;
            var playerNetworkTransform = root.AddComponent<OwnerNetworkTransform>();
            playerNetworkTransform.PositionInterpolationType = NetworkTransform.InterpolationTypes.Lerp;
            playerNetworkTransform.RotationInterpolationType = NetworkTransform.InterpolationTypes.Lerp;
            playerNetworkTransform.TickSyncChildren = true;
            playerNetworkTransform.UseUnreliableDeltas = true;
            playerNetworkTransform.UseQuaternionSynchronization = true;
            playerNetworkTransform.SwitchTransformSpaceWhenParented = false;
            var networkBody = root.AddComponent<NetworkRigidbody>();
            networkBody.UseRigidBodyForMotion = false;

            var visual = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            visual.name = "Visual";
            visual.transform.SetParent(root.transform, false);
            visual.transform.localPosition = Vector3.up;
            Object.DestroyImmediate(visual.GetComponent<Collider>());
            visual.GetComponent<MeshRenderer>().sharedMaterial = material;
            var animator = visual.AddComponent<Animator>();
            animator.runtimeAnimatorController = animatorController;

            var cameraTarget = new GameObject("Camera Holder").transform;
            cameraTarget.SetParent(root.transform, false);
            cameraTarget.localPosition = new Vector3(0f, 1.65f, 0f);
            cameraTarget.gameObject.tag = "MainCamera";
            var ownerView = cameraTarget.gameObject.AddComponent<Camera>();
            ownerView.fieldOfView = 75f;
            ownerView.nearClipPlane = 0.03f;
            var localBodyLayer = LayerMask.NameToLayer(NetworkPlayerController.LocalBodyLayerName);
            if (localBodyLayer >= 0)
                ownerView.cullingMask &= ~(1 << localBodyLayer);
            cameraTarget.gameObject.AddComponent<AudioListener>();
            cameraTarget.gameObject.AddComponent<UniversalAdditionalCameraData>();
            var firstPersonCamera = cameraTarget.gameObject.AddComponent<FirstPersonCamera>();
            SetObjectReference(firstPersonCamera, "eyeTarget", cameraTarget);
            SetObjectReference(firstPersonCamera, "characterBody", root.transform);
            cameraTarget.gameObject.SetActive(false);

            var animationSync = root.AddComponent<PlayerAnimationSync>();
            SetObjectReference(animationSync, "animator", animator);
            var inventory = root.AddComponent<PlayerInventory>();
            SetObjectReference(inventory, "catalog", catalog);
            SetObjectReference(inventory, "worldItemPrefab", worldItem);
            var player = root.AddComponent<NetworkPlayerController>();
            SetObjectReference(player, "body", body);
            SetObjectReference(player, "bodyCollider", bodyCollider);
            SetObjectReference(player, "animationSync", animationSync);
            SetObjectReference(player, "inventory", inventory);
            SetObjectReference(player, "cameraTarget", cameraTarget);
            SetObjectReference(player, "ownerCamera", firstPersonCamera);
            SetObjectReference(player, "firstPersonHiddenRoot", visual.transform);
            var equipment = root.AddComponent<PlayerEquipment>();
            SetObjectReference(equipment, "motions", AssetDatabase.LoadAssetAtPath<EquipmentMotionSet>(
                Root + "/Resources/EquipmentMotions.asset"));
            SetObjectReference(equipment, "firstPersonHandsPrefab", AssetDatabase.LoadAssetAtPath<GameObject>(
                Prefabs + "/FirstPersonHands.prefab"));
            SetObjectReference(equipment, "metalMaterial", AssetDatabase.LoadAssetAtPath<Material>(
                Root + "/Generated/Materials/CannonIron.mat"));
            SetObjectReference(equipment, "effectMaterial", AssetDatabase.LoadAssetAtPath<Material>(
                Root + "/Generated/Materials/LootGlow.mat"));
            SetObjectReference(equipment, "handMaterial", AssetDatabase.LoadAssetAtPath<Material>(
                Root + "/Generated/Materials/LootSurface.mat"));
            SetObjectReference(equipment, "sleeveMaterial", material);
            SetObjectReference(equipment, "waterWaveProfile", AssetDatabase.LoadAssetAtPath<WaveProfile>(
                "Assets/Stylized Water 3/Profiles/Ocean Wave Profile.asset"));
            SetObjectReference(equipment, "waterSplashPrefab", AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/Stylized Water 3/Prefabs/Particles/BigSplash.prefab"));

            var path = Prefabs + "/Player.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return prefab;
        }

        private static GameObject CreateShipPrefab(MaterialPalette palette, WaveProfile profile)
        {
            var root = new GameObject("Ship");
            root.AddComponent<NetworkObject>();
            var body = root.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.Discrete;
            var collider = root.AddComponent<BoxCollider>();
            collider.center = new Vector3(0f, 0.9f, 0f);
            collider.size = new Vector3(8f, 1.8f, 14f);
            root.AddComponent<MovingPlatform>();
            root.AddComponent<PlatformNetworkTransform>();
            var wind = root.AddComponent<NetworkWindController>();

            var alignment = root.AddComponent<AlignToWater>();
            alignment.heightInterface.method = HeightQuerySystem.Interface.Method.CPU;
            alignment.heightInterface.autoFind = true;
            alignment.heightInterface.waveProfile = profile;
            alignment.heightInterface.waterLevelSource = HeightQuerySystem.Interface.WaterLevelSource.WaterObject;
            alignment.surfaceSize = new Vector2(7f, 13f);
            alignment.heightOffset = 0.25f;
            alignment.rollAmount = 0.08f;
            alignment.smoothing = 1.25f;
            alignment.heightValue = AlignToWater.HeightValue.Average;

            var ship = root.AddComponent<NetworkShipController>();
            SetObjectReference(ship, "body", body);
            SetObjectReference(ship, "waterAlignment", alignment);
            SetObjectReference(ship, "collisionHull", collider);
            SetObjectReference(ship, "wind", wind);

            var shipSpawnPoints = CreateSpawnPoints(new Vector3(0f, 2.1f, 1.5f), root.transform);
            SetArray(ship, "playerSpawnPoints", shipSpawnPoints);

            var visualRoot = new GameObject("Wave Visual").transform;
            visualRoot.SetParent(root.transform, false);

            CreateCube("Hull", visualRoot, new Vector3(0f, 0.15f, 0f), new Vector3(7.6f, 1.6f, 13.5f), palette.DarkWood, false);
            CreateCube("Deck", visualRoot, new Vector3(0f, 1.05f, 0f), new Vector3(7.4f, 0.25f, 13f), palette.Wood, false);
            var mastPivot = new GameObject("Mast Pivot").transform;
            mastPivot.SetParent(visualRoot, false);
            CreateCube("Mast", mastPivot, new Vector3(0f, 5f, 0.5f), new Vector3(0.35f, 8f, 0.35f), palette.DarkWood, false);
            var sailVisual = CreateCube("Sail", mastPivot, new Vector3(0f, 5.6f, 0.45f), new Vector3(5.2f, 4.5f, 0.08f), palette.Sail, false);

            var helmRoot = new GameObject("Helm Interaction");
            helmRoot.transform.SetParent(root.transform, false);
            helmRoot.transform.localPosition = new Vector3(0f, 2f, -3.8f);
            var helmCollider = helmRoot.AddComponent<BoxCollider>();
            helmCollider.size = new Vector3(1.5f, 2.2f, 1.5f);
            helmCollider.isTrigger = true;
            var station = new GameObject("Station").transform;
            station.SetParent(helmRoot.transform, false);
            station.localPosition = new Vector3(0f, -1f, -1.2f);
            station.localRotation = Quaternion.Euler(0f, 180f, 0f);
            var helm = helmRoot.AddComponent<ShipHelm>();
            SetObjectReference(helm, "ship", ship);
            SetObjectReference(helm, "station", station);
            SetObjectReference(ship, "helm", helm);

            var capstanPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(Prefabs + "/ShipAnchorCapstan.prefab");
            if (capstanPrefab == null)
                throw new InvalidOperationException("ShipAnchorCapstan.prefab is required to generate the ship.");
            var anchorPoint = (GameObject)PrefabUtility.InstantiatePrefab(capstanPrefab);
            anchorPoint.transform.SetParent(root.transform, false);
            anchorPoint.transform.localPosition = new Vector3(-2f, 1.175f, -2.2f);
            var anchor = anchorPoint.GetComponent<ShipAnchor>();
            SetObjectReference(anchor, "ship", ship);
            SetObjectReference(ship, "anchor", anchor);

            var sailPoint = CreateShipInteractionPoint("Sail Control", root.transform,
                new Vector3(2f, 2f, -3.8f), palette.Gold);
            var sailStation = new GameObject("Station").transform;
            sailStation.SetParent(sailPoint.transform, false);
            sailStation.localPosition = new Vector3(0f, -1f, -1.2f);
            sailStation.localRotation = Quaternion.Euler(0f, 180f, 0f);
            var sailControl = sailPoint.AddComponent<ShipSailControl>();
            SetObjectReference(sailControl, "ship", ship);
            SetObjectReference(sailControl, "station", sailStation);
            SetObjectReference(sailControl, "indicatorRenderer", sailPoint.GetComponent<Renderer>());
            SetArray(sailControl, "sailVisuals", new[] { sailVisual.transform });
            SetObjectReference(sailControl, "windReference", sailVisual.transform);
            SetFloat(sailControl, "furledScaleY", 0.36f);
            SetFloat(sailControl, "deployedScaleY", 4.5f);
            SetObjectReference(ship, "sailControl", sailControl);

            var mastPoint = CreateShipInteractionPoint("Mast Control", root.transform,
                new Vector3(4f, 2f, -3.8f), palette.Gold);
            var mastStation = new GameObject("Station").transform;
            mastStation.SetParent(mastPoint.transform, false);
            mastStation.localPosition = new Vector3(0f, -1f, -1.2f);
            mastStation.localRotation = Quaternion.Euler(0f, 180f, 0f);
            var mastControl = mastPoint.AddComponent<ShipMastControl>();
            SetObjectReference(mastControl, "ship", ship);
            SetObjectReference(mastControl, "station", mastStation);
            SetObjectReference(mastControl, "indicatorRenderer", mastPoint.GetComponent<Renderer>());
            SetObjectReference(mastControl, "mastPivot", mastPivot);
            SetObjectReference(ship, "mastControl", mastControl);

            var flagRoot = new GameObject("Wind Flag").transform;
            flagRoot.SetParent(root.transform, false);
            flagRoot.localPosition = new Vector3(0f, 3.5f, -2f);
            CreateCube("Pole", flagRoot, new Vector3(0f, 0.75f, 0f),
                new Vector3(0.08f, 1.5f, 0.08f), palette.DarkWood, false);
            CreateCube("Flag", flagRoot, new Vector3(0f, 1.2f, 0.55f),
                new Vector3(0.08f, 0.5f, 1.1f), palette.Sail, false);
            var directionFlag = flagRoot.gameObject.AddComponent<WindDirectionFlag>();
            SetObjectReference(directionFlag, "wind", wind);
            SetObjectReference(directionFlag, "directionPivot", flagRoot);

            var wheel = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            wheel.name = "Wheel";
            wheel.transform.SetParent(visualRoot, false);
            wheel.transform.localPosition = new Vector3(0f, 2.2f, -3.8f);
            wheel.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            wheel.transform.localScale = new Vector3(1.1f, 0.12f, 1.1f);
            wheel.GetComponent<MeshRenderer>().sharedMaterial = palette.Gold;
            Object.DestroyImmediate(wheel.GetComponent<Collider>());
            SetObjectReference(helm, "wheelVisual", wheel.transform);
            SetVector3(helm, "localRotationAxis", Vector3.up);

            var cannonPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(Prefabs + "/ShipCannon.prefab");
            var chestPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(Prefabs + "/ShipTreasureChest.prefab");
            if (cannonPrefab == null || chestPrefab == null)
                throw new System.InvalidOperationException("Ship cannon and treasure chest prefabs are missing.");
            var battery = root.AddComponent<ShipCannonBattery>();
            var guns = new ShipCannon[2];
            for (var i = 0; i < 2; i++)
            {
                var gun = (GameObject)PrefabUtility.InstantiatePrefab(cannonPrefab);
                gun.transform.SetParent(root.transform, false);
                gun.transform.localPosition = new Vector3(i == 0 ? -3f : 3f, 1.175f, 0f);
                gun.transform.localRotation = Quaternion.Euler(0f, i == 0 ? -90f : 90f, 0f);
                guns[i] = gun.GetComponent<ShipCannon>();
            }
            var chest = (GameObject)PrefabUtility.InstantiatePrefab(chestPrefab);
            chest.transform.SetParent(root.transform, false);
            chest.transform.localPosition = new Vector3(1.5f, 1.175f, -2f);
            var serializedBattery = new SerializedObject(battery);
            var cannonArray = serializedBattery.FindProperty("cannons");
            cannonArray.arraySize = guns.Length;
            for (var i = 0; i < guns.Length; i++) cannonArray.GetArrayElementAtIndex(i).objectReferenceValue = guns[i];
            serializedBattery.ApplyModifiedPropertiesWithoutUndo();
            SetObjectReference(battery, "treasureChest", chest.GetComponent<ShipTreasureChest>());
            SetObjectReference(battery, "ballMaterial", AssetDatabase.LoadAssetAtPath<Material>(Materials + "/CannonIron.mat"));
            SetObjectReference(battery, "effectMaterial", AssetDatabase.LoadAssetAtPath<Material>(Materials + "/LootGlow.mat"));
            SetObjectReference(battery, "waterSplashPrefab", AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/Stylized Water 3/Prefabs/Particles/BigSplash.prefab"));

            var path = Prefabs + "/Ship.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return prefab;
        }

        private static NetworkPrefabsList CreateNetworkPrefabList(GameObject player, WorldItem item)
        {
            var path = Data + "/NetworkPrefabs.asset";
            var list = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(path);
            if (list == null)
            {
                list = ScriptableObject.CreateInstance<NetworkPrefabsList>();
                AssetDatabase.CreateAsset(list, path);
            }

            while (list.PrefabList.Count > 0)
                list.Remove(list.PrefabList[0]);
            list.Add(new NetworkPrefab { Override = NetworkPrefabOverride.None, Prefab = player });
            list.Add(new NetworkPrefab { Override = NetworkPrefabOverride.None, Prefab = item.gameObject });
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { Prefabs + "/Items" }))
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
                if (prefab != null && prefab.GetComponent<WorldItem>() != null)
                    list.Add(new NetworkPrefab { Override = NetworkPrefabOverride.None, Prefab = prefab });
            }
            EditorUtility.SetDirty(list);
            return list;
        }

        private static void CreatePortScene(GameObject playerPrefab, NetworkPrefabsList prefabs, MaterialPalette palette)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            CreateLighting(new Color(0.42f, 0.61f, 0.76f));
            CreatePreviewCamera(new Vector3(14f, 13f, -18f), new Vector3(15f, -35f, 0f));

            CreateCube("Port Ground", null, new Vector3(0f, -0.5f, 0f), new Vector3(42f, 1f, 34f), palette.Sand);
            CreateCube("Pier", null, new Vector3(0f, 0.1f, 15f), new Vector3(8f, 0.7f, 18f), palette.Wood);
            for (var i = -3; i <= 3; i += 2)
            {
                CreateCube($"Pier Post L{i}", null, new Vector3(-3.4f, -1.1f, 15f + i * 2f), new Vector3(0.45f, 3f, 0.45f), palette.DarkWood);
                CreateCube($"Pier Post R{i}", null, new Vector3(3.4f, -1.1f, 15f + i * 2f), new Vector3(0.45f, 3f, 0.45f), palette.DarkWood);
            }
            CreateCube("Meta Shop", null, new Vector3(-11f, 2f, 2f), new Vector3(7f, 4f, 6f), palette.DarkWood);
            CreateCube("Cartographer", null, new Vector3(11f, 1.5f, 3f), new Vector3(6f, 3f, 5f), palette.Wood);
            CreateNetworkPhysicsBox(palette.Wood);

            var spawnPoints = CreateSpawnPoints(new Vector3(0f, 0.05f, 3f));
            var director = new GameObject("Spawn Director").AddComponent<NetworkSpawnDirector>();
            SetArray(director, "spawnPoints", spawnPoints);
            CreateSessionMenu();
            CreateEventSystem();
            CreateLifetimeRoot(playerPrefab, prefabs);

            EditorSceneManager.SaveScene(scene, PortScenePath);
        }

        private static void CreateNetworkPhysicsBox(Material material)
        {
            var root = new GameObject("PhysicsBox");
            root.transform.position = new Vector3(9.4f, 0.74f, -2.05f);

            var collider = root.AddComponent<BoxCollider>();
            var body = root.AddComponent<Rigidbody>();
            body.mass = 5f;
            body.linearDamping = 0.35f;
            body.angularDamping = 0.8f;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            root.AddComponent<NetworkObject>();
            var networkTransform = root.AddComponent<NetworkTransform>();
            networkTransform.PositionInterpolationType = NetworkTransform.InterpolationTypes.Lerp;
            networkTransform.RotationInterpolationType = NetworkTransform.InterpolationTypes.Lerp;
            networkTransform.UseUnreliableDeltas = true;
            networkTransform.UseQuaternionSynchronization = true;
            networkTransform.SyncScaleX = false;
            networkTransform.SyncScaleY = false;
            networkTransform.SyncScaleZ = false;

            var networkBody = root.AddComponent<NetworkRigidbody>();
            networkBody.UseRigidBodyForMotion = true;
            var networkPhysics = root.AddComponent<NetworkPhysicsObject>();

            var visual = CreateCube(
                "Visual",
                root.transform,
                Vector3.zero,
                Vector3.one,
                material,
                false);
            var predictionCollider = visual.AddComponent<BoxCollider>();
            predictionCollider.enabled = false;
            SetObjectReference(networkPhysics, "body", body);
            SetObjectReference(networkPhysics, "interactionCollider", collider);
            SetObjectReference(networkPhysics, "visualRoot", visual.transform);
            SetObjectReference(networkPhysics, "predictionCollider", predictionCollider);
        }

        private static void CreateOceanScene(GameObject shipPrefab, MaterialPalette palette)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            CreateLighting(new Color(0.34f, 0.55f, 0.72f));
            CreatePreviewCamera(new Vector3(18f, 15f, -20f), new Vector3(20f, -38f, 0f));

            var oceanPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/Stylized Water 3/Prefabs/StylizedWater3_Ocean.prefab");
            var ocean = (GameObject)PrefabUtility.InstantiatePrefab(oceanPrefab, scene);
            ocean.name = "Infinite Ocean (Stylized Water 3)";
            ocean.transform.position = Vector3.zero;

            var waterMaterial = AssetDatabase.LoadAssetAtPath<Material>(
                "Assets/Stylized Water 3/Materials/StylizedWater3_Ocean.mat");
            CreateUnderwaterArea(waterMaterial);

            var ship = (GameObject)PrefabUtility.InstantiatePrefab(shipPrefab, scene);
            ship.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            new GameObject("Spawn Director").AddComponent<NetworkSpawnDirector>();
            CreateSessionMenu();
            CreateEventSystem();

            EditorSceneManager.SaveScene(scene, OceanScenePath);
        }

        private static void CreateLifetimeRoot(GameObject playerPrefab, NetworkPrefabsList prefabs)
        {
            var root = new GameObject("[Game Lifetime]");
            var manager = root.AddComponent<NetworkManager>();
            var steam = root.AddComponent<FacepunchTransport>();
            var local = root.AddComponent<UnityTransport>();
            var session = root.AddComponent<NetworkSessionCoordinator>();
            root.AddComponent<HybridSimulationBridge>();
            var scope = root.AddComponent<GameLifetimeScope>();

            manager.NetworkConfig.PlayerPrefab = playerPrefab;
            // Ship motion is authored in FixedUpdate at 50 Hz (0.02 s). Sampling it at
            // a different network rate produces uneven intervals on remote clients.
            manager.NetworkConfig.TickRate = 50;
            manager.NetworkConfig.EnableSceneManagement = true;
            manager.NetworkConfig.NetworkTransport = steam;
            manager.NetworkConfig.Prefabs.NetworkPrefabsLists.Clear();
            manager.NetworkConfig.Prefabs.NetworkPrefabsLists.Add(prefabs);

            SetUInt(steam, "steamAppId", 480);
            SetUInt(session, "steamAppId", 480);
            SetObjectReference(session, "networkManager", manager);
            SetObjectReference(session, "steamTransport", steam);
            SetObjectReference(session, "localTransport", local);
            SetObjectReference(scope, "sessionCoordinator", session);
        }

        private static Transform[] CreateSpawnPoints(Vector3 center, Transform parent = null)
        {
            var root = new GameObject("Player Spawn Points").transform;
            root.SetParent(parent, false);
            if (parent != null)
                root.localPosition = center;
            else
                root.position = center;

            var offsets = new[]
            {
                new Vector3(-1.5f, 0f, -1.5f), new Vector3(1.5f, 0f, -1.5f),
                new Vector3(-1.5f, 0f, 1.5f), new Vector3(1.5f, 0f, 1.5f)
            };
            var result = new Transform[offsets.Length];
            for (var i = 0; i < offsets.Length; i++)
            {
                result[i] = new GameObject($"Spawn {i + 1}").transform;
                result[i].SetParent(root, false);
                result[i].localPosition = offsets[i];
            }
            return result;
        }

        private static void CreateUnderwaterArea(Material waterMaterial)
        {
            var areaObject = new GameObject("Ocean Underwater Area");
            var box = areaObject.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = new Vector3(2000f, 500f, 2000f);
            box.center = new Vector3(0f, -247f, 0f);
            var area = areaObject.AddComponent<UnderwaterArea>();
            area.waterMaterial = waterMaterial;
            area.boxCollider = box;
            area.waterLevelSource = UnderwaterArea.WaterLevelSource.FixedValue;
            area.waterLevel = 0f;
            area.underwaterResources = AssetDatabase.LoadAssetAtPath<UnderwaterResources>(
                "Assets/Stylized Water 3/Runtime/Underwater/UnderwaterResources.asset");
            area.shadingSettings = new UnderwaterArea.ShadingSettings();
        }

        private static void ConfigureWaterRenderer()
        {
            var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>("Assets/Settings/PC_Renderer.asset");
            if (renderer == null)
                return;

            foreach (var existing in renderer.rendererFeatures)
            {
                if (existing is StylizedWaterRenderFeature)
                    return;
            }

            var feature = ScriptableObject.CreateInstance<StylizedWaterRenderFeature>();
            feature.name = "Stylized Water 3";
            feature.underwaterRenderingSettings.enable = true;
            feature.VerifyReferences();
            AssetDatabase.AddObjectToAsset(feature, renderer);
            renderer.rendererFeatures.Add(feature);
            feature.Create();
            EditorUtility.SetDirty(renderer);
        }

        private static void CreateLighting(Color ambient)
        {
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = ambient * 0.7f;
            var sun = new GameObject("Sun", typeof(Light));
            sun.transform.rotation = Quaternion.Euler(42f, -35f, 0f);
            var light = sun.GetComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.25f;
            light.color = new Color(1f, 0.88f, 0.68f);
            RenderSettings.sun = light;
        }

        private static void CreatePreviewCamera(Vector3 position, Vector3 euler)
        {
            var camera = new GameObject("Scene Preview Camera", typeof(Camera), typeof(UniversalAdditionalCameraData),
                typeof(AudioListener), typeof(ScenePreviewCamera));
            camera.transform.position = position;
            camera.transform.rotation = Quaternion.Euler(euler);
            camera.GetComponent<Camera>().clearFlags = CameraClearFlags.Skybox;
        }

        private static void CreateEventSystem()
        {
            // Adding the component already assigns its default UI actions in OnEnable.
            // Calling AssignDefaultActions a second time breaks the generated action references
            // when the generator creates the second scene in the same editor frame.
            _ = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
        }

        private static void CreateSessionMenu()
        {
            var canvasObject = new GameObject("Session UI", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;
            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            var escHint = CreateText(canvasObject.transform, "Esc Hint", "ESC — меню", 22, TextAnchor.UpperLeft);
            SetRect(escHint.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(24f, -24f), new Vector2(260f, 40f), new Vector2(0f, 1f));

            var panel = new GameObject("Menu Panel", typeof(RectTransform), typeof(Image));
            panel.transform.SetParent(canvasObject.transform, false);
            var panelRect = panel.GetComponent<RectTransform>();
            SetRect(panelRect, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(680f, 650f), new Vector2(0.5f, 0.5f));
            panel.GetComponent<Image>().color = new Color(0.015f, 0.035f, 0.055f, 0.96f);

            var title = CreateText(panel.transform, "Title", "ПОРТ", 46, TextAnchor.MiddleCenter);
            SetRect(title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -52f), new Vector2(600f, 70f), new Vector2(0.5f, 1f));
            var status = CreateText(panel.transform, "Status", "Инициализация сети…", 24, TextAnchor.MiddleCenter);
            SetRect(status.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -128f), new Vector2(600f, 60f), new Vector2(0.5f, 1f));
            var hint = CreateText(panel.transform, "Hint", string.Empty, 18, TextAnchor.MiddleCenter);
            SetRect(hint.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 26f), new Vector2(610f, 70f), new Vector2(0.5f, 0f));

            var invite = CreateButton(panel.transform, "Invite Friends", "Пригласить друзей Steam", -210f);
            var start = CreateButton(panel.transform, "Start Voyage", "Начать плавание", -290f);
            var localHost = CreateButton(panel.transform, "Local Host", "Локальный Host (тест)", -370f);
            var localClient = CreateButton(panel.transform, "Local Client", "Локальный Client (тест)", -450f);
            var returnPort = CreateButton(panel.transform, "Return Port", "Вернуться в порт", -290f);
            var close = CreateButton(panel.transform, "Close", "Продолжить", -530f);

            var presenter = canvasObject.AddComponent<SessionMenuPresenter>();
            SetObjectReference(presenter, "panel", panel);
            SetObjectReference(presenter, "title", title);
            SetObjectReference(presenter, "status", status);
            SetObjectReference(presenter, "hint", hint);
            SetObjectReference(presenter, "inviteButton", invite);
            SetObjectReference(presenter, "startButton", start);
            SetObjectReference(presenter, "localHostButton", localHost);
            SetObjectReference(presenter, "localClientButton", localClient);
            SetObjectReference(presenter, "returnToPortButton", returnPort);
            SetObjectReference(presenter, "closeButton", close);
        }

        private static Text CreateText(Transform parent, string name, string value, int size, TextAnchor alignment)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var text = go.GetComponent<Text>();
            text.font = _font;
            text.text = value;
            text.fontSize = size;
            text.alignment = alignment;
            text.color = new Color(0.95f, 0.9f, 0.78f);
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            return text;
        }

        private static Button CreateButton(Transform parent, string name, string label, float y)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            SetRect(go.GetComponent<RectTransform>(), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, y), new Vector2(480f, 60f), new Vector2(0.5f, 1f));
            var image = go.GetComponent<Image>();
            image.color = new Color(0.56f, 0.27f, 0.07f, 1f);
            var button = go.GetComponent<Button>();
            button.targetGraphic = image;
            var text = CreateText(go.transform, "Label", label, 24, TextAnchor.MiddleCenter);
            text.rectTransform.anchorMin = Vector2.zero;
            text.rectTransform.anchorMax = Vector2.one;
            text.rectTransform.offsetMin = Vector2.zero;
            text.rectTransform.offsetMax = Vector2.zero;
            return button;
        }

        private static GameObject CreateCube(string name, Transform parent, Vector3 position, Vector3 scale,
            Material material, bool collider = true)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            if (parent != null)
            {
                go.transform.SetParent(parent, false);
                go.transform.localPosition = position;
            }
            else
            {
                go.transform.position = position;
            }
            go.transform.localScale = scale;
            go.GetComponent<MeshRenderer>().sharedMaterial = material;
            if (!collider)
                Object.DestroyImmediate(go.GetComponent<Collider>());
            return go;
        }

        private static GameObject CreateShipInteractionPoint(string name, Transform parent, Vector3 position,
            Material material)
        {
            var point = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            point.name = name;
            point.transform.SetParent(parent, false);
            point.transform.localPosition = position;
            point.transform.localScale = Vector3.one * 0.5f;
            point.GetComponent<MeshRenderer>().sharedMaterial = material;
            var collider = point.GetComponent<SphereCollider>();
            collider.isTrigger = true;
            collider.radius = 1.2f;
            return point;
        }

        private static void SetRect(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax,
            Vector2 anchoredPosition, Vector2 size, Vector2 pivot)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = size;
            rect.pivot = pivot;
        }

        private static void ConfigureBuildSettings()
        {
            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene(PortScenePath, true),
                new EditorBuildSettingsScene(OceanScenePath, true)
            };
        }

        private static void SetObjectReference(Object target, string propertyName, Object value)
        {
            var serialized = new SerializedObject(target);
            serialized.FindProperty(propertyName).objectReferenceValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetUInt(Object target, string propertyName, uint value)
        {
            var serialized = new SerializedObject(target);
            serialized.FindProperty(propertyName).longValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetFloat(Object target, string propertyName, float value)
        {
            var serialized = new SerializedObject(target);
            serialized.FindProperty(propertyName).floatValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetVector3(Object target, string propertyName, Vector3 value)
        {
            var serialized = new SerializedObject(target);
            serialized.FindProperty(propertyName).vector3Value = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetArray(Object target, string propertyName, Transform[] values)
        {
            var serialized = new SerializedObject(target);
            var property = serialized.FindProperty(propertyName);
            property.arraySize = values.Length;
            for (var i = 0; i < values.Length; i++)
                property.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
