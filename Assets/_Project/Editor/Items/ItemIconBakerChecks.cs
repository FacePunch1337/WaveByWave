using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using WaveByWave.Items;
using Object = UnityEngine.Object;

namespace WaveByWave.Editor.Items
{
    public static class ItemIconBakerChecks
    {
        [MenuItem("Tools/Wave by Wave/Checks/Item icon baker")]
        public static void Run()
        {
            var folder = "Assets/__ItemIconBakerCheck_" + Guid.NewGuid().ToString("N");
            Texture2D source = null, processed = null, enclosed = null, transparent = null;
            try
            {
                var rejected = false;
                try { ItemIconBaker.ValidateFolder("Assets/../../Outside"); }
                catch (ArgumentException) { rejected = true; }
                Require(rejected, "Output path must stay inside Assets");
                var settings = new ItemIconBakeSettings
                {
                    OutputFolder = folder, BrightenPreview = false, Brightness = 1, Padding = 2, BackgroundTolerance = 0
                };
                source = new Texture2D(16, 16, TextureFormat.RGBA32, false);
                var gray = new Color32(82, 82, 82, 255);
                var pixels = Enumerable.Repeat(gray, 16 * 16).ToArray();
                for (var y = 4; y < 12; y++) for (var x = 4; x < 12; x++)
                    pixels[y * 16 + x] = new Color32(170, 80, 35, 255);
                // A closed gray patch must survive edge-only removal.
                pixels[7 * 16 + 7] = gray;
                source.SetPixels32(pixels); source.Apply();
                processed = ItemIconBaker.Process(source, settings);
                Require(processed.width == 12 && processed.height == 12, "Crop must add exact transparent padding");
                Require(processed.GetPixel(0, 0).a == 0 && processed.GetPixel(2, 2).a == 1, "Object/padding alpha");
                Require(processed.GetPixel(5, 5).a == 1, "Gray detail inside the model must survive edge-only keying");
                Require(source.GetPixel(0, 0).a == 1, "Processing must not change Unity's source texture");
                settings.RemoveEnclosedBackground = true;
                enclosed = ItemIconBaker.Process(source, settings);
                Require(enclosed.GetPixel(5, 5).a == 0, "Enclosed background option must open holes");
                settings.RemoveEnclosedBackground = false;
                // Already-transparent source should preserve dark details and translucent pixels.
                pixels[0] = new Color32(0, 0, 0, 0); pixels[1] = new Color32(0, 0, 0, 128);
                source.SetPixels32(pixels); source.Apply();
                settings.Crop = false;
                transparent = ItemIconBaker.Process(source, settings);
                Require(transparent.width == 16 && Mathf.Abs(transparent.GetPixel(1, 0).a - 128f / 255) < .005f, "Existing alpha must be preserved");
                settings.Crop = true;

                ItemIconBaker.EnsureFolder(folder);
                var item = MakeItem(folder + "/Item.asset", "test", "Test item");
                var sprite = ItemIconBaker.Save(item, processed, folder);
                var assetPath = AssetDatabase.GetAssetPath(sprite);
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sprite, out string guid, out long localId);
                Require(item.Icon == sprite && sprite.texture.width == 12, "Saved sprite must be assigned to ItemDefinition");
                var again = ItemIconBaker.Save(item, transparent, folder);
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(again, out string guidAgain, out long idAgain);
                Require(again == sprite && guid == guidAgain && localId == idAgain && again.rect.width == 16,
                    "Rebake must preserve the sprite identity while updating dimensions");
                var data = new SerializedObject(item);
                data.FindProperty("id").stringValue = "renamed"; data.ApplyModifiedPropertiesWithoutUndo();
                var renamed = ItemIconBaker.Save(item, processed, folder);
                Require(AssetDatabase.GetAssetPath(renamed) == assetPath, "Item renaming must not break existing sprite references");
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
                var imported = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
                Require(imported != null && imported.texture != null && imported.rect.width == 12, "Sprite and texture must survive reimport");

                // Original HUD icons are separate native Sprite/Texture assets. Keep those references too.
                var legacyItem = MakeItem(folder + "/LegacyItem.asset", "legacy", "Existing HUD icon");
                var legacyTexture = Object.Instantiate(processed); legacyTexture.hideFlags = HideFlags.None;
                AssetDatabase.CreateAsset(legacyTexture, folder + "/ExistingTexture.asset");
                var legacySprite = Sprite.Create(legacyTexture, new Rect(0, 0, legacyTexture.width, legacyTexture.height), Vector2.one * .5f, 100);
                AssetDatabase.CreateAsset(legacySprite, folder + "/ExistingSprite.asset");
                data = new SerializedObject(legacyItem);
                data.FindProperty("icon").objectReferenceValue = legacySprite; data.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(legacySprite, out string legacyGuid, out long legacyId);
                var legacyResult = ItemIconBaker.Save(legacyItem, transparent, folder + "/NewIcons");
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(legacyResult, out string updatedGuid, out long updatedId);
                Require(legacyResult == legacySprite && legacyGuid == updatedGuid && legacyId == updatedId && legacyResult.rect.width == 16,
                    "Existing native icons must update in place even when a different output folder is selected");
                Require(legacyResult.texture == legacyTexture && !AssetDatabase.IsValidFolder(folder + "/NewIcons"),
                    "Rebake must preserve the original texture and avoid creating duplicate icons");

                var noModel = MakeItem(folder + "/Missing.asset", "missing", "Missing model");
                var valid = MakeItem(folder + "/Valid.asset", "valid", "Valid item");
                var model = AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/_Project/Data/ItemCatalog.asset")
                    .Items.First(x => x != null && x.WorldVisualPrefab != null).WorldVisualPrefab;
                data = new SerializedObject(valid);
                data.FindProperty("worldVisualPrefab").objectReferenceValue = model; data.ApplyModifiedPropertiesWithoutUndo();
                var job = new ItemIconBakeJob(new[] { item, noModel, valid, valid }, settings, _ => source);
                for (var i = 0; !job.Done && i < 10; i++) job.Tick(i);
                Require(job.Done && job.Total == 3 && job.Saved == 1 && job.Skipped == 1 && job.Failed == 1 && valid.Icon != null,
                    "Batch must deduplicate items, protect existing icons, and continue after a missing model");
                settings.ReplaceExisting = true;
                var timeout = new ItemIconBakeJob(new[] { valid }, settings, _ => null);
                timeout.Tick(0); Require(!timeout.Done, "Pending thumbnail must yield to the editor");
                timeout.Tick(41); Require(timeout.Done && timeout.Failed == 1, "Unavailable thumbnail must time out");
                var cancelled = new ItemIconBakeJob(new[] { valid }, settings, _ => source);
                cancelled.Cancel(); cancelled.Tick(0);
                Require(cancelled.Done && cancelled.Saved == 0, "Cancelled jobs must stop writing assets");
                Debug.Log("[Item icon baker] PASS: crop, alpha, background modes, source preservation, native asset persistence/reimport, stable combined/separate sprite and texture references, renamed items, safe batch/skip/failure, timeout and cancellation.");
            }
            finally
            {
                if (source != null) Object.DestroyImmediate(source);
                if (processed != null) Object.DestroyImmediate(processed);
                if (enclosed != null) Object.DestroyImmediate(enclosed);
                if (transparent != null) Object.DestroyImmediate(transparent);
                if (AssetDatabase.IsValidFolder(folder) && folder.StartsWith("Assets/__ItemIconBakerCheck_", StringComparison.Ordinal))
                    AssetDatabase.DeleteAsset(folder);
            }
        }

        private static ItemDefinition MakeItem(string path, string id, string name)
        {
            var item = ScriptableObject.CreateInstance<ItemDefinition>();
            AssetDatabase.CreateAsset(item, path);
            var data = new SerializedObject(item);
            data.FindProperty("id").stringValue = id;
            data.FindProperty("displayName").stringValue = name;
            data.ApplyModifiedPropertiesWithoutUndo();
            return item;
        }
        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
    }
}
