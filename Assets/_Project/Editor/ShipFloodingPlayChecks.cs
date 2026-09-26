using System;
using System.IO;
using System.Reflection;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using WaveByWave.Generation;
using WaveByWave.Player;
using WaveByWave.Ships;
using Object = UnityEngine.Object;

namespace WaveByWave.Editor
{
    // Opt-in host integration check. Never replaces a dirty scene or an active game.
    public static class ShipFloodingPlayChecks
    {
        private const string State = "Temp/ShipFloodingPlayChecks.state";
        [Serializable] private class SavedScenes { public SavedScene[] Scenes; }
        [Serializable] private class SavedScene { public string Path; public bool Loaded, Active; }
        private static int _stage;
        private static double _deadline;
        private static NetworkManager _manager;
        private static ShipFlooding _ship;
        private static ShipCannonBattery _battery;
        private static ShipHullHealth _hullHealth;
        private static string _result;
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        [InitializeOnLoadMethod]
        private static void Install() => EditorApplication.update += Tick;
        private static void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException(message); }

        private static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            const string request = "Temp/ShipFloodingPlayChecks.request";
            if (!File.Exists(State))
            {
                if (!File.Exists(request) || EditorApplication.isPlayingOrWillChangePlaymode) return;
                File.Delete(request);
                for (var i = 0; i < SceneManager.sceneCount; i++)
                    if (SceneManager.GetSceneAt(i).isDirty || string.IsNullOrEmpty(SceneManager.GetSceneAt(i).path))
                    { File.WriteAllText("Temp/ShipFloodingPlayChecks.result", "SKIPPED: editor has an unsaved scene; existing work was preserved."); return; }
                var setup = EditorSceneManager.GetSceneManagerSetup();
                var saved = new SavedScenes { Scenes = new SavedScene[setup.Length] };
                for (var i = 0; i < setup.Length; i++) saved.Scenes[i] = new SavedScene { Path = setup[i].path, Loaded = setup[i].isLoaded, Active = setup[i].isActive };
                File.WriteAllText(State, JsonUtility.ToJson(saved));
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                EditorApplication.EnterPlaymode();
                return;
            }
            if (!EditorApplication.isPlaying)
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode) return;
                var saved = JsonUtility.FromJson<SavedScenes>(File.ReadAllText(State));
                var setup = new SceneSetup[saved.Scenes.Length];
                for (var i = 0; i < setup.Length; i++) setup[i] = new SceneSetup { path = saved.Scenes[i].Path, isLoaded = saved.Scenes[i].Loaded, isActive = saved.Scenes[i].Active };
                File.Delete(State); EditorSceneManager.RestoreSceneManagerSetup(setup); _stage = 0;
                return;
            }
            try
            {
                if (_stage == 0)
                {
                    _stage = 1;
                    StartHost(); _deadline = EditorApplication.timeSinceStartup + 0.5;
                }
                else if (_stage == 1 && EditorApplication.timeSinceStartup >= _deadline)
                {
                    TestGameplay(); _stage = 2; _deadline = EditorApplication.timeSinceStartup + 0.5;
                }
                else if (_stage == 2 && EditorApplication.timeSinceStartup >= _deadline)
                {
                    Check(_ship.TrySinkingPose(out var p, out _) && p.y < -0.0001f, "Flooded ship must move down through its kinematic mover");
                    _result = "PASS: real NGO host; cannon and boundary breaches, spaced boundary timer, water transfers, repair ring, defeat and sinking.";
                    Finish();
                }
            }
            catch (Exception e) { _result = "FAIL: " + e; Debug.LogException(e); Finish(); }
        }

        private static void StartHost()
        {
            Check(NetworkManager.Singleton == null, "Integration check requires an isolated scene");
            var managerGO = new GameObject("Flooding check host");
            _manager = managerGO.AddComponent<NetworkManager>();
            var transport = managerGO.AddComponent<UnityTransport>();
            transport.SetConnectionData("127.0.0.1", 17843, "127.0.0.1");
            _manager.NetworkConfig = new NetworkConfig { NetworkTransport = transport, EnableSceneManagement = false };
            Check(_manager.StartHost(), "Could not start test host");
            var root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/Ship.prefab"));
            root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            root.GetComponent<NetworkObject>().Spawn();
            _ship = root.GetComponent<ShipFlooding>(); _battery = root.GetComponent<ShipCannonBattery>();
            _hullHealth = root.GetComponent<ShipHullHealth>();
            // Keep amounts deterministic; network tick and KCC continue running.
            _ship.enabled = false; _battery.enabled = false;
        }

        private static void TestGameplay()
        {
            var setBattlefield = typeof(ShipCannonBattery).GetMethod("SetBattlefieldServer", Private);
            var clearBattlefield = typeof(ShipCannonBattery).GetMethod("ClearBattlefieldServer", Private);
            Check(setBattlefield != null && clearBattlefield != null,
                "Battlefield snapshot API must exist");
            setBattlefield.Invoke(_battery, new object[] { new Vector3(5f, 0f, 7f), 20f });
            setBattlefield.Invoke(_battery, new object[] { new Vector3(100f, 0f, 100f), 200f });
            Check(_battery.BattlefieldCenter == new Vector3(5f, 0f, 7f) &&
                  Mathf.Approximately(_battery.BattlefieldRadius, 20f) &&
                  _battery.IsInsideBattlefield(new Vector3(5f, 0f, 7f)) &&
                  !_battery.IsInsideBattlefield(new Vector3(30f, 0f, 7f)),
                "Battlefield must remain fixed at its first wave-start snapshot");
            clearBattlefield.Invoke(_battery, null);
            var boundary = new BattlefieldBoundaryBreachTimer();
            Check(!boundary.Step(0f, false, 8f, 10f) &&
                  !boundary.Step(1f, true, 8f, 10f) &&
                  !boundary.Step(8.9f, true, 8f, 10f) &&
                  boundary.Step(9f, true, 8f, 10f) &&
                  !boundary.Step(9.25f, true, 8f, 10f) &&
                  boundary.Step(19.25f, true, 8f, 10f) &&
                  !boundary.Step(20f, false, 8f, 10f) &&
                  !boundary.Step(21f, true, 8f, 10f),
                "Boundary must create spaced breaches after grace and reset on re-entry");
            var site = _ship.Hull.Sites[_ship.Hull.Sites.Length / 2];
            var point = _ship.Hull.transform.TransformPoint(site.Position);
            _ship.DamageBreachChancePercent = 0f;
            _hullHealth.ApplyDamageServer(25f, point);
            Check(_ship.HoleCount == 0 && _ship.Inflow == 0f && _ship.WaterLitres == 0f,
                "Zero breach chance must prevent ordinary hits from opening holes or adding water");
            _ship.DamageBreachChancePercent = 100f;
            _hullHealth.ApplyDamageServer(25f, point);
            Check(_ship.HoleCount == 1 && _ship.Inflow > 0f, "Cannon hit must open a leaking breach on the server");
            var waterPoint = _ship.WaterVolume.transform.TransformPoint(_ship.WaterVolume.LocalBounds.center);
            _ship.AddWaterServer(6f, waterPoint);
            waterPoint.y = _ship.WaterVolume.HeightAt(waterPoint, _ship.Fill);
            var bucket = _ship.ScoopWaterServer(10f, waterPoint);
            Check(Mathf.Abs(bucket - 6f) < 0.001f && _ship.WaterLitres == 0f, "Server must allow a partial bucket");
            _ship.AddWaterServer(bucket, waterPoint);
            Check(Mathf.Abs(_ship.WaterLitres - 6f) < 0.001f, "Pouring back must conserve water");

            var playerGO = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/Player.prefab"));
            playerGO.transform.position = new Vector3(40f, 10f, 40f);
            playerGO.GetComponent<NetworkObject>().SpawnAsPlayerObject(_manager.LocalClientId);
            var player = playerGO.GetComponent<NetworkPlayerController>(); var inventory = playerGO.GetComponent<PlayerInventory>();
            foreach (var behaviour in playerGO.GetComponents<MonoBehaviour>()) if (behaviour is not NetworkObject) behaviour.enabled = false;
            var equipment = playerGO.GetComponent<PlayerEquipment>();
            var scoop = typeof(PlayerEquipment).GetMethod("ScoopBucketServer", Private);
            var pour = typeof(PlayerEquipment).GetMethod("PourBucketServer", Private);
            waterPoint.y = _ship.WaterVolume.HeightAt(waterPoint, _ship.Fill);
            var scooped = (float)scoop.Invoke(equipment, new object[] { waterPoint + Vector3.up, Vector3.down, null });
            Check(Mathf.Abs(scooped - 6f) < 0.001f && _ship.WaterLitres == 0f, "Bucket aim must reach the internal water without scooping through the deck");
            var pouredAt = (Vector3)pour.Invoke(equipment, new object[] { waterPoint + Vector3.up * 7f, Vector3.down, scooped });
            Check(Mathf.Abs(_ship.WaterLitres - 6f) < 0.001f,
                $"A ballistic pour onto the ship must restore its water (impact {pouredAt}, water {waterPoint}, litres {_ship.WaterLitres})");
            _ship.ScoopWaterServer(10f, waterPoint);
            pour.Invoke(equipment, new object[] { waterPoint + Vector3.right * 8f + Vector3.up, Vector3.right, 6f });
            Check(_ship.WaterLitres == 0f, "A ballistic pour outside the ship must not refill it");
            _ship.AddWaterServer(6f, waterPoint);
            Check(inventory.ApplySelectionServer(6, 1), "Select plank on the authoritative inventory");
            typeof(NetworkPlayerController).GetField("_pendingRingOffers", Private).SetValue(player, (ulong)PlayerRingStat.Repair);
            typeof(NetworkPlayerController).GetField("_awaitingRingChoice", Private).SetValue(player, true);
            typeof(NetworkPlayerController).GetMethod("ChooseRingServerRpc", Private).Invoke(player, new object[] { (byte)0 });
            Check(player.RingLevel(PlayerRingStat.Repair) == 1 && player.RingValue(PlayerRingStat.Repair) > 0f,
                "The server must accept the new repair ring, including its packed offer index");
            var boards = inventory.GetSlot(6).Amount;
            var hole = _ship.GetHole(0);
            point = _ship.Hull.transform.TransformPoint(hole.Position);
            var normal = _ship.Hull.transform.TransformDirection(hole.Normal).normalized;
            // Approach the actual opening from outside; validate no blanket ship-HP repair remains.
            var origin = point + normal * 0.55f;
            Physics.SyncTransforms();
            for (var i = 0; i < 45 && _ship.HoleCount > 0; i++)
                Check(_ship.RepairServer(player, inventory, origin, -normal, 0.1f), "Holding a plank at the breach should repair it");
            Check(_ship.HoleCount == 0 && _ship.Inflow == 0f && inventory.GetSlot(6).Amount == boards - 1,
                "Repair must seal exactly one hole and consume exactly one plank");
            var presentation = _ship.GetComponent<ShipRepairPresentation>();
            Check(presentation != null, "Repair presentation must be created on clients");
            Check(_ship.transform.Find("Nailed plank " + hole.Id) == null,
                "Repair must not leave a visual plank on the hull");
            Check(Mathf.Abs(_ship.WaterLitres - 6f) < 0.001f, "Repair must not drain existing water");
            _hullHealth.ApplyDamageServer(25f, point);
            Check(_ship.HoleCount == 1, "A new hit after repair must create another breach");
            var waterBeforeBoundary = _ship.WaterLitres;
            _ship.DamageBreachChancePercent = 0f;
            Check(_hullHealth.OpenBoundaryBreachServer(1f) && _ship.HoleCount == 2 &&
                  _ship.Inflow > _ship.LeakLitresPerSecond &&
                  Mathf.Approximately(_ship.WaterLitres, waterBeforeBoundary),
                "Boundary damage must open a leaking hole even at zero damage breach chance, without adding water directly");
            var inflowBeforeHit = _ship.Inflow;
            _hullHealth.ApplyDamageServer(25f, point);
            Check(_ship.HoleCount == 2 && Mathf.Approximately(_ship.Inflow, inflowBeforeHit),
                "Zero breach chance must also prevent ordinary hits from worsening existing leaks");
            _ship.DamageBreachChancePercent = 100f;
            _ship.AddWaterServer(_ship.CapacityLitres, waterPoint);
            Check(_ship.IsSinking && _battery.Phase == VoyagePhase.Defeat && _battery.ReturnToPortIn > 0f, "Full water must start defeat and the shared port countdown");
            var count = _ship.HoleCount;
            _hullHealth.ApplyDamageServer(25f, point);
            Check(_ship.HoleCount == count && _ship.ScoopWaterServer(10f, waterPoint) == 0f, "Ended voyage cannot be revived by a late input");
        }

        private static void Finish()
        {
            File.WriteAllText("Temp/ShipFloodingPlayChecks.result", _result ?? "FAIL: no result");
            _stage = 3;
            if (_manager != null) _manager.Shutdown();
            EditorApplication.ExitPlaymode();
        }
    }
}
