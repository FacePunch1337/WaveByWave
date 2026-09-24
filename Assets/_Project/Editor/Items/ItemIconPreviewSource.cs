using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace WaveByWave.Editor.Items
{
    // AssetPreview uses a fixed camera. Rotate a renderer-only copy, never the authored prefab.
    public sealed class ItemIconPreviewSource : IDisposable
    {
        private GameObject _model, _rotatedAsset;
        private Quaternion _rotation;
        private string _folder;
        public string TemporaryFolder => _folder;

        public Texture2D GetPreview(GameObject model, Vector3 eulerAngles)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            var rotation = Quaternion.Euler(eulerAngles);
            if (Quaternion.Angle(rotation, Quaternion.identity) < .001f)
            {
                Dispose();
                return AssetPreview.GetAssetPreview(model);
            }
            if (_model != model || _rotatedAsset == null || Quaternion.Angle(rotation, _rotation) > .001f)
            {
                Dispose();
                _model = model; _rotation = rotation;
                CreateRotatedAsset();
            }
            return AssetPreview.GetAssetPreview(_rotatedAsset);
        }

        private void CreateRotatedAsset()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            GameObject root = null;
            try
            {
                root = new GameObject("Item icon view");
                SceneManager.MoveGameObjectToScene(root, scene);
                var pivot = new GameObject("Rotation").transform;
                pivot.SetParent(root.transform, false); pivot.localRotation = _rotation;
                var count = CopyGeometry(_model.transform, pivot);
                if (count == 0) throw new InvalidOperationException("В prefab нет активных мешей с MeshFilter и MeshRenderer для запекания.");
                _folder = "Assets/__ItemIconPreview_" + Guid.NewGuid().ToString("N");
                ItemIconBaker.EnsureFolder(_folder);
                _rotatedAsset = PrefabUtility.SaveAsPrefabAsset(root, _folder + "/View.prefab");
                if (_rotatedAsset == null) throw new InvalidOperationException("Не удалось подготовить повернутую модель.");
            }
            catch { Dispose(); throw; }
            finally
            {
                if (root != null) Object.DestroyImmediate(root);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        private static int CopyGeometry(Transform source, Transform parent)
        {
            if (!source.gameObject.activeSelf) return 0;
            var node = new GameObject(source.name).transform;
            node.SetParent(parent, false);
            node.localPosition = source.localPosition;
            node.localRotation = source.localRotation;
            node.localScale = source.localScale;
            var count = 0;
            var filter = source.GetComponent<MeshFilter>();
            var renderer = source.GetComponent<MeshRenderer>();
            if (filter != null && filter.sharedMesh != null && renderer != null && renderer.enabled)
            {
                node.gameObject.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
                var copy = node.gameObject.AddComponent<MeshRenderer>();
                copy.sharedMaterials = renderer.sharedMaterials;
                copy.shadowCastingMode = renderer.shadowCastingMode;
                copy.receiveShadows = renderer.receiveShadows;
                count++;
            }
            foreach (Transform child in source) count += CopyGeometry(child, node);
            return count;
        }

        public void Dispose()
        {
            _rotatedAsset = null; _model = null;
            if (string.IsNullOrEmpty(_folder)) return;
            var path = _folder; _folder = null;
            if (path.StartsWith("Assets/__ItemIconPreview_", StringComparison.Ordinal) && AssetDatabase.IsValidFolder(path))
                AssetDatabase.DeleteAsset(path);
        }
    }
}
