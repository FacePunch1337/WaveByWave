using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using WaveByWave.Generation;

namespace WaveByWave.Editor
{
    [CustomEditor(typeof(NightWaveSettings))]
    public sealed class NightWaveSettingsEditor : UnityEditor.Editor
    {
        private WaveEnemyProfile[] _profiles = Array.Empty<WaveEnemyProfile>();
        private string[] _profileNames = Array.Empty<string>();

        private void OnEnable() => RefreshProfiles();

        private void RefreshProfiles()
        {
            _profiles = AssetDatabase.FindAssets("t:WaveEnemyProfile")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<WaveEnemyProfile>)
                .Where(profile => profile != null)
                .OrderBy(profile => profile.InspectorOrder)
                .ThenBy(profile => profile.DisplayName)
                .ToArray();
            _profileNames = new[] { "— Select enemy —" }
                .Concat(_profiles.Select(profile => string.IsNullOrWhiteSpace(profile.DisplayName)
                    ? profile.name : profile.DisplayName)).ToArray();
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.HelpBox(
                "A wave owns the battlefield radius. Each fragment starts after the previous fragment is fully defeated. " +
                "Each enemy entry has its own type, count and spawn radius.", MessageType.Info);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("VictoryDisplayDuration"),
                new GUIContent("Victory Screen Duration"));
            DrawBattlefieldLink();
            EditorGUILayout.Space(8f);

            var waves = serializedObject.FindProperty("Waves");
            EditorGUILayout.LabelField("Waves", EditorStyles.boldLabel);
            for (var i = 0; i < waves.arraySize; i++) DrawWave(waves, i);
            if (GUILayout.Button("+ Add Wave", GUILayout.Height(28f)))
            {
                var element = Add(waves);
                element.FindPropertyRelative("Name").stringValue = $"Wave {waves.arraySize}";
                element.FindPropertyRelative("BattlefieldRadius").floatValue = 150f;
                element.FindPropertyRelative("Fragments").ClearArray();
                element.isExpanded = true;
            }
            serializedObject.ApplyModifiedProperties();
        }

        private static void DrawBattlefieldLink()
        {
            var battlefield = AssetDatabase.LoadAssetAtPath<NightBattlefieldSettings>(
                "Assets/_Project/Resources/NightBattlefieldSettings.asset");
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Fog and Boundary Profile", GUILayout.Width(150f));
                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.ObjectField(battlefield, typeof(NightBattlefieldSettings), false);
                if (GUILayout.Button("Open", GUILayout.Width(52f))) Selection.activeObject = battlefield;
            }
        }

        private void DrawWave(SerializedProperty waves, int index)
        {
            var wave = waves.GetArrayElementAtIndex(index);
            var name = wave.FindPropertyRelative("Name");
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (DrawHeader(wave, string.IsNullOrWhiteSpace(name.stringValue)
                        ? $"Wave {index + 1}" : $"{index + 1}. {name.stringValue}", waves, index)) return;
                if (!wave.isExpanded) return;
                EditorGUILayout.PropertyField(name, new GUIContent("Name"));
                EditorGUILayout.PropertyField(wave.FindPropertyRelative("BattlefieldRadius"),
                    new GUIContent("Battlefield Radius"));
                var fragments = wave.FindPropertyRelative("Fragments");
                EditorGUILayout.Space(4f);
                EditorGUILayout.LabelField("Fragments", EditorStyles.boldLabel);
                for (var i = 0; i < fragments.arraySize; i++) DrawFragment(fragments, i);
                if (GUILayout.Button("+ Add Fragment"))
                {
                    var fragment = Add(fragments);
                    fragment.FindPropertyRelative("Name").stringValue = $"Fragment {fragments.arraySize}";
                    fragment.FindPropertyRelative("Enemies").ClearArray();
                    fragment.isExpanded = true;
                }
            }
        }

        private void DrawFragment(SerializedProperty fragments, int index)
        {
            var fragment = fragments.GetArrayElementAtIndex(index);
            var name = fragment.FindPropertyRelative("Name");
            using (new EditorGUILayout.VerticalScope("box"))
            {
                if (DrawHeader(fragment, string.IsNullOrWhiteSpace(name.stringValue)
                        ? $"Fragment {index + 1}" : name.stringValue, fragments, index)) return;
                if (!fragment.isExpanded) return;
                EditorGUILayout.PropertyField(name, new GUIContent("Name"));
                var enemies = fragment.FindPropertyRelative("Enemies");
                EditorGUILayout.Space(3f);
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Enemy Type", EditorStyles.miniBoldLabel);
                    EditorGUILayout.LabelField("Count", EditorStyles.miniBoldLabel, GUILayout.Width(58f));
                    EditorGUILayout.LabelField("Exclusion Radius", EditorStyles.miniBoldLabel, GUILayout.Width(108f));
                    EditorGUILayout.LabelField("Spawn Band", EditorStyles.miniBoldLabel, GUILayout.Width(86f));
                    GUILayout.Space(24f);
                }
                for (var i = 0; i < enemies.arraySize; i++) DrawEnemy(enemies, i);
                if (GUILayout.Button("+ Add Enemy"))
                {
                    var enemy = Add(enemies);
                    enemy.FindPropertyRelative("EnemyType").objectReferenceValue =
                        _profiles.Length > 0 ? _profiles[0] : null;
                    enemy.FindPropertyRelative("Count").intValue = 1;
                    enemy.FindPropertyRelative("SpawnRadius").floatValue = 20f;
                    enemy.FindPropertyRelative("SpawnBandWidth").floatValue = 20f;
                }
            }
        }

        private void DrawEnemy(SerializedProperty enemies, int index)
        {
            var enemy = enemies.GetArrayElementAtIndex(index);
            var profile = enemy.FindPropertyRelative("EnemyType");
            var selected = Array.IndexOf(_profiles, profile.objectReferenceValue as WaveEnemyProfile) + 1;
            var removed = false;
            using (new EditorGUILayout.HorizontalScope())
            {
                var next = EditorGUILayout.Popup(Mathf.Max(0, selected), _profileNames);
                profile.objectReferenceValue = next > 0 && next <= _profiles.Length ? _profiles[next - 1] : null;
                var count = enemy.FindPropertyRelative("Count");
                count.intValue = Mathf.Max(0, EditorGUILayout.IntField(count.intValue, GUILayout.Width(58f)));
                var radius = enemy.FindPropertyRelative("SpawnRadius");
                radius.floatValue = Mathf.Max(1f, EditorGUILayout.FloatField(radius.floatValue, GUILayout.Width(108f)));
                var band = enemy.FindPropertyRelative("SpawnBandWidth");
                var bandValue = band.floatValue > 0f ? band.floatValue : 20f;
                band.floatValue = Mathf.Max(1f, EditorGUILayout.FloatField(bandValue, GUILayout.Width(86f)));
                if (GUILayout.Button("×", GUILayout.Width(24f)))
                {
                    enemies.DeleteArrayElementAtIndex(index);
                    removed = true;
                }
            }
            if (removed) return;
            if (profile.objectReferenceValue == null)
                EditorGUILayout.HelpBox("Select an Enemy Type profile.", MessageType.Warning);
        }

        private static bool DrawHeader(SerializedProperty item, string title,
            SerializedProperty array, int index)
        {
            var removed = false;
            using (new EditorGUILayout.HorizontalScope())
            {
                item.isExpanded = EditorGUILayout.Foldout(item.isExpanded, title, true, EditorStyles.foldoutHeader);
                using (new EditorGUI.DisabledScope(index == 0))
                    if (GUILayout.Button("↑", GUILayout.Width(24f)))
                    {
                        array.MoveArrayElement(index, index - 1);
                        removed = true;
                    }
                using (new EditorGUI.DisabledScope(index + 1 >= array.arraySize))
                    if (GUILayout.Button("↓", GUILayout.Width(24f)))
                    {
                        array.MoveArrayElement(index, index + 1);
                        removed = true;
                    }
                if (GUILayout.Button("×", GUILayout.Width(24f)))
                {
                    array.DeleteArrayElementAtIndex(index);
                    removed = true;
                }
            }
            return removed;
        }

        private static SerializedProperty Add(SerializedProperty array)
        {
            array.InsertArrayElementAtIndex(array.arraySize);
            return array.GetArrayElementAtIndex(array.arraySize - 1);
        }
    }

    [CustomEditor(typeof(WaveEnemyProfile))]
    public sealed class WaveEnemyProfileEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("DisplayName"));
            var spawnKind = serializedObject.FindProperty("SpawnKind");
            EditorGUILayout.PropertyField(spawnKind, new GUIContent("Enemy Class"));
            if ((WaveEnemySpawnKind)spawnKind.enumValueIndex == WaveEnemySpawnKind.Character)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("Species"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("CombatType"));
            }
            else EditorGUILayout.HelpBox("Ships themselves are excluded from the wave counter; their configured crew is counted.",
                MessageType.Info);
            serializedObject.ApplyModifiedProperties();
        }
    }
}
