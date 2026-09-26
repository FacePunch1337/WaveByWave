using UnityEditor;
using UnityEngine;
using WaveByWave.Items;

namespace WaveByWave.Editor.Items
{
    [CustomEditor(typeof(ItemDefinition)), CanEditMultipleObjects]
    internal sealed class ItemDefinitionEditor : UnityEditor.Editor
    {
        private static readonly string[] PoseFields = {
            "overrideHeldPose", "heldPosition", "heldEulerAngles", "heldScale",
            "overrideSecondaryHeldPose", "secondaryHeldPosition", "secondaryHeldEulerAngles",
            "overrideHandGripPoints", "rightHandGrip", "leftHandGrip"
        };
        private static readonly string[] Excluded = {
            "heldAndIkProfile", "overrideHeldPose", "heldPosition", "heldEulerAngles", "heldScale",
            "overrideSecondaryHeldPose", "secondaryHeldPosition", "secondaryHeldEulerAngles",
            "overrideHandGripPoints", "rightHandGrip", "leftHandGrip"
        };
        private string _assignmentError;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            DrawPropertiesExcluding(serializedObject, Excluded);
            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Общий профиль Held / IK", EditorStyles.boldLabel);
            var profile = serializedObject.FindProperty("heldAndIkProfile");
            EditorGUI.showMixedValue = profile.hasMultipleDifferentValues;
            EditorGUI.BeginChangeCheck();
            var next = (ItemDefinition)EditorGUILayout.ObjectField(new GUIContent("Базовый предмет",
                "Выберите ItemDefinition, например предмет обычного качества. Можно назначить сразу нескольким выделенным вариантам."),
                profile.objectReferenceValue, typeof(ItemDefinition), false);
            if (EditorGUI.EndChangeCheck()) AssignProfile(next);
            EditorGUI.showMixedValue = false;
            if (!string.IsNullOrEmpty(_assignmentError)) EditorGUILayout.HelpBox(_assignmentError, MessageType.Error);

            var allLocal = true;
            var sameSource = true;
            ItemDefinition shared = null;
            foreach (ItemDefinition item in targets)
            {
                if (!item.TryResolveHeldAndIkSource(out var source))
                    EditorGUILayout.HelpBox($"{item.name}: циклическая ссылка профилей. Временно используются собственные Held/IK. Исправьте поле «Базовый предмет».", MessageType.Error);
                allLocal &= source == item;
                if (shared == null) shared = source;
                else sameSource &= source == shared;
            }
            if (allLocal)
            {
                EditorGUILayout.HelpBox("Собственные настройки. Чтобы связать варианты редкости, выделите их вместе и назначьте базовый предмет. Его дальнейшие изменения применятся автоматически.", MessageType.Info);
                DrawPoseFields(serializedObject);
                serializedObject.ApplyModifiedProperties();
            }
            else
            {
                EditorGUILayout.HelpBox("Held, блок/прицеливание и IK наследуются. Меняйте их у базового предмета. Собственные значения сохранены и вернутся при удалении ссылки. Модель, иконка, редкость и урон остаются у каждого предмета своими.", MessageType.Info);
                if (sameSource && shared != null)
                {
                    using (new EditorGUI.DisabledScope(true))
                    {
                        EditorGUILayout.ObjectField("Источник настроек", shared, typeof(ItemDefinition), false);
                        using var inherited = new SerializedObject(shared);
                        inherited.Update();
                        DrawPoseFields(inherited);
                    }
                    if (GUILayout.Button("Открыть источник Held / IK")) Selection.activeObject = shared;
                }
                else EditorGUILayout.HelpBox("У выделенных предметов разные источники. Назначьте общий базовый предмет или выберите один вариант для просмотра его настроек.", MessageType.Info);
                if (GUILayout.Button("Вернуть собственные Held / IK")) AssignProfile(null);
            }
            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(targets.Length != 1))
                if (GUILayout.Button("Открыть запекание иконки…")) ItemIconBakerWindow.OpenFor((ItemDefinition)target);
        }

        private void AssignProfile(ItemDefinition profile)
        {
            foreach (ItemDefinition item in targets)
                if (!item.CanUseHeldAndIkProfile(profile))
                {
                    _assignmentError = "Нельзя назначить предмет самому себе или создать замкнутую цепочку профилей. Если базовый предмет выделен вместе с вариантами, уберите его из выделения.";
                    return;
                }
            Undo.RecordObjects(targets, "Изменить базовый профиль Held / IK");
            foreach (ItemDefinition item in targets)
            {
                item.TrySetHeldAndIkProfile(profile);
                EditorUtility.SetDirty(item);
            }
            _assignmentError = null;
            serializedObject.Update();
        }

        private static void DrawPoseFields(SerializedObject data)
        {
            foreach (var name in PoseFields)
                EditorGUILayout.PropertyField(data.FindProperty(name), true);
        }
    }
}
