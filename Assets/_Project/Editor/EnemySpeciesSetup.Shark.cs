using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace WaveByWave.Editor
{
    public static partial class EnemySpeciesSetup
    {
        private static GameObject CreateSharkRig(out AnimationClip idle, out AnimationClip swim, out AnimationClip attack)
        {
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(SharkSource);
            if (source == null) throw new InvalidOperationException("SharkV1 prefab missing.");
            var filter = source.GetComponentInChildren<MeshFilter>();
            var original = filter.sharedMesh;
            var mesh = Object.Instantiate(original);
            mesh.name = "SharkSkinned";
            var vertices = mesh.vertices;
            var bounds = mesh.bounds;
            // The source fish points along X. Its thin caudal fin identifies the tail end;
            // normalize the authored mesh to the enemy convention (+Z forward, origin at the bottom).
            float EndWidth(bool positive)
            {
                var points = vertices.Where(p => positive ? p.x > bounds.max.x - bounds.size.x * .15f :
                    p.x < bounds.min.x + bounds.size.x * .15f).ToArray();
                return points.Max(p => p.z) - points.Min(p => p.z);
            }
            var forward = EndWidth(true) > EndWidth(false) ? Vector3.right : Vector3.left;
            var rotation = Quaternion.FromToRotation(forward, Vector3.forward);
            var scale = 3.8f / bounds.size.x;
            var center = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
            var normals = mesh.normals;
            var tangents = mesh.tangents;
            for (var i = 0; i < vertices.Length; i++)
            {
                vertices[i] = rotation * (vertices[i] - center) * scale;
                if (i < normals.Length) normals[i] = rotation * normals[i];
                if (i < tangents.Length)
                {
                    var tangent = rotation * (Vector3)tangents[i];
                    tangents[i] = new Vector4(tangent.x, tangent.y, tangent.z, tangents[i].w);
                }
            }
            mesh.vertices = vertices; mesh.normals = normals; mesh.tangents = tangents; mesh.RecalculateBounds();
            var root = new GameObject("SharkRig");
            try
            {
                const int count = 7;
                var bones = new Transform[count];
                var binds = new Matrix4x4[count];
                var front = mesh.bounds.max.z * .65f;
                var back = mesh.bounds.min.z;
                for (var i = 0; i < count; i++)
                {
                    var bone = new GameObject("SharkSpine_" + i).transform;
                    bone.SetParent(i == 0 ? root.transform : bones[i - 1], false);
                    bone.position = new Vector3(0, mesh.bounds.center.y * .75f, Mathf.Lerp(front, back, i / (float)(count - 1)));
                    bones[i] = bone; binds[i] = bone.worldToLocalMatrix * root.transform.localToWorldMatrix;
                }
                var weights = new BoneWeight[vertices.Length];
                for (var i = 0; i < vertices.Length; i++)
                {
                    var t = Mathf.Clamp01((front - vertices[i].z) / (front - back)) * (count - 1);
                    var lo = Mathf.FloorToInt(t); var hi = Mathf.Min(count - 1, lo + 1);
                    weights[i] = new BoneWeight { boneIndex0 = lo, boneIndex1 = hi, weight0 = 1 - (t - lo), weight1 = t - lo };
                }
                mesh.boneWeights = weights; mesh.bindposes = binds;
                var meshPath = Folder + "/SharkSkinned.asset";
                var existing = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
                if (existing == null) AssetDatabase.CreateAsset(mesh, meshPath);
                else { EditorUtility.CopySerialized(mesh, existing); Object.DestroyImmediate(mesh); mesh = existing; }
                var skin = root.AddComponent<SkinnedMeshRenderer>(); skin.sharedMesh = mesh;
                skin.sharedMaterials = filter.GetComponent<Renderer>().sharedMaterials;
                skin.bones = bones; skin.rootBone = bones[0]; skin.updateWhenOffscreen = true;
                idle = FishClip(root.transform, bones, "SharkIdle", 1.5f, 0.55f, false);
                swim = FishClip(root.transform, bones, "SharkSwim", 0.9f, 1f, false);
                attack = FishClip(root.transform, bones, "SharkBite", 0.8f, 1.35f, true);
                var result = PrefabUtility.SaveAsPrefabAsset(root, PrefabFolder + "/SharkRig.prefab");
                Debug.Log($"[Enemies] Shark rig: {vertices.Length} vertices, {count} bones, source forward {forward}.");
                return result;
            }
            finally { Object.DestroyImmediate(root); }
        }

        private static AnimationClip FishClip(Transform root, Transform[] bones, string name, float duration,
            float strength, bool attack)
        {
            var path = Folder + "/" + name + ".anim";
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (clip == null) { clip = new AnimationClip { name = name }; AssetDatabase.CreateAsset(clip, path); }
            clip.ClearCurves(); clip.frameRate = 30;
            for (var bone = 0; bone < bones.Length; bone++)
            {
                var bonePath = AnimationUtility.CalculateTransformPath(bones[bone], root);
                var keys = new Keyframe[61];
                var amplitude = (bone == 0 ? 1.3f : Mathf.Lerp(2, 12, bone / (float)(bones.Length - 1))) * strength;
                for (var sample = 0; sample < keys.Length; sample++)
                {
                    var phase = sample / (float)(keys.Length - 1);
                    var wave = phase * Mathf.PI * 2 - bone * .65f;
                    var omega = Mathf.PI * 2 / duration;
                    keys[sample] = new Keyframe(phase * duration, Mathf.Sin(wave) * amplitude,
                        Mathf.Cos(wave) * omega * amplitude, Mathf.Cos(wave) * omega * amplitude);
                }
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(bonePath, typeof(Transform), "localEulerAnglesRaw.y"), new AnimationCurve(keys));
                foreach (var axis in new[] { "x", "z" })
                    AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(bonePath, typeof(Transform), "localEulerAnglesRaw." + axis),
                        AnimationCurve.Constant(0, duration, 0));
                if (attack && bone == 0)
                    AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(bonePath, typeof(Transform), "m_LocalPosition.z"),
                        new AnimationCurve(new Keyframe(0, bones[bone].localPosition.z),
                            new Keyframe(duration * .45f, bones[bone].localPosition.z + .2f),
                            new Keyframe(duration, bones[bone].localPosition.z)));
            }
            var settings = AnimationUtility.GetAnimationClipSettings(clip); settings.loopTime = !attack;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            EditorUtility.SetDirty(clip);
            return clip;
        }
    }
}
