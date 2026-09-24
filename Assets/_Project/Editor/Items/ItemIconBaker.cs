using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using WaveByWave.Items;
using Object = UnityEngine.Object;

namespace WaveByWave.Editor.Items
{
    [Serializable]
    public sealed class ItemIconBakeSettings
    {
        public string OutputFolder = "Assets/_Project/Prefabs/UI/Art/ItemIcons";
        public bool ReplaceExisting;
        public Vector3 ModelEulerAngles;
        public bool RemoveBackground = true;
        public int BackgroundTolerance = 6;
        public bool RemoveEnclosedBackground;
        public bool Crop = true;
        public int Padding = 2;
        public bool BrightenPreview = true;
        public float Brightness = 1f;

        public ItemIconBakeSettings Copy() => (ItemIconBakeSettings)MemberwiseClone();
    }

    public static class ItemIconBaker
    {
        public static string ValidateFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("Выберите папку внутри Assets.");
            folder = folder.Replace('\\', '/').TrimEnd('/');
            if (folder != "Assets" && !folder.StartsWith("Assets/", StringComparison.Ordinal))
                throw new ArgumentException("Иконки должны сохраняться внутри Assets этого проекта.");
            var assets = Path.GetFullPath(Application.dataPath);
            var target = Path.GetFullPath(Path.Combine(assets, folder.Substring(6).TrimStart('/')));
            if (target != assets && !target.StartsWith(assets + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Папка выходит за границы Assets.");
            if (folder.Split('/').Any(part => part == "." || part == ".." || string.IsNullOrWhiteSpace(part)))
                throw new ArgumentException("Укажите обычный путь к папке без точек и пустых сегментов.");
            return folder;
        }

        public static void EnsureFolder(string folder)
        {
            var parts = ValidateFolder(folder).Split('/');
            var current = parts[0];
            for (var i = 1; i < parts.Length; i++)
            {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    if (string.IsNullOrEmpty(AssetDatabase.CreateFolder(current, parts[i])))
                        throw new IOException("Не удалось создать папку: " + next);
                }
                current = next;
            }
        }

        // AssetPreview owns its texture. Always process a copy and never change the model or scene.
        public static Texture2D Process(Texture2D preview, ItemIconBakeSettings settings)
        {
            if (preview == null) throw new ArgumentNullException(nameof(preview));
            var pixels = ReadPixels(preview);
            var width = preview.width;
            var height = preview.height;
            if (settings.RemoveBackground) RemoveBackground(pixels, width, height, settings);
            var left = width; var right = -1; var bottom = height; var top = -1;
            for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
            {
                if (pixels[y * width + x].a == 0) continue;
                left = Mathf.Min(left, x); right = Mathf.Max(right, x);
                bottom = Mathf.Min(bottom, y); top = Mathf.Max(top, y);
            }
            if (right < left)
                throw new InvalidOperationException("Иконка стала полностью прозрачной. Уменьшите допуск фона или отключите его удаление.");
            var padding = settings.Crop ? Mathf.Clamp(settings.Padding, 0, 32) : 0;
            if (!settings.Crop) { left = 0; right = width - 1; bottom = 0; top = height - 1; }
            var outputWidth = right - left + 1 + padding * 2;
            var outputHeight = top - bottom + 1 + padding * 2;
            var output = new Color32[outputWidth * outputHeight];
            for (var y = bottom; y <= top; y++) for (var x = left; x <= right; x++)
            {
                Color color = pixels[y * width + x];
                if (color.a == 0) continue;
                if (settings.BrightenPreview) color = color.gamma;
                var brightness = Mathf.Clamp(settings.Brightness, .1f, 3f);
                color.r *= brightness; color.g *= brightness; color.b *= brightness;
                output[(y - bottom + padding) * outputWidth + x - left + padding] = color;
            }
            var result = new Texture2D(outputWidth, outputHeight, TextureFormat.RGBA32, false)
            {
                name = "Item icon sample", hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp
            };
            result.SetPixels32(output); result.Apply();
            return result;
        }

        private static Color32[] ReadPixels(Texture2D source)
        {
            if (source.isReadable) return source.GetPixels32();
            var target = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var previous = RenderTexture.active;
            var srgb = GL.sRGBWrite;
            Texture2D copy = null;
            try
            {
                GL.sRGBWrite = QualitySettings.activeColorSpace == ColorSpace.Linear;
                Graphics.Blit(source, target); RenderTexture.active = target;
                copy = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
                copy.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0); copy.Apply();
                return copy.GetPixels32();
            }
            finally
            {
                GL.sRGBWrite = srgb; RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
                if (copy != null) Object.DestroyImmediate(copy);
            }
        }

        private static void RemoveBackground(Color32[] pixels, int width, int height, ItemIconBakeSettings settings)
        {
            var border = new List<int>(width * 2 + height * 2);
            for (var x = 0; x < width; x++) { border.Add(x); border.Add((height - 1) * width + x); }
            for (var y = 1; y < height - 1; y++) { border.Add(y * width); border.Add(y * width + width - 1); }
            // A preview that already has transparency needs no color keying.
            if (border.Any(i => pixels[i].a < 250)) return;
            var histogram = new Dictionary<int, int>();
            var mostFrequent = 0;
            var background = pixels[0];
            foreach (var index in border)
            {
                var p = pixels[index];
                var key = (p.r >> 2) << 12 | (p.g >> 2) << 6 | p.b >> 2;
                histogram.TryGetValue(key, out var count);
                histogram[key] = ++count;
                if (count > mostFrequent) { mostFrequent = count; background = p; }
            }
            var visited = new bool[pixels.Length];
            var queue = new Queue<int>();
            var tolerance = Mathf.Clamp(settings.BackgroundTolerance, 0, 64);
            void Add(int index)
            {
                if (visited[index]) return;
                visited[index] = true;
                var c = pixels[index];
                if (Mathf.Max(Mathf.Abs(c.r - background.r), Mathf.Abs(c.g - background.g), Mathf.Abs(c.b - background.b)) > tolerance) return;
                pixels[index] = new Color32(0, 0, 0, 0);
                queue.Enqueue(index);
            }
            if (settings.RemoveEnclosedBackground)
            {
                for (var i = 0; i < pixels.Length; i++) Add(i);
                return;
            }
            foreach (var index in border) Add(index);
            while (queue.Count > 0)
            {
                var at = queue.Dequeue(); var x = at % width; var y = at / width;
                if (x > 0) Add(at - 1); if (x + 1 < width) Add(at + 1);
                if (y > 0) Add(at - width); if (y + 1 < height) Add(at + width);
            }
        }

        public static Sprite Save(ItemDefinition item, Texture2D processed, string outputFolder)
        {
            var itemPath = AssetDatabase.GetAssetPath(item);
            if (string.IsNullOrEmpty(itemPath)) throw new ArgumentException("Сначала сохраните ItemDefinition как ассет.");
            // Rebaking a native icon must update the asset already used by the project.
            // The original HUD icons store Sprite and Texture2D in separate .asset files.
            if (CanReplaceNativeIcon(item.Icon))
            {
                ReplaceNativeIcon(item.Icon, processed);
                return item.Icon;
            }
            var guid = AssetDatabase.AssetPathToGUID(itemPath);
            var name = string.IsNullOrEmpty(item.Id) ? item.name : item.Id;
            name = new string(name.Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_').ToArray());
            if (name.Length > 64) name = name.Substring(0, 64);
            var stem = "Icon_" + name + "_" + guid.Substring(0, 8);
            var folder = ValidateFolder(outputFolder);
            EnsureFolder(folder);
            var path = folder + "/" + stem + ".asset";
            var marker = "WaveByWave.ItemIcon:" + guid;
            // Keep the asset path when the item's ID has been renamed since the previous bake.
            var oldPath = item.Icon != null ? AssetDatabase.GetAssetPath(item.Icon) : "";
            if (oldPath.StartsWith(folder + "/", StringComparison.Ordinal) && AssetImporter.GetAtPath(oldPath)?.userData == marker)
            { path = oldPath; stem = Path.GetFileNameWithoutExtension(path); }
            var oldAssets = AssetDatabase.LoadAllAssetsAtPath(path) ?? Array.Empty<Object>();
            var savedTexture = oldAssets.OfType<Texture2D>().FirstOrDefault();
            var savedSprite = oldAssets.OfType<Sprite>().FirstOrDefault();
            if (oldAssets.Length > 0 && (savedTexture == null || savedSprite == null || AssetImporter.GetAtPath(path)?.userData != marker))
                throw new IOException("Файл занят другим ассетом: " + path);

            var texture = new Texture2D(processed.width, processed.height, TextureFormat.RGBA32, false)
            {
                name = stem + " Texture", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp
            };
            texture.SetPixels32(processed.GetPixels32()); texture.Apply();
            if (savedTexture != null)
            {
                EditorUtility.CopySerialized(texture, savedTexture); Object.DestroyImmediate(texture);
                texture = savedTexture;
            }
            else AssetDatabase.CreateAsset(texture, path);
            var sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), Vector2.one * .5f, 100, 0, SpriteMeshType.FullRect);
            sprite.name = stem;
            if (savedSprite != null)
            {
                EditorUtility.CopySerialized(sprite, savedSprite); Object.DestroyImmediate(sprite);
                sprite = savedSprite;
            }
            else
            {
                AssetDatabase.AddObjectToAsset(sprite, path);
                AssetDatabase.SetMainObject(sprite, path);
            }
            EditorUtility.SetDirty(texture); EditorUtility.SetDirty(sprite);
            var importer = AssetImporter.GetAtPath(path);
            importer.userData = marker;
            AssetDatabase.WriteImportSettingsIfDirty(path);
            AssetDatabase.SaveAssetIfDirty(sprite);
            var data = new SerializedObject(item);
            data.FindProperty("icon").objectReferenceValue = sprite;
            data.ApplyModifiedProperties();
            AssetDatabase.SaveAssetIfDirty(item);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            EditorApplication.RepaintProjectWindow();
            return sprite;
        }

        public static bool CanReplaceNativeIcon(Sprite sprite)
        {
            if (sprite == null || sprite.texture == null) return false;
            var spritePath = AssetDatabase.GetAssetPath(sprite);
            var texturePath = AssetDatabase.GetAssetPath(sprite.texture);
            // Do not overwrite source PNGs, imported models or a region of an atlas.
            return spritePath.StartsWith("Assets/", StringComparison.Ordinal) && spritePath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase) &&
                texturePath.StartsWith("Assets/", StringComparison.Ordinal) && texturePath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase) &&
                sprite.rect == new Rect(0, 0, sprite.texture.width, sprite.texture.height);
        }

        public static void ReplaceNativeIcon(Sprite sprite, Texture2D processed)
        {
            if (!CanReplaceNativeIcon(sprite)) throw new ArgumentException("Иконка должна быть отдельным native Sprite с собственной текстурой.");
            var texture = sprite.texture;
            var spritePath = AssetDatabase.GetAssetPath(sprite);
            var texturePath = AssetDatabase.GetAssetPath(texture);
            var pivot = new Vector2(sprite.pivot.x / sprite.rect.width, sprite.pivot.y / sprite.rect.height);
            var pixelsPerUnit = sprite.pixelsPerUnit;
            if (texture.width != processed.width || texture.height != processed.height || texture.format != TextureFormat.RGBA32)
                if (!texture.Reinitialize(processed.width, processed.height, TextureFormat.RGBA32, false))
                    throw new InvalidOperationException("Не удалось обновить размер текстуры иконки.");
            texture.SetPixels32(processed.GetPixels32()); texture.Apply();
            var replacement = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), pivot, pixelsPerUnit, 0, SpriteMeshType.FullRect);
            replacement.name = sprite.name;
            try { EditorUtility.CopySerialized(replacement, sprite); }
            finally { Object.DestroyImmediate(replacement); }
            EditorUtility.SetDirty(texture); EditorUtility.SetDirty(sprite);
            AssetDatabase.SaveAssetIfDirty(texture);
            AssetDatabase.SaveAssetIfDirty(sprite);
            AssetDatabase.ImportAsset(texturePath, ImportAssetOptions.ForceUpdate);
            if (spritePath != texturePath) AssetDatabase.ImportAsset(spritePath, ImportAssetOptions.ForceUpdate);
            EditorApplication.RepaintProjectWindow();
        }
    }

    // Tick from the window, yielding between assets while Unity prepares its thumbnails.
    public sealed class ItemIconBakeJob
    {
        private readonly ItemDefinition[] _items;
        private readonly ItemIconBakeSettings _settings;
        private readonly Func<GameObject, Texture2D> _getPreview;
        private readonly ItemIconPreviewSource _previewSource;
        private int _index;
        private double _waitingSince = -1;
        public int Saved { get; private set; }
        public int Skipped { get; private set; }
        public int Failed { get; private set; }
        public bool Cancelled { get; private set; }
        public bool Done => Cancelled || _index >= _items.Length;
        public int Total => _items.Length;
        public float Progress => Total == 0 ? 1 : (float)_index / Total;
        public string Status { get; private set; } = "Подготовка…";
        public List<string> Messages { get; } = new();

        public ItemIconBakeJob(IEnumerable<ItemDefinition> items, ItemIconBakeSettings settings, Func<GameObject, Texture2D> getPreview = null)
        {
            _items = items.Where(x => x != null).Distinct().ToArray();
            _settings = settings.Copy();
            ItemIconBaker.ValidateFolder(_settings.OutputFolder);
            if (getPreview != null) _getPreview = getPreview;
            else
            {
                _previewSource = new ItemIconPreviewSource();
                _getPreview = model => _previewSource.GetPreview(model, _settings.ModelEulerAngles);
            }
            if (_items.Length == 0) Status = "Нет предметов для запекания.";
        }

        public void Cancel()
        {
            Cancelled = true; _previewSource?.Dispose();
            Status = "Отменено. Уже сохранённые иконки оставлены.";
        }

        public void Tick(double now)
        {
            if (Done) return;
            var item = _items[_index];
            Texture2D processed = null;
            try
            {
                if (item == null) { Skipped++; Advance(); return; }
                if (!_settings.ReplaceExisting && item.Icon != null) { Skipped++; Advance(); return; }
                if (item.WorldVisualPrefab == null) throw new InvalidOperationException("Не назначен World Visual Prefab.");
                Status = $"{_index + 1}/{Total}: {item.DisplayName}";
                var preview = _getPreview(item.WorldVisualPrefab);
                if (preview == null)
                {
                    if (_waitingSince < 0) _waitingSince = now;
                    if (now - _waitingSince < 40) return;
                    throw new TimeoutException("Unity не подготовила миниатюру за 40 с. Проверьте, есть ли у prefab видимые меши.");
                }
                processed = ItemIconBaker.Process(preview, _settings);
                ItemIconBaker.Save(item, processed, _settings.OutputFolder);
                Saved++;
            }
            catch (Exception ex)
            {
                Failed++;
                Messages.Add((item != null ? item.name : "Удалённый предмет") + ": " + ex.Message);
            }
            finally { if (processed != null) Object.DestroyImmediate(processed); }
            Advance();
        }

        private void Advance()
        {
            _index++; _waitingSince = -1;
            if (Done)
            {
                _previewSource?.Dispose();
                Status = $"Готово: {Saved}. Пропущено: {Skipped}. Ошибок: {Failed}.";
            }
        }
    }
}
