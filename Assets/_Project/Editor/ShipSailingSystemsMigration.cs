using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using WaveByWave.Ships;
using Object = UnityEngine.Object;

namespace WaveByWave.Editor
{
    public static class ShipSailingSystemsMigration
    {
        private const string ShipPrefabPath = "Assets/_Project/Prefabs/Ship.prefab";

        [MenuItem("Tools/Wave by Wave/Upgrade Ship Sailing Systems")]
        public static void UpgradeIfRequired()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
                EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
                return;
            }

            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += UpgradeIfRequired;
                return;
            }

            var root = PrefabUtility.LoadPrefabContents(ShipPrefabPath);
            if (root == null)
                return;

            var changed = false;
            try
            {
                var ship = root.GetComponent<NetworkShipController>();
                if (ship == null)
                    return;

                var body = root.GetComponent<Rigidbody>();
                if (body != null && body.collisionDetectionMode != CollisionDetectionMode.Discrete)
                {
                    body.collisionDetectionMode = CollisionDetectionMode.Discrete;
                    changed = true;
                }

                var wind = root.GetComponent<NetworkWindController>();
                if (wind == null)
                {
                    wind = root.AddComponent<NetworkWindController>();
                    changed = true;
                }

                var helm = root.GetComponentInChildren<ShipHelm>(true);
                var helmPosition = helm != null ? helm.transform.localPosition : new Vector3(0f, 2f, -3.8f);
                var gold = AssetDatabase.LoadAssetAtPath<Material>("Assets/_Project/Generated/Materials/Gold.mat");
                var sailMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/_Project/Generated/Materials/Sail.mat");
                var darkWood = AssetDatabase.LoadAssetAtPath<Material>("Assets/_Project/Generated/Materials/DarkWood.mat");

                if (helm != null)
                {
                    var serializedHelm = new SerializedObject(helm);
                    var wheelVisualProperty = serializedHelm.FindProperty("wheelVisual");
                    if (wheelVisualProperty != null && wheelVisualProperty.objectReferenceValue == null)
                    {
                        var wheelVisual = root.GetComponentsInChildren<MeshRenderer>(true)
                            .Where(renderer => renderer.name.IndexOf("WheelStand", StringComparison.OrdinalIgnoreCase) < 0)
                            .OrderByDescending(renderer =>
                                renderer.name.Equals("StylShip_Wheel", StringComparison.OrdinalIgnoreCase))
                            .ThenByDescending(renderer =>
                                renderer.name.Equals("Wheel", StringComparison.OrdinalIgnoreCase))
                            .FirstOrDefault(renderer =>
                                renderer.name.IndexOf("Wheel", StringComparison.OrdinalIgnoreCase) >= 0);
                        if (wheelVisual != null)
                        {
                            wheelVisualProperty.objectReferenceValue = wheelVisual.transform;
                            var axisProperty = serializedHelm.FindProperty("localRotationAxis");
                            if (axisProperty != null)
                                axisProperty.vector3Value = DetermineWheelAxis(wheelVisual);
                            serializedHelm.ApplyModifiedPropertiesWithoutUndo();
                            changed = true;
                        }
                    }
                }

                var anchorPoint = root.transform.Find("Anchor Interaction");
                if (anchorPoint == null)
                {
                    anchorPoint = CreateInteractionPoint("Anchor Interaction", root.transform,
                        helmPosition + new Vector3(-2f, 0f, 0f), gold).transform;
                    changed = true;
                }
                var anchor = anchorPoint.GetComponent<ShipAnchor>();
                if (anchor == null)
                {
                    anchor = anchorPoint.gameObject.AddComponent<ShipAnchor>();
                    changed = true;
                }
                changed |= SetReference(anchor, "ship", ship);
                changed |= SetReference(anchor, "indicatorRenderer", anchorPoint.GetComponent<Renderer>());

                var sailPoint = root.transform.Find("Sail Control");
                if (sailPoint == null)
                {
                    sailPoint = CreateInteractionPoint("Sail Control", root.transform,
                        helmPosition + new Vector3(2f, 0f, 0f), gold).transform;
                    changed = true;
                }
                var station = sailPoint.Find("Station");
                if (station == null)
                {
                    station = new GameObject("Station").transform;
                    station.SetParent(sailPoint, false);
                    station.localPosition = new Vector3(0f, -1f, -1.2f);
                    station.localRotation = Quaternion.Euler(0f, 180f, 0f);
                    changed = true;
                }
                var sailControl = sailPoint.GetComponent<ShipSailControl>();
                if (sailControl == null)
                {
                    sailControl = sailPoint.gameObject.AddComponent<ShipSailControl>();
                    changed = true;
                }
                changed |= SetReference(sailControl, "ship", ship);
                changed |= SetReference(sailControl, "station", station);
                changed |= SetReference(sailControl, "indicatorRenderer", sailPoint.GetComponent<Renderer>());

                var serializedSail = new SerializedObject(sailControl);
                var sailVisuals = serializedSail.FindProperty("sailVisuals");
                var hasSailVisual = Enumerable.Range(0, sailVisuals.arraySize)
                    .Any(index => sailVisuals.GetArrayElementAtIndex(index).objectReferenceValue != null);
                if (!hasSailVisual)
                {
                    var candidates = root.GetComponentsInChildren<MeshRenderer>(true)
                        .Where(renderer => renderer.name.IndexOf("Sail", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                           !renderer.transform.IsChildOf(sailPoint))
                        .Select(renderer => renderer.transform)
                        .Distinct()
                        .ToArray();
                    sailVisuals.arraySize = candidates.Length;
                    for (var i = 0; i < candidates.Length; i++)
                        sailVisuals.GetArrayElementAtIndex(i).objectReferenceValue = candidates[i];
                    serializedSail.ApplyModifiedPropertiesWithoutUndo();
                    changed = true;
                }

                serializedSail.Update();
                var windReference = serializedSail.FindProperty("windReference");
                if (windReference != null && windReference.objectReferenceValue == null)
                {
                    for (var i = 0; i < sailVisuals.arraySize; i++)
                    {
                        var sailVisual = sailVisuals.GetArrayElementAtIndex(i).objectReferenceValue as Transform;
                        if (sailVisual == null)
                            continue;

                        windReference.objectReferenceValue = sailVisual;
                        serializedSail.ApplyModifiedPropertiesWithoutUndo();
                        changed = true;
                        break;
                    }
                }

                var allTransforms = root.GetComponentsInChildren<Transform>(true);
                var mastPivot = allTransforms.FirstOrDefault(candidate =>
                    candidate.name.Equals("Mast Pivot", StringComparison.OrdinalIgnoreCase));
                mastPivot ??= allTransforms.FirstOrDefault(candidate =>
                    candidate.gameObject.activeInHierarchy &&
                    candidate.name.IndexOf("Mast", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    candidate.GetComponentsInChildren<MeshRenderer>(true).Any(renderer =>
                        renderer.name.IndexOf("Sail", StringComparison.OrdinalIgnoreCase) >= 0));
                mastPivot ??= allTransforms.FirstOrDefault(candidate =>
                    candidate.name.Equals("Sails", StringComparison.OrdinalIgnoreCase));
                if (mastPivot == null)
                {
                    var firstSail = root.GetComponentsInChildren<MeshRenderer>(true)
                        .FirstOrDefault(renderer =>
                            renderer.gameObject.activeInHierarchy &&
                            renderer.name.IndexOf("Sail", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            !renderer.transform.IsChildOf(sailPoint));
                    mastPivot = firstSail != null ? firstSail.transform.parent : null;
                }

                var mastPoint = root.transform.Find("Mast Control");
                if (mastPoint == null)
                {
                    mastPoint = CreateInteractionPoint("Mast Control", root.transform,
                        helmPosition + new Vector3(4f, 0f, 0f), gold).transform;
                    changed = true;
                }
                var mastStation = mastPoint.Find("Station");
                if (mastStation == null)
                {
                    mastStation = new GameObject("Station").transform;
                    mastStation.SetParent(mastPoint, false);
                    mastStation.localPosition = new Vector3(0f, -1f, -1.2f);
                    mastStation.localRotation = Quaternion.Euler(0f, 180f, 0f);
                    changed = true;
                }
                var mastControl = mastPoint.GetComponent<ShipMastControl>();
                if (mastControl == null)
                {
                    mastControl = mastPoint.gameObject.AddComponent<ShipMastControl>();
                    changed = true;
                }
                changed |= SetReference(mastControl, "ship", ship);
                changed |= SetReference(mastControl, "station", mastStation);
                changed |= SetReference(mastControl, "indicatorRenderer", mastPoint.GetComponent<Renderer>());
                var serializedMast = new SerializedObject(mastControl);
                var mastPivotProperty = serializedMast.FindProperty("mastPivot");
                if (mastPivotProperty != null && mastPivotProperty.objectReferenceValue == null)
                {
                    if (mastPivot != null)
                    {
                        mastPivotProperty.objectReferenceValue = mastPivot;
                        serializedMast.ApplyModifiedPropertiesWithoutUndo();
                        changed = true;
                    }
                    else
                    {
                        Debug.LogWarning("[Wave by Wave] Could not find a safe mast pivot. Assign Mast Control/Mast Pivot manually.");
                    }
                }

                var flagRoot = root.transform.Find("Wind Flag");
                if (flagRoot == null)
                {
                    flagRoot = new GameObject("Wind Flag").transform;
                    flagRoot.SetParent(root.transform, false);
                    flagRoot.localPosition = helmPosition + new Vector3(0f, 3f, 2f);
                    CreateCube("Pole", flagRoot, new Vector3(0f, 0.75f, 0f),
                        new Vector3(0.08f, 1.5f, 0.08f), darkWood);
                    CreateCube("Flag", flagRoot, new Vector3(0f, 1.2f, 0.55f),
                        new Vector3(0.08f, 0.5f, 1.1f), sailMaterial);
                    changed = true;
                }
                var directionFlag = flagRoot.GetComponent<WindDirectionFlag>();
                if (directionFlag == null)
                {
                    directionFlag = flagRoot.gameObject.AddComponent<WindDirectionFlag>();
                    changed = true;
                }
                changed |= SetReference(directionFlag, "wind", wind);
                changed |= SetReference(directionFlag, "directionPivot", flagRoot);

                changed |= SetReference(ship, "wind", wind);
                changed |= SetReference(ship, "anchor", anchor);
                changed |= SetReference(ship, "sailControl", sailControl);
                changed |= SetReference(ship, "mastControl", mastControl);
                changed |= SetReference(ship, "helm", helm);

                if (changed)
                {
                    PrefabUtility.SaveAsPrefabAsset(root, ShipPrefabPath);
                    Debug.Log("[Wave by Wave] Ship upgraded with anchor, sails, mast control, wind flag, and compound collision support.");
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredEditMode)
                return;

            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.delayCall += UpgradeIfRequired;
        }

        private static GameObject CreateInteractionPoint(string name, Transform parent, Vector3 localPosition,
            Material material)
        {
            var point = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            point.name = name;
            point.transform.SetParent(parent, false);
            point.transform.localPosition = localPosition;
            point.transform.localScale = Vector3.one * 0.5f;
            point.GetComponent<MeshRenderer>().sharedMaterial = material;
            var collider = point.GetComponent<SphereCollider>();
            collider.isTrigger = true;
            collider.radius = 1.2f;
            return point;
        }

        private static void CreateCube(string name, Transform parent, Vector3 localPosition, Vector3 localScale,
            Material material)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = name;
            cube.transform.SetParent(parent, false);
            cube.transform.localPosition = localPosition;
            cube.transform.localScale = localScale;
            cube.GetComponent<MeshRenderer>().sharedMaterial = material;
            Object.DestroyImmediate(cube.GetComponent<Collider>());
        }

        private static Vector3 DetermineWheelAxis(MeshRenderer wheelRenderer)
        {
            var meshFilter = wheelRenderer != null ? wheelRenderer.GetComponent<MeshFilter>() : null;
            if (meshFilter == null || meshFilter.sharedMesh == null)
                return Vector3.forward;

            var boundsSize = meshFilter.sharedMesh.bounds.size;
            var scale = wheelRenderer.transform.localScale;
            var scaledSize = Vector3.Scale(boundsSize,
                new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
            if (scaledSize.x <= scaledSize.y && scaledSize.x <= scaledSize.z)
                return Vector3.right;
            if (scaledSize.y <= scaledSize.x && scaledSize.y <= scaledSize.z)
                return Vector3.up;
            return Vector3.forward;
        }

        private static bool SetReference(UnityEngine.Object target, string propertyName, UnityEngine.Object value)
        {
            var serialized = new SerializedObject(target);
            var property = serialized.FindProperty(propertyName);
            if (property == null || property.objectReferenceValue == value)
                return false;

            property.objectReferenceValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return true;
        }
    }
}
