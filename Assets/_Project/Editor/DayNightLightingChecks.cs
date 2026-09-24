using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using WaveByWave.Generation;
using Object = UnityEngine.Object;

namespace WaveByWave.Editor
{
    public static class DayNightLightingChecks
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        [MenuItem("Tools/Wave by Wave/Voyage/Validate day and night")]
        public static void Run()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Run lighting checks outside Play Mode.");

            var settings = Object.Instantiate(Resources.Load<VoyageMapSettings>("VoyageMapSettings"));
            var sunPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/Sun.prefab");
            var root = new GameObject("Day/night checks") { hideFlags = HideFlags.HideAndDontSave };
            root.SetActive(false);
            var controller = root.AddComponent<VoyageDayNightController>();
            var sun = CreateLight(root.transform, "Check sun", sunPrefab.transform.rotation);
            var moon = CreateLight(root.transform, "Check moon", Quaternion.identity);
            Set(controller, "settings", settings);
            Set(controller, "sun", sun);
            Set(controller, "moon", moon);
            root.SetActive(true);
            // In an empty scene Unity auto-selects the newly enabled directional light.
            var sceneSun = RenderSettings.sun;
            var sceneSkybox = RenderSettings.skybox;
            var sceneAmbient = RenderSettings.ambientSkyColor;
            try
            {
                Require(settings.DayStartHour == 10f && settings.SceneSunHour == 10f &&
                    settings.TimeOfDay == 10f && settings.SunAzimuth == 0f,
                    "The configured morning must be the scene Sun at 10:00.");
                var reference = sun.transform.rotation;
                var position = sun.transform.position;
                Apply(controller, 10f);
                Require(Quaternion.Angle(sun.transform.rotation, reference) < 0.05f,
                    "10:00 does not preserve the authored Sun rotation.");
                Require(Mathf.Abs(sun.intensity - settings.SunMaximumIntensity) < 0.001f,
                    "The morning starts with dim or missing sunlight.");

                var switches = CheckCycle(controller, sun, moon, position);
                Require(switches == 2, "Expected one sunset and one sunrise main-light handover.");

                // Reproduce the old partial-turn curve: midnight must still close the orbit.
                settings.SunRotation = AnimationCurve.Linear(0f, 0f, 1.5f, 262.94003f);
                CheckMidnight(controller, sun, moon);
                settings.SunRotation = AnimationCurve.Constant(0f, 1f, 0f);
                CheckMidnight(controller, sun, moon);
                Apply(controller, 10f);
                Require(Quaternion.Angle(sun.transform.rotation, reference) < 0.05f,
                    "Changing the orbit curve moved the scene reference.");

                Invoke(controller, "OnDisable");
                Require(Quaternion.Angle(sun.transform.rotation, reference) < 0.05f &&
                    sun.intensity == 1.25f && sun.useColorTemperature && moon.useColorTemperature,
                    "Disabling the preview failed to restore authored lights.");
                Require(RenderSettings.sun == sceneSun && RenderSettings.skybox == sceneSkybox &&
                    RenderSettings.ambientSkyColor == sceneAmbient,
                    $"Preview failed to restore the scene environment: sun {RenderSettings.sun == sceneSun}, " +
                    $"skybox {RenderSettings.skybox == sceneSkybox}, ambient {RenderSettings.ambientSkyColor} / {sceneAmbient}.");

                // A new capture must honor a subsequently edited transform, including a parent.
                root.transform.rotation = Quaternion.Euler(0f, 105f, 0f);
                sun.transform.localRotation = Quaternion.Euler(38f, 25f, 7f);
                reference = sun.transform.rotation;
                Apply(controller, 10f);
                Require(Quaternion.Angle(sun.transform.rotation, reference) < 0.05f,
                    "The edited or parented scene Sun was not recaptured.");
                Invoke(controller, "SetPhase", WaveByWave.Ships.VoyagePhase.Day);
                Require(Mathf.Abs((float)Get(controller, "_hour") - 10f) < 0.001f,
                    "A new day did not start at 10:00.");

                Debug.Log("[Day/night checks] PASS: 2,881 lighting samples, smooth sunrise/sunset, " +
                    "two dark main-light handovers, midnight wrap, malformed-curve fallback, " +
                    "scene reference at 10:00, parent rotation and preview restoration.");
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(settings);
            }
        }

        private static int CheckCycle(VoyageDayNightController controller, Light sun, Light moon,
            Vector3 position)
        {
            Apply(controller, 0f);
            var previousDirection = sun.transform.forward;
            var previousSun = sun.intensity;
            var previousMoon = moon.intensity;
            var previousAmbient = RenderSettings.ambientSkyColor;
            var previousMain = RenderSettings.sun;
            var switches = 0;
            for (var i = 1; i <= 2880; i++)
            {
                var hour = i / 120f;
                Apply(controller, hour);
                Require(Vector3.Angle(previousDirection, sun.transform.forward) < 0.2f,
                    $"Sun direction jumped at {hour:F4}.");
                Require(Mathf.Abs(previousSun - sun.intensity) < 0.03f &&
                    Mathf.Abs(previousMoon - moon.intensity) < 0.03f,
                    $"Direct lighting jumped at {hour:F4}.");
                Require(ColorDistance(previousAmbient, RenderSettings.ambientSkyColor) < 0.03f,
                    $"Ambient lighting jumped at {hour:F4}.");
                Require(sun.transform.position == position, "The controller moved the Sun position.");
                Require(Vector3.Dot(sun.transform.forward, moon.transform.forward) < -0.999f,
                    "Sun and moon are no longer opposite.");
                Require(sun.transform.forward.y <= 0f || sun.intensity < 0.00001f,
                    $"The Sun lights the scene from below the horizon at {hour:F4}.");
                Require(moon.transform.forward.y <= 0f || moon.intensity < 0.00001f,
                    $"The Moon lights the scene from below the horizon at {hour:F4}.");
                if (previousMain != RenderSettings.sun)
                {
                    switches++;
                    Require(Mathf.Max(previousSun, previousMoon, sun.intensity, moon.intensity) < 0.005f,
                        $"Main light switched while still bright at {hour:F4}.");
                }
                previousMain = RenderSettings.sun;
                previousDirection = sun.transform.forward;
                previousSun = sun.intensity;
                previousMoon = moon.intensity;
                previousAmbient = RenderSettings.ambientSkyColor;
            }
            CheckMidnight(controller, sun, moon);
            return switches;
        }

        private static void CheckMidnight(VoyageDayNightController controller, Light sun, Light moon)
        {
            Apply(controller, 23.9999f);
            var direction = sun.transform.forward;
            var intensity = moon.intensity;
            Apply(controller, 0f);
            Require(Vector3.Distance(direction, sun.transform.forward) < 0.0001f &&
                Mathf.Abs(moon.intensity - intensity) < 0.001f, "Lighting jumps across midnight.");
        }

        private static Light CreateLight(Transform parent, string name, Quaternion rotation)
        {
            var go = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
            go.transform.SetParent(parent);
            go.transform.rotation = rotation;
            var light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.25f;
            light.useColorTemperature = true;
            return light;
        }

        private static float ColorDistance(Color a, Color b) =>
            Mathf.Max(Mathf.Abs(a.r - b.r), Mathf.Abs(a.g - b.g), Mathf.Abs(a.b - b.b));

        private static void Apply(VoyageDayNightController controller, float hour) =>
            Invoke(controller, "ApplyLighting", hour);

        private static void Set(object target, string field, object value) =>
            target.GetType().GetField(field, PrivateInstance).SetValue(target, value);

        private static object Get(object target, string field) =>
            target.GetType().GetField(field, PrivateInstance).GetValue(target);

        private static void Invoke(object target, string method, params object[] arguments) =>
            target.GetType().GetMethod(method, PrivateInstance).Invoke(target, arguments);

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
