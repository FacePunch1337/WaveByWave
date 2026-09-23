using UnityEngine;

namespace WaveByWave.Generation
{
    [CreateAssetMenu(menuName = "Wave By Wave/Voyage/Day and night", fileName = "VoyageMapSettings")]
    public sealed class VoyageMapSettings : ScriptableObject
    {
        [Header("Clock")]
        [Min(5f)] public float DayDuration = 300f;
        [Min(1f)] public float SunsetDuration = 20f;
        [Tooltip("Time for the night sky to reach dawn. The night itself waits for the wave to be defeated.")]
        [Min(1f)] public float NightDuration = 120f;
        [Min(1f)] public float SunriseDuration = 20f;

        [Header("Time scrubbing")]
        [Tooltip("In Edit Mode previews lighting. In Play Mode pauses the automatic clock and sets the actual networked game time on the host.")]
        public bool OverrideTime;
        [Tooltip("Drag in the asset Inspector to rewind or fast-forward time; 0/24 = midnight, 8 = morning, 20 = night.")]
        [Range(0f, 24f)] public float TimeOfDay = 8f;

        [Header("Sun and moon")]
        public float SunAzimuth = -50f;
        [Min(0f)] public float SunMaximumIntensity = 1.25f;
        [Min(0f)] public float MoonMaximumIntensity = 0.35f;
        [Tooltip("Gradient time is hour / 24; changing it updates lighting immediately.")]
        public Gradient SunColor = CreateSunColor();
        public Color MoonColor = new(0.48f, 0.62f, 1f);
        [Tooltip("Curve X is hour / 24; Y is the sun's elevation angle in degrees.")]
        public AnimationCurve SunRotation = AnimationCurve.Linear(0f, -90f, 1f, 270f);
        [Tooltip("Curve X is hour / 24; Y multiplies Sun Maximum Intensity.")]
        public AnimationCurve SunIntensity = new(
            new Keyframe(0f, 0f), new Keyframe(6f / 24f, 0f),
            new Keyframe(8f / 24f, 0.3f), new Keyframe(12f / 24f, 1f),
            new Keyframe(17f / 24f, 0.35f), new Keyframe(20f / 24f, 0f),
            new Keyframe(1f, 0f));

        [Header("Smooth skybox transition")]
        [Tooltip("Day and night materials must use the same cubemap skybox shader as the blend material.")]
        public Material DaySkybox;
        public Material NightSkybox;
        public Material BlendedSkybox;
        [Tooltip("Curve X is hour / 24. Blend 0 = day cubemap, 1 = night cubemap.")]
        public AnimationCurve SkyboxBlend = new(
            new Keyframe(0f, 1f), new Keyframe(6f / 24f, 1f),
            new Keyframe(8f / 24f, 0f), new Keyframe(17f / 24f, 0f),
            new Keyframe(20f / 24f, 1f), new Keyframe(1f, 1f));
        [Tooltip("Day cubemap tint multiplier over 24 hours; white keeps its original colors.")]
        public Gradient DaySkyboxTint = CreateWhiteTint();

        [Header("Procedural sun and moon")]
        [Tooltip("Project skybox shader that adds sun and moon discs to the selected cubemaps. Source cubemap materials are not modified.")]
        public Shader CelestialSkyboxShader;
        public bool ShowSun = true;
        [Tooltip("Optional sun sprite. If empty, the skybox material's sprite settings are used; without one a procedural disc is drawn.")]
        public Sprite SunSprite;
        [Range(0.1f, 10f)] public float SunAngularRadius = 1.5f;
        [Range(0.01f, 1f)] public float SunEdgeSoftness = 0.08f;
        [Range(0f, 20f)] public float SunHaloRadius = 4f;
        [Range(0f, 2f)] public float SunHaloStrength = 0.18f;
        [Min(0f)] public float SunDiskIntensity = 2f;
        [ColorUsage(true, true)] public Color SunDiskColor = new(1f, 0.9f, 0.65f);
        public bool ShowMoon = true;
        [Tooltip("Optional moon sprite. If empty, the skybox material's sprite settings are used; without one a procedural disc is drawn.")]
        public Sprite MoonSprite;
        [Range(0.1f, 10f)] public float MoonAngularRadius = 1.1f;
        [Range(0.01f, 1f)] public float MoonEdgeSoftness = 0.06f;
        [Range(0f, 20f)] public float MoonHaloRadius = 2f;
        [Range(0f, 2f)] public float MoonHaloStrength = 0.12f;
        [Min(0f)] public float MoonDiskIntensity = 1.25f;
        [ColorUsage(true, true)] public Color MoonDiskColor = new(0.78f, 0.88f, 1f);

        [Header("Environment")]
        public Color DayAmbient = new(0.35f, 0.42f, 0.5f);
        public Color NightAmbient = new(0.04f, 0.06f, 0.11f);
        public Color DayEquator = new(0.28f, 0.34f, 0.42f);
        public Color NightEquator = new(0.02f, 0.025f, 0.045f);
        public Color DayGround = new(0.18f, 0.22f, 0.27f);
        public Color NightGround = new(0.01f, 0.012f, 0.02f);
        public Color DayFog = new(0.56f, 0.66f, 0.73f);
        public Color NightFog = new(0.04f, 0.07f, 0.12f);
        [Range(0f, 1f)] public float NightReflectionIntensity = 0.15f;

        private void OnEnable()
        {
            SunColor ??= CreateSunColor();
            DaySkyboxTint ??= CreateWhiteTint();
            SunRotation ??= AnimationCurve.Linear(0f, -90f, 1f, 270f);
            SunIntensity ??= new AnimationCurve(new Keyframe(0f, 0f),
                new Keyframe(0.5f, 1f), new Keyframe(1f, 0f));
            SkyboxBlend ??= new AnimationCurve(new Keyframe(0f, 1f),
                new Keyframe(0.5f, 0f), new Keyframe(1f, 1f));
        }

        private static Gradient CreateSunColor()
        {
            var gradient = new Gradient();
            gradient.SetKeys(new[]
            {
                new GradientColorKey(new Color(1f, 0.57f, 0.3f), 0f),
                new GradientColorKey(new Color(1f, 0.57f, 0.3f), 8f / 24f),
                new GradientColorKey(new Color(1f, 0.94f, 0.8f), 12f / 24f),
                new GradientColorKey(new Color(1f, 0.46f, 0.27f), 18f / 24f),
                new GradientColorKey(new Color(1f, 0.46f, 0.27f), 1f)
            }, new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            return gradient;
        }

        private static Gradient CreateWhiteTint()
        {
            var gradient = new Gradient();
            gradient.SetKeys(new[]
            {
                new GradientColorKey(Color.white, 0f),
                new GradientColorKey(Color.white, 1f)
            }, new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            return gradient;
        }
    }
}
