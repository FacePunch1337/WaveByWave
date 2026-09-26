using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using WaveByWave.Items;
using WaveByWave.Player;
using Object = UnityEngine.Object;

namespace WaveByWave.Editor.Items
{
    public static class ItemHeldProfileChecks
    {
        private const string Request = "Temp/ItemHeldProfile.request";
        [InitializeOnLoadMethod]
        private static void Install() => EditorApplication.update += Requested;
        private static void Requested()
        {
            if (!File.Exists(Request) || EditorApplication.isPlayingOrWillChangePlaymode ||
                EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            File.Delete(Request);
            try { Run(); File.WriteAllText("Temp/ItemHeldProfile.result", "PASS: inherited held/secondary/IK poses, live changes, chains, cycle rejection/fallback, independent item data, unlink restoration, prefab grip transforms, visible clickable enemy admin rows."); }
            catch (Exception e) { File.WriteAllText("Temp/ItemHeldProfile.result", "FAIL: " + e); Debug.LogException(e); }
        }

        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
        private static void Edit(Object target, Action<SerializedObject> change)
        {
            using var data = new SerializedObject(target);
            change(data); data.ApplyModifiedPropertiesWithoutUndo();
        }

        [MenuItem("Tools/Wave by Wave/Checks/Item Held IK profiles and enemy admin rows")]
        public static void Run()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Run outside Play Mode.");
            var source = ScriptableObject.CreateInstance<ItemDefinition>();
            var variant = ScriptableObject.CreateInstance<ItemDefinition>();
            var leaf = ScriptableObject.CreateInstance<ItemDefinition>();
            var sourceModel = new GameObject("Base model");
            var variantModel = new GameObject("Variant model");
            var rig = new GameObject("Held pose check");
            try
            {
                source.name = "Base"; variant.name = "Variant"; leaf.name = "Chained variant";
                sourceModel.transform.SetPositionAndRotation(new Vector3(1,2,3), Quaternion.Euler(10,20,30));
                sourceModel.transform.localScale = Vector3.one * 2;
                variantModel.transform.SetPositionAndRotation(new Vector3(-2,1,3), Quaternion.Euler(5,-30,12));
                variantModel.transform.localScale = Vector3.one * .7f;
                Edit(source, so =>
                {
                    so.FindProperty("id").stringValue = "musket_profile_test";
                    so.FindProperty("equipmentKind").intValue = (int)ItemEquipmentKind.Musket;
                    so.FindProperty("worldVisualPrefab").objectReferenceValue = sourceModel;
                    so.FindProperty("overrideHeldPose").boolValue = true;
                    so.FindProperty("heldPosition").vector3Value = new Vector3(.2f,-.4f,.8f);
                    so.FindProperty("heldEulerAngles").vector3Value = new Vector3(20,40,60);
                    so.FindProperty("heldScale").floatValue = 1.8f;
                    so.FindProperty("overrideSecondaryHeldPose").boolValue = true;
                    so.FindProperty("secondaryHeldPosition").vector3Value = new Vector3(.1f,.2f,.3f);
                    so.FindProperty("secondaryHeldEulerAngles").vector3Value = new Vector3(1,2,3);
                    so.FindProperty("overrideHandGripPoints").boolValue = true;
                    var right = so.FindProperty("rightHandGrip");
                    right.FindPropertyRelative("enabled").boolValue = true;
                    right.FindPropertyRelative("localPosition").vector3Value = new Vector3(.1f,.3f,-.2f);
                    right.FindPropertyRelative("localEulerAngles").vector3Value = new Vector3(0,90,15);
                });
                Edit(variant, so =>
                {
                    so.FindProperty("id").stringValue = "sword_variant_test";
                    so.FindProperty("displayName").stringValue = "Own name";
                    so.FindProperty("equipmentKind").intValue = (int)ItemEquipmentKind.Sword;
                    so.FindProperty("rarity").intValue = (int)ItemRarity.Legendary;
                    so.FindProperty("potency").floatValue = 123;
                    so.FindProperty("worldVisualPrefab").objectReferenceValue = variantModel;
                    so.FindProperty("overrideHeldPose").boolValue = true;
                    so.FindProperty("heldPosition").vector3Value = Vector3.one * 3;
                });
                var original = EditorJsonUtility.ToJson(variant);
                Require(variant.TrySetHeldAndIkProfile(source) && leaf.TrySetHeldAndIkProfile(variant), "Valid profile assignment rejected.");
                Require(leaf.HeldAndIkSource == source && leaf.HeldPosition == source.HeldPosition &&
                    leaf.HeldEulerAngles == source.HeldEulerAngles && leaf.HeldScale == source.HeldScale &&
                    leaf.SecondaryHeldPosition == source.SecondaryHeldPosition && leaf.SecondaryHeldRotation == source.SecondaryHeldRotation,
                    "Held/secondary settings did not propagate through a profile chain.");
                Require(variant.EquipmentKind == ItemEquipmentKind.Sword && variant.Rarity == ItemRarity.Legendary &&
                    variant.Potency == 123 && variant.DisplayName == "Own name" && variant.WorldVisualPrefab == variantModel,
                    "Profile overwrote individual item data.");
                Require(leaf.OverridesHandGripPoints && leaf.TryGetHandGrip(ItemGripHand.Right,out var inherited) &&
                    inherited.LocalPosition == new Vector3(.1f,.3f,-.2f) && !leaf.TryGetHandGrip(ItemGripHand.Left,out _),
                    "IK pose or disabled-hand state did not propagate.");
                Edit(source, so => so.FindProperty("heldPosition").vector3Value = new Vector3(4,5,6));
                Require(variant.HeldPosition == new Vector3(4,5,6) && leaf.HeldPosition == variant.HeldPosition,
                    "Changing the base did not update linked items immediately.");
                Edit(source, so =>
                {
                    so.FindProperty("overrideHeldPose").boolValue = false;
                    so.FindProperty("overrideSecondaryHeldPose").boolValue = false;
                });
                Require(variant.HeldPosition == source.HeldPosition && variant.SecondaryHeldPosition == source.SecondaryHeldPosition &&
                    variant.HeldScale == 1 && variant.HeldEulerAngles == Vector3.zero,
                    "Automatic poses used the variant's equipment kind instead of the base profile.");
                CheckGripTransforms(variant, source, sourceModel, variantModel, rig);
                Require(!source.TrySetHeldAndIkProfile(leaf) && !variant.TrySetHeldAndIkProfile(variant), "Profile cycle accepted.");
                // Malformed serialized data must still be safe in a build, even bypassing the inspector.
                Edit(source, so => so.FindProperty("heldAndIkProfile").objectReferenceValue = leaf);
                Require(!leaf.TryResolveHeldAndIkSource(out var fallback) && fallback == leaf &&
                    variant.HeldPosition == Vector3.one * 3, "A serialized cycle did not fall back to local settings.");
                source.TrySetHeldAndIkProfile(null);
                variant.TrySetHeldAndIkProfile(null);
                Require(EditorJsonUtility.ToJson(variant) == original, "Unlinking did not restore all original item values.");
                CheckEnemyAdminRow(rig.transform);
            }
            finally
            {
                Object.DestroyImmediate(rig); Object.DestroyImmediate(sourceModel); Object.DestroyImmediate(variantModel);
                Object.DestroyImmediate(source); Object.DestroyImmediate(variant); Object.DestroyImmediate(leaf);
            }
            Debug.Log("[Items] Held/IK inheritance and visible enemy admin rows: PASS");
        }

        private static void CheckGripTransforms(ItemDefinition variant, ItemDefinition source,
            GameObject sourceModel, GameObject variantModel, GameObject rig)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var view = rig.AddComponent<HeldItemView>();
            var poseRoot = new GameObject("Pose").transform; poseRoot.SetParent(rig.transform,false);
            poseRoot.SetPositionAndRotation(new Vector3(2,3,5),Quaternion.Euler(25,70,0));
            typeof(HeldItemView).GetField("_definition",flags).SetValue(view,variant);
            typeof(HeldItemView).GetField("_item",flags).SetValue(view,variantModel.transform);
            typeof(HeldItemView).GetField("_itemPose",flags).SetValue(view,poseRoot);
            var get = typeof(HeldItemView).GetMethod("TryGetGripPose",flags);
            object[] args = { ItemGripHand.Right, default(Pose) };
            Require((bool)get.Invoke(view,args), "Inherited definition grip was not usable by HeldItemView.");
            var actual = (Pose)args[1];
            var variantRoot = Matrix4x4.TRS(variantModel.transform.localPosition,variantModel.transform.localRotation,variantModel.transform.localScale);
            var expected = poseRoot.localToWorldMatrix * variantRoot * Matrix4x4.TRS(new Vector3(.1f,.3f,-.2f),Quaternion.Euler(0,90,15),Vector3.one);
            Require(Vector3.Distance(actual.position,expected.GetColumn(3)) < .0001f && Quaternion.Angle(actual.rotation,expected.rotation) < .02f,
                "Inherited IK ignored the variant model transform.");
            var marker = new GameObject("Right grip").AddComponent<ItemHandGripPoint>();
            marker.transform.SetParent(sourceModel.transform,false);
            marker.transform.localPosition = new Vector3(.2f,-.1f,.4f); marker.transform.localRotation = Quaternion.Euler(15,45,90);
            Edit(source, so => so.FindProperty("overrideHandGripPoints").boolValue = false);
            typeof(HeldItemView).GetMethod("RefreshGripPoints",flags).Invoke(view,null);
            Require((bool)get.Invoke(view,args), "Base prefab's authored grip did not propagate.");
            actual = (Pose)args[1];
            expected = poseRoot.localToWorldMatrix * variantRoot * Matrix4x4.TRS(marker.transform.localPosition,marker.transform.localRotation,Vector3.one);
            Require(Vector3.Distance(actual.position,expected.GetColumn(3)) < .0001f && Quaternion.Angle(actual.rotation,expected.rotation) < .02f,
                "Inherited prefab marker applied the base root scale/rotation twice.");
        }

        private static void CheckEnemyAdminRow(Transform parent)
        {
            var clicked = false;
            UnityAction click = () => clicked = true;
            var create = typeof(EquipmentAdminPanel).GetMethod("EnemyRow",BindingFlags.Static | BindingFlags.NonPublic);
            foreach (var title in new[] { "Тролль • создать 1", "Акула • создать 10" })
            {
                var label = (Text)create.Invoke(null,new object[] { parent,title,click });
                Require(label != null && label.gameObject.activeInHierarchy && label.text == title, "Enemy admin row exists but is invisible.");
                var button = label.GetComponentInParent<Button>();
                Require(button != null && button.interactable, "Enemy admin button cannot be clicked.");
                clicked = false; button.onClick.Invoke(); Require(clicked,"Enemy admin callback was lost.");
            }
        }
    }
}
