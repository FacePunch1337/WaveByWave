using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering;
using UnityEngine.UI;
using WaveByWave.Combat;
using WaveByWave.Generation;
using WaveByWave.Items;
using WaveByWave.Core;
using WaveByWave.Networking;
using WaveByWave.Player;
using WaveByWave.UI;
using Object = UnityEngine.Object;

namespace WaveByWave.Editor
{
    public static class GameplayFeatureChecks
    {
        [MenuItem("Tools/Wave by Wave/Checks/Waves weapons rings and UI")]
        public static void Run()
        {
            var items=AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/_Project/Data/ItemCatalog.asset");
            foreach(var kind in new[]{ItemEquipmentKind.Sword,ItemEquipmentKind.Musket})
            {
                var damage=0f;
                for(var tier=0;tier<5;tier++)
                {
                    var item=items.Items.FirstOrDefault(x=>x!=null&&x.EquipmentKind==kind&&(int)x.Rarity==tier);
                    Require(item!=null,$"Missing {kind} rarity {tier}");
                    Require(item.WeaponDamage>damage,$"{kind}: rarity must increase damage");damage=item.WeaponDamage;
                }
            }
            var catalog=Resources.Load<PlayerRingCatalog>("PlayerRingCatalog");
            foreach(PlayerRingStat stat in Enum.GetValues(typeof(PlayerRingStat)))
            {Require(catalog.Find(stat).BaseBonus>0,$"Missing stat {stat}");Require(catalog.Find(stat).Icon!=null,$"Missing icon {stat}");}
            var random=new Unity.Mathematics.Random(18761);
            var owned=new HashSet<PlayerRingStat>{PlayerRingStat.Repair,PlayerRingStat.AttackSpeed,PlayerRingStat.ProjectileCount,PlayerRingStat.MaximumHealth};
            for(var roll=0;roll<200;roll++)
            {
                var packed=catalog.CreateOffers(catalog.MaximumDistinctRings,s=>owned.Contains(s)?1:0,ref random,out var count);
                Require(count==3,"Full ring slots should still offer upgrades");
                var seen=new HashSet<int>();
                for(var i=0;i<count;i++)
                {
                    var code=(byte)(packed>>(i*8));Require(owned.Contains((PlayerRingStat)(code&15)),"New ring offered when all slots full");
                    Require(seen.Add(code&15),"Duplicate ring offer");
                }
                var single=catalog.CreateOffers(catalog.MaximumDistinctRings,s=>s==PlayerRingStat.Repair?1:0,ref random,out var singleCount);
                Require(singleCount==1&&(single&15)==(int)PlayerRingStat.Repair,"One owned ring must give one valid choice");
            }
            for(var tier=0;tier<5;tier++)
            {var count=catalog.Bonus(PlayerRingStat.ProjectileCount,(ItemRarity)tier);Require(count>=1&&Mathf.Approximately(count,Mathf.Round(count)),"Projectile bonuses must be integral");}
            var timer=new BattlefieldBoundaryBreachTimer();
            Require(!timer.Step(0,true,8,10)&&!timer.Step(7,true,8,10)&&timer.Step(8,true,8,10),"Boundary grace");
            Require(!timer.Step(9,false,8,10)&&!timer.Step(10,true,8,10)&&!timer.Step(17,true,8,10)&&timer.Step(18,true,8,10),"Reentry must reset boundary damage");
            CheckFogFade();
            foreach(var key in new[]{"HUD/Inventory","HUD/Equipment","HUD/Stamina","HUD/AimAndReload","HUD/RepairProgress","HUD/PlayerHealth","HUD/Voyage",
                "World/AnchorProgress","World/HealthBar","World/ItemTooltip","World/TreasureLevel","Menus/Wardrobe","Menus/Admin","Menus/RingUpgrade","Menus/PlayerRings","Menus/Session","Menus/VoyageResult","Elements/RingRow","Elements/ItemSpawnRow"})
            {
                var prefab=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/UI/Resources/UI"+"/"+key+".prefab");Require(prefab!=null,"Missing UI "+key);
                foreach(var t in prefab.GetComponentsInChildren<Transform>(true))Require(GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject)==0,"Missing script in "+key);
                foreach(var text in prefab.GetComponentsInChildren<Text>(true))Require(text.font!=null,"Missing UI font "+key);
            }
            CheckCombinedUi();
            var admin=Resources.Load<GameObject>("UI/Menus/Admin");
            foreach(var path in new[]{"LootCount","LootRadius","EnemyCount","EnemyRadius","ShipCount","ShipRadius"})
                Require(admin.transform.Find("Items/"+path)?.GetComponent<Slider>()!=null,"Unbound admin slider "+path);
            foreach(var path in new[]{"LootCountLabel","LootRadiusLabel","EnemyCountLabel","EnemyRadiusLabel","ShipCountLabel","ShipRadiusLabel"})
                Require(admin.transform.Find("Items/"+path)?.GetComponent<Text>()!=null,"Unbound admin label "+path);
            foreach(var key in new[]{"Menus/RingUpgrade","Menus/PlayerRings"})
            {
                var view=Resources.Load<GameObject>("UI/"+key);
                Require(view.transform.Find("Panel/Scroll/Content")?.GetComponent<VerticalLayoutGroup>()!=null,"Missing vertical ring layout");
                Require(view.transform.Find("Panel/Footer")?.GetComponent<Text>()!=null,"Missing waiting/status text");
            }
            var popup=Resources.Load<DamagePopupSettings>("DamagePopupSettings");Require(popup!=null&&popup.Font!=null&&popup.Shader!=null,"Damage popup settings");
            Require(!ShaderUtil.ShaderHasError(popup.Shader),"Damage glyph shader error");
            Require(!ShaderUtil.ShaderHasError(Shader.Find("Hidden/WaveByWave/Night Battlefield Fog")),"Fog shader error");
            CheckEnemyDepth();
            DotsDamagePopups.CheckPoolAnimation();
            var admission = typeof(NetworkSessionCoordinator).GetMethod("CanApproveConnection", BindingFlags.Static | BindingFlags.NonPublic);
            bool Allows(bool sailing, string scene, ulong id) => (bool)admission.Invoke(null, new object[] { sailing, scene, id });
            Require(Allows(false, GameScenes.Port, 7), "Port must allow new players");
            Require(!Allows(true, GameScenes.Port, 7), "Departure and return transitions must reject joins");
            Require(!Allows(true, GameScenes.Ocean, 7) && !Allows(false, GameScenes.Ocean, 7), "Ocean must reject new players");
            Require(Allows(true, GameScenes.Ocean, Unity.Netcode.NetworkManager.ServerClientId), "Host startup must remain possible in Ocean");
            Debug.Log("[Gameplay checks] PASS: five weapon rarities, monotonic damage, 200 full-slot offer rounds, unique choices, icons, integer projectiles, boundary reentry, all UI prefabs/bindings, shaders, Burst popup animation/expiry/vertex layout and voyage admission policy.");
        }
        public static void CheckCombinedUi()
        {
            var root = PrefabUtility.LoadPrefabContents("Assets/_Project/Prefabs/UI/Resources/UI/GameUI.prefab");
            try
            {
                var layout = root.GetComponent<GameUiRoot>();
                Require(layout != null, "Missing GameUI runtime binding");
                Require(root.transform.Find("Templates") == null && root.transform.Find("Live UI") == null,
                    "GameUI must use its visible hierarchy directly");
                var count = 0;
                foreach (Transform group in root.transform)
                    foreach (Transform child in group)
                    {
                        count++;
                        Require(layout.FindView(group.name + "/" + child.name) == child.gameObject, "Unreachable UI view");
                        Require(PrefabUtility.IsPartOfPrefabInstance(child), "GameUI must preserve nested prefab links");
                    }
                Require(count == 20 && layout.FindView("Loading/OceanLoadingCurtain") != null, "Missing UI screen");
                var source = layout.FindView("HUD/Stamina");
                var color = new Color(.18f, .35f, .92f, 1f);
                var size = new Vector2(370, 16);
                source.transform.Find("Stamina").GetComponent<RectTransform>().sizeDelta = size;
                source.transform.Find("Stamina/Stamina fill").GetComponent<Image>().color = color;
                source.SetActive(false);
                var secondOwner = layout.FindView("HUD/Equipment");
                var view = layout.CreateView("HUD/Stamina", owner: root);
                Require(view == source && view.activeSelf, "HUD must use the authored object without cloning");
                layout.ReleaseView(view, root);
                Require(!view.activeSelf && view != null, "Releasing HUD must hide, not destroy it");
                Require(layout.CreateView("HUD/Stamina", owner: secondOwner) == source, "Respawn must reuse the same view");
                layout.ReleaseView(view, root);
                Require(view.activeSelf, "Old owner must not hide the replacement player's HUD");
                Require(view.transform.Find("Stamina").GetComponent<RectTransform>().sizeDelta == size &&
                    view.transform.Find("Stamina/Stamina fill").GetComponent<Image>().color == color,
                    "Authored size and color must survive rebinding");
                var menu = layout.CreateView("Menus/PlayerRings", owner: root);
                var button = menu.transform.Find("Panel/Close").GetComponent<Button>();
                var callbacks = 0;
                button.onClick.AddListener(() => callbacks++);
                layout.ReleaseView(menu, root);
                layout.CreateView("Menus/PlayerRings", owner: secondOwner);
                button.onClick.AddListener(() => callbacks += 10);
                button.onClick.Invoke();
                Require(callbacks == 10, "Rebinding a menu must remove callbacks from the previous player");
                var row = layout.CreateView("Elements/RingRow", root.transform);
                Require(row != layout.FindView("Elements/RingRow") && row.activeSelf, "Repeated rows need independent instances");
                Object.DestroyImmediate(row);
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    Require(GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject) == 0, "Missing script in " + t.name);
                Debug.Log("[GameUI checks] PASS: 20 nested prefabs, direct view identity, layout/color preservation, respawn, owner handover and callback cleanup.");
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }
        [MenuItem("Tools/Wave by Wave/Checks/DOTS enemy depth shader")]
        public static void CheckEnemyDepth()
        {
            var shader = Shader.Find("WaveByWave/EnemyVertexAnimation");
            Require(shader != null, "Missing DOTS enemy shader");
            var material = new Material(shader);
            var variants = new ShaderVariantCollection();
            try
            {
                var forward = material.FindPass("ForwardLit");
                var depthNormals = material.FindPass("DepthNormalsOnly");
                Require(forward >= 0 && depthNormals >= 0 && material.FindPass("DepthOnly") >= 0,
                    "DOTS enemies must support both depth and depth/normals prepasses");
                Require(shader.FindPassTagValue(0, forward, new ShaderTagId("LightMode")).name == "UniversalForwardOnly",
                    "DOTS enemy lighting must support deferred as well as forward renderers");
                Require(shader.FindPassTagValue(0, depthNormals, new ShaderTagId("LightMode")).name == "DepthNormalsOnly",
                    "URP must select animated enemy depth in a deferred prepass");
                for (var mask = 0; mask < 8; mask++)
                {
                    var keywords = new List<string>();
                    if ((mask & 1) != 0) keywords.Add("DOTS_INSTANCING_ON");
                    if ((mask & 2) != 0) keywords.Add("_GBUFFER_NORMALS_OCT");
                    if ((mask & 4) != 0) keywords.Add("_WRITE_RENDERING_LAYERS");
                    variants.Add(new ShaderVariantCollection.ShaderVariant(shader,
                        PassType.ScriptableRenderPipeline, keywords.ToArray()));
                }
                variants.WarmUp();
                Require(!ShaderUtil.ShaderHasError(shader), "Enemy depth shader: " +
                    string.Join("\n", ShaderUtil.GetShaderMessages(shader).Select(message => message.message)));
                Debug.Log("[Enemy depth checks] PASS: forward-only and depth/normals passes, DOTS variants, packed normals and rendering layers.");
            }
            finally { Object.DestroyImmediate(material); Object.DestroyImmediate(variants); }
        }
        private static void CheckFogFade()
        {
            var fade = new BattlefieldFogFade();
            fade.Step(true, 4f, 4f, 4f, 4f);
            Require(Mathf.Approximately(fade.Opacity, 1f), "Fog must finish appearing");
            fade.Step(false, 0f, 5f, 4f, 4f);
            Require(Mathf.Approximately(fade.Opacity, 1f), "Ending a wave must not abruptly hide fog");
            fade.Step(false, 0f, 7f, 4f, 4f);
            Require(Mathf.Approximately(fade.Opacity, .5f), "Fog fade-out midpoint");
            fade.Step(false, 0f, 9f, 4f, 4f);
            Require(fade.Opacity == 0f, "Finished fog must stop rendering");
            fade.Step(true, 1f, 20f, 4f, 4f);
            var partial = fade.Opacity;
            fade.Step(false, 0f, 21f, 4f, 4f);
            Require(Mathf.Approximately(fade.Opacity, partial), "A short wave must fade from current opacity");
            fade.Step(false, 0f, 23f, 4f, 4f);
            Require(Mathf.Approximately(fade.Opacity, partial * .5f), "Partial fog must fade smoothly");
            fade.Reset();
            Require(fade.Opacity == 0f, "Scene unload must clear fog");
            fade.Step(true, 0f, 30f, 0f, 0f);
            Require(fade.Opacity == 1f, "Zero-duration fade-in");
            fade.Step(false, 0f, 30f, 0f, 0f);
            Require(fade.Opacity == 0f, "Zero-duration fade-out");
            Debug.Log("[Fog fade checks] PASS: smooth fade-out, short waves, completion, reset and zero durations.");
        }
        private static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
    }
}
