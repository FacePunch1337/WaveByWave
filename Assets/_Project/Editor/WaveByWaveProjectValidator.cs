using System;
using System.IO;
using System.Linq;
using Netcode.Transports.Facepunch;
using StylizedWater3;
using StylizedWater3.UnderwaterRendering;
using Unity.Netcode;
using Unity.Netcode.Components;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using WaveByWave.Core;
using WaveByWave.Items;
using WaveByWave.Networking;
using WaveByWave.Player;
using WaveByWave.Ships;
using WaveByWave.Simulation;
using WaveByWave.UI;

namespace WaveByWave.Editor
{
    public static class WaveByWaveProjectValidator
    {
        private const string PortScenePath = "Assets/_Project/Scenes/Port.unity";
        private const string OceanScenePath = "Assets/_Project/Scenes/Ocean.unity";

        [MenuItem("Tools/Wave by Wave/Validate Vertical Slice")]
        public static void Validate()
        {
            ValidateAssets();
            ValidatePort();
            ValidateOcean();
            ValidateBuildSettings();
            EditorSceneManager.OpenScene(PortScenePath, OpenSceneMode.Single);
            Debug.Log("[Wave by Wave] VALIDATION PASSED: Port, Ocean, networking, inventory and server-authoritative water alignment are configured.");
        }

        public static void ValidateBatch()
        {
            try
            {
                Validate();
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }

        private static void ValidateAssets()
        {
            Require(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/Player.prefab"), "Player prefab");
            Require(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/Ship.prefab"), "Ship prefab");
            Require(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/Items/Cannonball.prefab"), "Item prefab");
            var worldItem = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/Items/Cannonball.prefab");
            if (worldItem.GetComponent<Rigidbody>() != null || worldItem.GetComponent<NetworkTransform>() != null ||
                worldItem.GetComponent<NetworkRigidbody>() != null || !worldItem.GetComponent<Collider>().isTrigger)
                throw new InvalidOperationException("Loot must use procedural placement and only a pickup trigger, without Rigidbody or NetworkTransform.");
            var itemCatalog = Require(AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/_Project/Data/ItemCatalog.asset"),
                "Item catalog");
            foreach (var definition in itemCatalog.Items)
            {
                if (definition == null || definition.WorldVisualPrefab == null) continue;
                var filters = definition.WorldVisualPrefab.GetComponentsInChildren<MeshFilter>(true);
                if (filters.Length != 1 || filters[0].sharedMesh == null || filters[0].GetComponent<MeshRenderer>() == null)
                    throw new InvalidOperationException($"Item prefab '{definition.WorldVisualPrefab.name}' must contain exactly one MeshFilter and MeshRenderer.");
            }
            Require(AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>("Assets/_Project/Data/NetworkPrefabs.asset"), "Network prefab list");
            Require(AssetDatabase.LoadAssetAtPath<WaveProfile>("Assets/Stylized Water 3/Profiles/Ocean Wave Profile.asset"), "Ocean wave profile");

            var player = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/Player.prefab");
            var playerNetworkObject = Require(player.GetComponent<NetworkObject>(), "Player NetworkObject");
            var playerNetworkTransform = Require(player.GetComponent<OwnerNetworkTransform>(), "Owner-authoritative player transform");
            var playerBody = Require(player.GetComponent<Rigidbody>(), "Player Rigidbody motor");
            Require(player.GetComponent<CapsuleCollider>(), "Player capsule collider");
            var playerNetworkBody = Require(player.GetComponent<Unity.Netcode.Components.NetworkRigidbody>(), "Player NetworkRigidbody");
            Require(player.GetComponent<NetworkPlayerController>(), "Player controller");
            if (player.GetComponent<CharacterController>() != null)
                throw new InvalidOperationException("Player must use only the Rigidbody motor, not CharacterController.");
            if (playerBody.useGravity || playerBody.interpolation != RigidbodyInterpolation.Interpolate ||
                playerBody.constraints != RigidbodyConstraints.FreezeRotation)
                throw new InvalidOperationException("Player Rigidbody movement settings are invalid.");
            if (playerNetworkBody.UseRigidBodyForMotion || playerNetworkObject.AllowOwnerToParent ||
                playerNetworkTransform.SwitchTransformSpaceWhenParented)
                throw new InvalidOperationException("Player networking must keep an unparented world-space physics root.");
            Require(player.GetComponent<PlayerAnimationSync>(), "Player animation sync");
            var ownerCamera = Require(player.GetComponentInChildren<FirstPersonCamera>(true), "Embedded first-person owner camera");
            if (ownerCamera.transform.parent != player.transform)
                throw new InvalidOperationException("The owner camera must live in a direct child Camera Holder.");
            var inventory = Require(player.GetComponent<PlayerInventory>(), "Player inventory");
            var equipment = Require(player.GetComponent<PlayerEquipment>(), "Player equipment");
            var equipmentSerialized = new SerializedObject(equipment);
            var equipmentMotions = Require(equipmentSerialized.FindProperty("motions").objectReferenceValue as EquipmentMotionSet,
                "Editable equipment motions");
            for (var action = (int)EquipmentAction.SwordSwing; action <= (int)EquipmentAction.ShovelDig; action++)
                Require(equipmentMotions.Get((EquipmentAction)action), ((EquipmentAction)action) + " animation");
            var inventorySerialized = new SerializedObject(inventory);
            var startingItems = inventorySerialized.FindProperty("startingItemIds");
            if (startingItems == null || startingItems.arraySize != 8)
                throw new InvalidOperationException("Player must start with five tools and cannonball, plank and food stacks.");

            var ship = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/Ship.prefab");
            Require(ship.GetComponent<NetworkObject>(), "Ship NetworkObject");
            var shipController = Require(ship.GetComponent<NetworkShipController>(), "Ship controller");
            Require(ship.GetComponent<MovingPlatform>(), "Ship moving-platform compensation");
            var battery = Require(ship.GetComponent<ShipCannonBattery>(), "Ship cannon battery");
            var cannons = ship.GetComponentsInChildren<ShipCannon>(true);
            if (cannons.Length != 2)
                throw new InvalidOperationException("The ship needs exactly two broadside cannons.");
            foreach (var cannon in cannons)
            {
                if (cannon.Station == cannon.transform)
                    throw new InvalidOperationException("Cannons need authored operator stations.");
            }
            Require(ship.GetComponentInChildren<ShipTreasureChest>(true), "Crew treasure chest");
            var anchor = Require(ship.GetComponentInChildren<ShipAnchor>(true), "Ship anchor capstan");
            if (anchor.HandleCount < 1)
                throw new InvalidOperationException("The anchor capstan needs authored handle stations.");
            for (var i = 0; i < anchor.HandleCount; i++)
            {
                var station = Require(anchor.GetHandleStation(i), $"Anchor handle station {i + 1}");
                if (!station.IsChildOf(anchor.Rotor))
                    throw new InvalidOperationException("Anchor stations must turn with the capstan handles.");
            }
            Require(ship.GetComponent<PlatformNetworkTransform>(), "Timestamped ship motion snapshots");
            if (ship.GetComponent<NetworkTransform>() != null)
                throw new InvalidOperationException("Ship motion must use physics snapshots without a second NetworkTransform writer.");
            var body = Require(ship.GetComponent<Rigidbody>(), "Ship Rigidbody");
            var collisionHull = Require(ship.GetComponent<BoxCollider>(), "Ship collision hull");
            if (!body.isKinematic)
                throw new InvalidOperationException("The gameplay ship root must remain kinematic and upright.");
            if (collisionHull.isTrigger)
                throw new InvalidOperationException("The ship collision hull must be solid, not a trigger.");
            if (ship.GetComponent<Unity.Netcode.Components.NetworkRigidbody>() != null)
                throw new InvalidOperationException("Kinematic AlignToWater ship must not use NetworkRigidbody authority switching.");
            if (!Physics.autoSyncTransforms)
                throw new InvalidOperationException("Physics Auto Sync Transforms must be enabled for client-side moving platforms.");

            var alignment = Require(ship.GetComponent<AlignToWater>(), "Ship root AlignToWater");
            if (alignment.transform != ship.transform)
                throw new InvalidOperationException("AlignToWater must be attached directly to the network ship root.");
            var shipSerialized = new SerializedObject(shipController);
            if (shipSerialized.FindProperty("anchor")?.objectReferenceValue != anchor)
                throw new InvalidOperationException("NetworkShipController must reference the anchor capstan.");
            if (shipSerialized.FindProperty("collisionHull")?.objectReferenceValue != collisionHull)
                throw new InvalidOperationException("NetworkShipController must reference the root collision hull.");

            var embeddedNgoAsmdef = "Packages/com.unity.netcode.gameobjects/Editor/Unity.Netcode.Editor.asmdef";
            if (!File.Exists(embeddedNgoAsmdef) || !File.ReadAllText(embeddedNgoAsmdef).Contains("Unity.Netcode.GameObjects.Editor"))
                throw new InvalidOperationException("Embedded NGO editor compatibility fix is missing.");
        }

        private static void ValidatePort()
        {
            EditorSceneManager.OpenScene(PortScenePath, OpenSceneMode.Single);
            var manager = Require(UnityEngine.Object.FindFirstObjectByType<NetworkManager>(), "Port NetworkManager");
            Require(manager.GetComponent<FacepunchTransport>(), "Facepunch transport");
            Require(manager.GetComponent<UnityTransport>(), "Local development transport");
            Require(manager.GetComponent<NetworkSessionCoordinator>(), "Session coordinator");
            Require(manager.GetComponent<GameLifetimeScope>(), "Lifetime scope");
            Require(manager.GetComponent<HybridSimulationBridge>(), "NGO/ECS bridge");
            Require(UnityEngine.Object.FindFirstObjectByType<SessionMenuPresenter>(), "Port session menu");
            Require(UnityEngine.Object.FindFirstObjectByType<NetworkSpawnDirector>(), "Port spawn director");

            var physicsBoxObject = Require(GameObject.Find("PhysicsBox"), "Port PhysicsBox");
            var physicsBox = Require(
                physicsBoxObject.GetComponent<NetworkPhysicsObject>(),
                "PhysicsBox network physics controller");
            var physicsNetworkObject = Require(
                physicsBoxObject.GetComponent<NetworkObject>(),
                "PhysicsBox NetworkObject");
            var physicsTransform = Require(
                physicsBoxObject.GetComponent<NetworkTransform>(),
                "PhysicsBox NetworkTransform");
            var physicsBody = Require(
                physicsBoxObject.GetComponent<Rigidbody>(),
                "PhysicsBox Rigidbody");
            var physicsNetworkBody = Require(
                physicsBoxObject.GetComponent<Unity.Netcode.Components.NetworkRigidbody>(),
                "PhysicsBox NetworkRigidbody");
            var physicsCollider = Require(
                physicsBoxObject.GetComponent<BoxCollider>(),
                "PhysicsBox collider");
            var physicsNetworkSerialized = new SerializedObject(physicsNetworkObject);
            var globalObjectIdHash = physicsNetworkSerialized.FindProperty("GlobalObjectIdHash");
            if (globalObjectIdHash == null || globalObjectIdHash.uintValue == 0 || physicsBody.isKinematic ||
                physicsBody.interpolation != RigidbodyInterpolation.Interpolate ||
                physicsBody.collisionDetectionMode != CollisionDetectionMode.ContinuousDynamic ||
                !physicsTransform.Interpolate || !physicsTransform.UseUnreliableDeltas ||
                !physicsTransform.UseQuaternionSynchronization || !physicsNetworkBody.UseRigidBodyForMotion ||
                physicsCollider.isTrigger)
                throw new InvalidOperationException("PhysicsBox network physics settings are invalid.");

            var physicsSerialized = new SerializedObject(physicsBox);
            var visualRoot = physicsSerialized.FindProperty("visualRoot")?.objectReferenceValue as Transform;
            var predictionCollider = physicsSerialized.FindProperty("predictionCollider")?.objectReferenceValue as Collider;
            if (visualRoot == null || visualRoot.parent != physicsBoxObject.transform ||
                predictionCollider == null || predictionCollider.transform != visualRoot ||
                predictionCollider.enabled)
                throw new InvalidOperationException(
                    "PhysicsBox must use a disabled child prediction collider for smooth remote pushing.");

            if (manager.NetworkConfig.PlayerPrefab == null || !manager.NetworkConfig.EnableSceneManagement)
                throw new InvalidOperationException("NetworkManager player prefab or NGO scene management is not configured.");

            var physicsTickRate = Mathf.RoundToInt(1f / Time.fixedDeltaTime);
            if (manager.NetworkConfig.TickRate != physicsTickRate)
                throw new InvalidOperationException(
                    $"Network tick rate ({manager.NetworkConfig.TickRate}) must match physics ({physicsTickRate}).");
            if (SteamNetcodeBootstrap.ConfiguredSimulationTickRate != physicsTickRate)
                throw new InvalidOperationException(
                    $"DOTS NetCode simulation rate ({SteamNetcodeBootstrap.ConfiguredSimulationTickRate}) " +
                    $"must match physics ({physicsTickRate}).");
        }

        private static void ValidateOcean()
        {
            EditorSceneManager.OpenScene(OceanScenePath, OpenSceneMode.Single);
            Require(UnityEngine.Object.FindFirstObjectByType<WaterObject>(), "Stylized Water 3 ocean");
            Require(UnityEngine.Object.FindFirstObjectByType<UnderwaterArea>(), "Underwater area");
            Require(UnityEngine.Object.FindFirstObjectByType<NetworkShipController>(), "Ocean ship");
            Require(UnityEngine.Object.FindFirstObjectByType<AlignToWater>(), "Ocean ship water alignment");
            Require(UnityEngine.Object.FindFirstObjectByType<NetworkSpawnDirector>(), "Ocean spawn director");
            Require(UnityEngine.Object.FindFirstObjectByType<SessionMenuPresenter>(), "Ocean session menu");
        }

        private static void ValidateBuildSettings()
        {
            var enabled = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray();
            if (!enabled.SequenceEqual(new[] { PortScenePath, OceanScenePath }))
                throw new InvalidOperationException("Build settings must contain Port first and Ocean second.");
        }

        private static T Require<T>(T value, string label) where T : UnityEngine.Object
        {
            if (value == null)
                throw new InvalidOperationException($"Missing required object: {label}.");
            return value;
        }
    }
}
