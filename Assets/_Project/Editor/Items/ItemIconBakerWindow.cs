using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using WaveByWave.Items;

namespace WaveByWave.Editor.Items
{
    public sealed class ItemIconBakerWindow : EditorWindow
    {
        [SerializeField] private ItemDefinition item;
        [SerializeField] private ItemCatalog catalog;
        [SerializeField] private ItemIconBakeSettings settings = new();
        private Texture2D _sample;
        private ItemIconPreviewSource _previewSource;
        private ItemIconBakeJob _job;
        private Vector2 _scroll;
        private string _sampleStatus = "Выберите предмет для образца.";
        private string _error;
        private bool _previewPending;
        private double _previewStarted, _nextTick, _previewAfter;
        private string SettingsKey => "WaveByWave.ItemIconBaker." + Hash128.Compute(Application.dataPath);

        [MenuItem("Tools/Wave by Wave/Items/Icon Baker")]
        public static void Open()
        {
            var window = GetWindow<ItemIconBakerWindow>("Иконки предметов");
            window.minSize = new Vector2(500, 640);
            window.OnSelectionChange();
            if (window.item == null && window.catalog != null)
            {
                window.item = window.catalog.Items.FirstOrDefault(value => value != null && value.WorldVisualPrefab != null);
                window.QueuePreview();
            }
            window.Show();
        }

        public static void OpenFor(ItemDefinition value)
        {
            Open();
            var window = GetWindow<ItemIconBakerWindow>();
            window.item = value; window.QueuePreview();
        }

        [MenuItem("Assets/Wave by Wave/Иконки предметов…", false, 2050)]
        private static void OpenSelected() => Open();

        [MenuItem("Assets/Wave by Wave/Иконки предметов…", true)]
        private static bool CanOpenSelected() => SelectedItems().Length > 0;

        private void OnEnable()
        {
            settings ??= new ItemIconBakeSettings();
            var saved = EditorPrefs.GetString(SettingsKey, "");
            if (!string.IsNullOrEmpty(saved))
            {
                try { JsonUtility.FromJsonOverwrite(saved, settings); }
                catch (ArgumentException) { settings = new ItemIconBakeSettings(); }
            }
            if (catalog == null) catalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/_Project/Data/ItemCatalog.asset");
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            _previewSource = new ItemIconPreviewSource();
            if (item != null) QueuePreview();
        }

        private void OnDisable()
        {
            EditorApplication.update -= Tick;
            if (_job != null && !_job.Done) _job.Cancel();
            _previewSource?.Dispose();
            EditorPrefs.SetString(SettingsKey, JsonUtility.ToJson(settings));
            ClearSample();
        }

        private void OnSelectionChange()
        {
            if (_job != null && !_job.Done) return;
            if (Selection.activeObject is ItemCatalog selectedCatalog) catalog = selectedCatalog;
            var selected = SelectedItems();
            if (selected.Length > 0) { item = selected[0]; QueuePreview(); }
            Repaint();
        }

        private static ItemDefinition[] SelectedItems()
        {
            var result = new List<ItemDefinition>();
            foreach (var selected in Selection.objects)
            {
                if (selected is ItemDefinition definition) result.Add(definition);
                else if (selected is ItemCatalog selectedCatalog) result.AddRange(selectedCatalog.Items);
            }
            return result.Where(value => value != null).Distinct().ToArray();
        }

        private void OnGUI()
        {
            var busy = _job != null && !_job.Done;
            var unavailable = EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling;
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.LabelField("Иконки из моделей предметов", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Образец не меняет ассеты. Запекание сохраняет Sprite и назначает его в Icon.", EditorStyles.wordWrappedLabel);
            EditorGUILayout.Space(6);
            using (new EditorGUI.DisabledScope(busy || unavailable))
            {
                EditorGUI.BeginChangeCheck();
                item = (ItemDefinition)EditorGUILayout.ObjectField("Предмет / образец", item, typeof(ItemDefinition), false);
                if (EditorGUI.EndChangeCheck()) QueuePreview();
                catalog = (ItemCatalog)EditorGUILayout.ObjectField("Каталог", catalog, typeof(ItemCatalog), false);
                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.ObjectField("Текущая иконка", item != null ? item.Icon : null, typeof(Sprite), false);
                if (item != null && item.Icon != null)
                {
                    EditorGUILayout.LabelField(AssetDatabase.GetAssetPath(item.Icon), EditorStyles.wordWrappedMiniLabel);
                    if (GUILayout.Button("Показать текущую иконку в Project")) EditorGUIUtility.PingObject(item.Icon);
                }
                EditorGUILayout.Space(5);
                EditorGUI.BeginChangeCheck();
                settings.ModelEulerAngles = EditorGUILayout.Vector3Field("Поворот модели, °", settings.ModelEulerAngles);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Влево 90°")) { settings.ModelEulerAngles.y -= 90; GUI.changed = true; }
                    if (GUILayout.Button("Обратная сторона")) { settings.ModelEulerAngles.y += 180; GUI.changed = true; }
                    if (GUILayout.Button("Сброс поворота")) { settings.ModelEulerAngles = Vector3.zero; GUI.changed = true; }
                }
                settings.RemoveBackground = EditorGUILayout.Toggle("Удалять фон", settings.RemoveBackground);
                using (new EditorGUI.DisabledScope(!settings.RemoveBackground))
                {
                    settings.BackgroundTolerance = EditorGUILayout.IntSlider(new GUIContent("Допуск фона", "Повышайте, если вокруг модели остаётся фон. Большие значения могут удалить похожие цвета самой модели."), settings.BackgroundTolerance, 0, 64);
                    settings.RemoveEnclosedBackground = EditorGUILayout.Toggle(new GUIContent("Фон внутри отверстий", "Удалять цвет фона также внутри замкнутых отверстий, например в ручке. Отключите, если исчезают серые детали."), settings.RemoveEnclosedBackground);
                }
                settings.Crop = EditorGUILayout.Toggle("Обрезать пустые края", settings.Crop);
                using (new EditorGUI.DisabledScope(!settings.Crop))
                    settings.Padding = EditorGUILayout.IntSlider("Отступ, пиксели", settings.Padding, 0, 32);
                settings.BrightenPreview = EditorGUILayout.Toggle(new GUIContent("Осветлить миниатюру", "Использовать такое же осветление, как у подготовленных иконок HUD."), settings.BrightenPreview);
                settings.Brightness = EditorGUILayout.Slider("Яркость", settings.Brightness, .1f, 3f);
                if (EditorGUI.EndChangeCheck()) QueuePreview();
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(item == null || item.WorldVisualPrefab == null))
                        if (GUILayout.Button("Обновить образец")) QueuePreview();
                    using (new EditorGUI.DisabledScope(_sample == null || _previewPending))
                        if (GUILayout.Button("Экспорт образца в PNG…")) ExportPng();
                }
            }
            var previewRect = GUILayoutUtility.GetRect(160, 192, GUILayout.ExpandWidth(true));
            if (_sample != null) EditorGUI.DrawTextureTransparent(previewRect, _sample, ScaleMode.ScaleToFit);
            else EditorGUI.HelpBox(previewRect, _sampleStatus, MessageType.None);
            EditorGUILayout.LabelField(_sampleStatus, EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField("Поворот применяется только к копии геометрии. Исходный prefab, камера и свет сцены не меняются.", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(8);
            using (new EditorGUI.DisabledScope(busy || unavailable))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    settings.OutputFolder = EditorGUILayout.TextField("Папка спрайтов", settings.OutputFolder);
                    if (GUILayout.Button("…", GUILayout.Width(28))) PickFolder();
                }
                settings.ReplaceExisting = EditorGUILayout.ToggleLeft("Заменять существующие иконки", settings.ReplaceExisting);
                if (settings.ReplaceExisting)
                    EditorGUILayout.HelpBox("Существующие иконки .asset обновляются на месте с сохранением ссылок. Папка выше используется для новых иконок. Если несколько предметов используют один Sprite, его изображение изменится у всех.", MessageType.Info);
                if (!settings.ReplaceExisting)
                    EditorGUILayout.LabelField("Предметы с заполненным Icon будут пропущены.", EditorStyles.wordWrappedMiniLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(item == null))
                        if (GUILayout.Button("Запечь предмет")) StartBake(new[] { item });
                    var selected = SelectedItems();
                    using (new EditorGUI.DisabledScope(selected.Length == 0))
                        if (GUILayout.Button($"Выделенные ({selected.Length})")) StartBake(selected);
                    using (new EditorGUI.DisabledScope(catalog == null))
                        if (GUILayout.Button("Весь каталог")) StartBake(catalog.Items);
                }
            }
            if (_job != null)
            {
                EditorGUI.ProgressBar(GUILayoutUtility.GetRect(1, 23, GUILayout.ExpandWidth(true)), _job.Progress, _job.Status);
                if (!_job.Done && GUILayout.Button("Остановить запекание")) _job.Cancel();
                if (_job.Messages.Count > 0)
                    EditorGUILayout.HelpBox(string.Join("\n", _job.Messages), MessageType.Warning);
            }
            if (!string.IsNullOrEmpty(_error)) EditorGUILayout.HelpBox(_error, MessageType.Error);
            if (unavailable) EditorGUILayout.HelpBox("Запекание доступно вне Play Mode, после компиляции скриптов.", MessageType.Info);
            EditorGUILayout.EndScrollView();
        }

        private void QueuePreview()
        {
            _previewPending = item != null && item.WorldVisualPrefab != null;
            _previewStarted = EditorApplication.timeSinceStartup;
            _previewAfter = _previewStarted + .2;
            _sampleStatus = item == null ? "Выберите предмет." : item.WorldVisualPrefab == null ?
                "У предмета не назначен World Visual Prefab." : "Подготовка образца…";
            _error = null;
            if (!_previewPending) _previewSource?.Dispose();
            ClearSample(); Repaint();
        }

        private void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                if (_job != null && !_job.Done) { _job.Cancel(); Repaint(); }
                _previewSource?.Dispose();
                return;
            }
            var now = EditorApplication.timeSinceStartup;
            if (now < _nextTick) return;
            _nextTick = now + .1;
            if (_job != null && !_job.Done) { _job.Tick(now); Repaint(); return; }
            if (!_previewPending || now < _previewAfter) return;
            try
            {
                if (item == null || item.WorldVisualPrefab == null) { _previewPending = false; return; }
                var preview = _previewSource.GetPreview(item.WorldVisualPrefab, settings.ModelEulerAngles);
                if (preview == null)
                {
                    if (now - _previewStarted < 40) return;
                    throw new TimeoutException("Unity не подготовила миниатюру. Проверьте меши prefab и нажмите «Обновить образец».");
                }
                ClearSample();
                _sample = ItemIconBaker.Process(preview, settings);
                _sampleStatus = $"{item.DisplayName} · {_sample.width}×{_sample.height} px · исходная миниатюра {preview.width}×{preview.height} px";
            }
            catch (Exception ex)
            {
                _previewSource?.Dispose();
                _error = ex.Message; _sampleStatus = "Не удалось подготовить образец.";
            }
            _previewPending = false; Repaint();
        }

        private void StartBake(IEnumerable<ItemDefinition> items)
        {
            try
            {
                settings.OutputFolder = ItemIconBaker.ValidateFolder(settings.OutputFolder);
                _job = new ItemIconBakeJob(items, settings);
                _error = null;
                EditorPrefs.SetString(SettingsKey, JsonUtility.ToJson(settings));
            }
            catch (Exception ex) { _error = ex.Message; }
        }

        private void PickFolder()
        {
            var folder = EditorUtility.OpenFolderPanel("Папка для иконок", Application.dataPath, "");
            if (string.IsNullOrEmpty(folder)) return;
            var assets = Application.dataPath.Replace('\\', '/');
            folder = folder.Replace('\\', '/');
            if (folder.Equals(assets, StringComparison.OrdinalIgnoreCase)) settings.OutputFolder = "Assets";
            else if (folder.StartsWith(assets + "/", StringComparison.OrdinalIgnoreCase)) settings.OutputFolder = "Assets" + folder.Substring(assets.Length);
            else _error = "Выберите папку внутри Assets этого проекта.";
        }

        private void ExportPng()
        {
            var path = EditorUtility.SaveFilePanel("Экспорт иконки", "", item != null ? item.Id : "ItemIcon", "png");
            if (string.IsNullOrEmpty(path)) return;
            try { File.WriteAllBytes(path, _sample.EncodeToPNG()); }
            catch (Exception ex) { _error = ex.Message; }
        }

        private void ClearSample()
        {
            if (_sample != null) DestroyImmediate(_sample);
            _sample = null;
        }
    }

}
