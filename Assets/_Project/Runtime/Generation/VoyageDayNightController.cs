using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;
using WaveByWave.Ships;

namespace WaveByWave.Generation
{
    // The scene clock and lighting do not own wave spawning or map completion.
    [ExecuteAlways]
    public sealed class VoyageDayNightController : MonoBehaviour
    {
        [SerializeField] private VoyageMapSettings settings;
        [SerializeField] private Light sun;
        [SerializeField] private Light moon;
        [SerializeField] private DayNightMaterialController nightMaterials;

        public event Action NightStartedServer;

        private ShipCannonBattery _battery;
        private VoyagePhase _phase;
        private float _elapsed, _hour = 10f, _sunriseFromHour = 6f, _nextNetworkUpdate;
        private float _displayHour = 10f;
        private bool _displayInitialized;
        private bool _started, _advanceWhenManualEnds, _wasOverridden, _clockPaused;

        private Material _runtimeSkybox, _skyboxSource;
        private Shader _celestialShaderSource;
        private Material _initialSkybox;
        private Light _initialRenderSun;
        private AmbientMode _initialAmbientMode;
        private Color _initialAmbientSky, _initialAmbientEquator, _initialAmbientGround, _initialFog;
        private float _initialReflection;
        private bool _initialSunEnabled, _initialMoonEnabled, _initialSunColorTemperature, _initialMoonColorTemperature;
        private bool _lightingCaptured, _lightingApplied;
        private float _initialSunIntensity, _initialMoonIntensity;
        private Color _initialSunColor, _initialMoonColor;
        private Quaternion _initialSunRotation, _initialMoonRotation;
        private float DayStartHour => Mathf.Clamp(settings.DayStartHour, 8f, 18f);


        private static readonly string[] SkyboxFloatProperties =
        {
            "_CubemapPosition", "_FogHeight", "_FogSmoothness", "_FogFill",
            "_FogIntensity", "_FogPosition"
        };

        private void OnEnable()
        {
            settings ??= Resources.Load<VoyageMapSettings>("VoyageMapSettings");
            if (sun == null) sun = RenderSettings.sun;
        }

        private void OnDisable()
        {
            RestoreLighting();
            if (_runtimeSkybox == null) return;
            if (Application.IsPlaying(gameObject)) Destroy(_runtimeSkybox);
            else DestroyImmediate(_runtimeSkybox);
            _runtimeSkybox = null;
            _skyboxSource = null;
            _celestialShaderSource = null;
        }

        private void CaptureLighting()
        {
            _lightingCaptured = true;
            _initialSkybox = RenderSettings.skybox;
            _initialRenderSun = RenderSettings.sun;
            _initialAmbientMode = RenderSettings.ambientMode;
            _initialAmbientSky = RenderSettings.ambientSkyColor;
            _initialAmbientEquator = RenderSettings.ambientEquatorColor;
            _initialAmbientGround = RenderSettings.ambientGroundColor;
            _initialFog = RenderSettings.fogColor;
            _initialReflection = RenderSettings.reflectionIntensity;
            if (sun != null)
            {
                _initialSunEnabled = sun.enabled;
                _initialSunColorTemperature = sun.useColorTemperature;
                _initialSunIntensity = sun.intensity;
                _initialSunColor = sun.color;
                _initialSunRotation = sun.transform.rotation;
            }
            if (moon != null)
            {
                _initialMoonEnabled = moon.enabled;
                _initialMoonColorTemperature = moon.useColorTemperature;
                _initialMoonIntensity = moon.intensity;
                _initialMoonColor = moon.color;
                _initialMoonRotation = moon.transform.rotation;
            }
        }

        private void RestoreLighting()
        {
            if (!_lightingApplied) return;
            _lightingApplied = false;
            _lightingCaptured = false;
            if (sun != null)
            {
                sun.enabled = _initialSunEnabled;
                sun.useColorTemperature = _initialSunColorTemperature;
                sun.intensity = _initialSunIntensity;
                sun.color = _initialSunColor;
                sun.transform.rotation = _initialSunRotation;
            }
            if (moon != null)
            {
                moon.enabled = _initialMoonEnabled;
                moon.useColorTemperature = _initialMoonColorTemperature;
                moon.intensity = _initialMoonIntensity;
                moon.color = _initialMoonColor;
                moon.transform.rotation = _initialMoonRotation;
            }
            RenderSettings.skybox = _initialSkybox;
            RenderSettings.sun = _initialRenderSun;
            RenderSettings.ambientMode = _initialAmbientMode;
            RenderSettings.ambientSkyColor = _initialAmbientSky;
            RenderSettings.ambientEquatorColor = _initialAmbientEquator;
            RenderSettings.ambientGroundColor = _initialAmbientGround;
            RenderSettings.fogColor = _initialFog;
            RenderSettings.reflectionIntensity = _initialReflection;
            if (nightMaterials != null) nightMaterials.RestoreMaterials();
        }

#if UNITY_EDITOR
        public void RestoreEditorPreview()
        {
            if (!Application.IsPlaying(gameObject)) RestoreLighting();
        }
#endif

        private void Update()
        {
            if (settings == null) return;
            if (!Application.IsPlaying(gameObject))
            {
                if (settings.OverrideTime) ApplyLighting(settings.TimeOfDay);
                else RestoreLighting();
                return;
            }
            if (_battery == null || !_battery.IsSpawned)
                _battery = FindFirstObjectByType<ShipCannonBattery>();
            var manager = NetworkManager.Singleton;
            var clockAuthority = manager == null || manager.IsServer;
            if (clockAuthority) ServerUpdate();

            var targetHour = _battery != null && _battery.IsSpawned ? _battery.TimeOfDay : _hour;
            if (!_displayInitialized)
            {
                _displayHour = targetHour;
                _displayInitialized = true;
            }
            if (clockAuthority) _displayHour = _hour;
            else _displayHour = Mathf.Repeat(Mathf.LerpAngle(_displayHour * 15f,
                targetHour * 15f, Mathf.Clamp01(Time.unscaledDeltaTime * 12f)) / 15f, 24f);
            ApplyLighting(_displayHour);
        }

        private void ServerUpdate()
        {
            if (!_started)
            {
                _started = true;
                SetPhase(VoyagePhase.Day);
            }
            if (_clockPaused) return;
            if (settings.OverrideTime)
            {
                _wasOverridden = true;
                ScrubServer(settings.TimeOfDay);
                PublishClockServer();
                return;
            }
            if (_wasOverridden)
            {
                _wasOverridden = false;
                if (_advanceWhenManualEnds)
                {
                    _advanceWhenManualEnds = false;
                    SetPhase(VoyagePhase.Sunrise);
                }
            }
            if (_battery != null && _battery.IsSpawned && _battery.UpgradePaused) return;
            _elapsed += Time.deltaTime;
            switch (_phase)
            {
                case VoyagePhase.Day:
                    _hour = Mathf.Lerp(DayStartHour, 18f,
                        Mathf.Clamp01(_elapsed / Mathf.Max(1f, settings.DayDuration)));
                    if (_elapsed >= settings.DayDuration) SetPhase(VoyagePhase.Sunset);
                    break;
                case VoyagePhase.Sunset:
                    _hour = Mathf.Lerp(18f, 20f, Mathf.Clamp01(_elapsed / Mathf.Max(1f, settings.SunsetDuration)));
                    if (_elapsed >= settings.SunsetDuration) SetPhase(VoyagePhase.Night);
                    break;
                case VoyagePhase.Night:
                    _elapsed = Mathf.Min(_elapsed, Mathf.Max(1f, settings.NightDuration));
                    _hour = Mathf.Repeat(20f + 10f * Mathf.Clamp01(
                        _elapsed / Mathf.Max(1f, settings.NightDuration)), 24f);
                    break;
                case VoyagePhase.Sunrise:
                    var endHour = _sunriseFromHour > DayStartHour
                        ? DayStartHour + 24f : DayStartHour;
                    _hour = Mathf.Repeat(Mathf.Lerp(_sunriseFromHour, endHour,
                        Mathf.Clamp01(_elapsed / Mathf.Max(1f, settings.SunriseDuration))), 24f);
                    if (_elapsed >= settings.SunriseDuration) SetPhase(VoyagePhase.Day);
                    break;
            }
            if (Time.time >= _nextNetworkUpdate) PublishClockServer();
        }

        private void ScrubServer(float requestedHour)
        {
            var selectedHour = Mathf.Repeat(requestedHour, 24f);
            _hour = selectedHour;
            var phase = _hour >= 20f || _hour < 6f ? VoyagePhase.Night :
                _hour < DayStartHour ? VoyagePhase.Sunrise :
                _hour < 18f ? VoyagePhase.Day : VoyagePhase.Sunset;
            if (_phase != phase) SetPhase(phase);
            _hour = selectedHour;
            if (phase == VoyagePhase.Sunrise) _sunriseFromHour = 6f;
            var normalized = phase switch
            {
                VoyagePhase.Day => (_hour - DayStartHour) / Mathf.Max(0.01f, 18f - DayStartHour),
                VoyagePhase.Sunset => (_hour - 18f) / 2f,
                VoyagePhase.Night => (_hour >= 20f ? _hour - 20f : _hour + 4f) / 10f,
                _ => (_hour - 6f) / Mathf.Max(0.01f, DayStartHour - 6f)
            };
            _elapsed = normalized * PhaseDuration(phase);
        }

        private float PhaseDuration(VoyagePhase phase) => phase switch
        {
            VoyagePhase.Day => settings.DayDuration,
            VoyagePhase.Sunset => settings.SunsetDuration,
            VoyagePhase.Night => settings.NightDuration,
            VoyagePhase.Sunrise => settings.SunriseDuration,
            _ => 1f
        };

        private void SetPhase(VoyagePhase phase)
        {
            if (phase == VoyagePhase.Sunrise) _sunriseFromHour = _hour;
            _phase = phase;
            _elapsed = 0f;
            _nextNetworkUpdate = 0f;
            if (phase == VoyagePhase.Day) _hour = DayStartHour;
            else if (phase == VoyagePhase.Sunset) _hour = 18f;
            else if (phase == VoyagePhase.Night) _hour = 20f;
            PublishClockServer();
            if (phase == VoyagePhase.Night) NightStartedServer?.Invoke();
        }

        private void PublishClockServer()
        {
            if (_battery == null || !_battery.IsSpawned) return;
            _battery.SetVoyageClockServer(_phase,
                _phase == VoyagePhase.Day
                    ? Mathf.Clamp01(_elapsed / Mathf.Max(1f, settings.DayDuration))
                    : _phase == VoyagePhase.Sunrise ? 0f : 1f, _hour);
            _nextNetworkUpdate = Time.time + 0.1f;
        }

        public void ReleaseNightServer()
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;
            if (_phase != VoyagePhase.Night) return;
            if (settings.OverrideTime) _advanceWhenManualEnds = true;
            else SetPhase(VoyagePhase.Sunrise);
        }

        public void PauseClockServer()
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;
            _clockPaused = true;
        }

        private void ApplyLighting(float hour)
        {
            if (!_lightingCaptured) CaptureLighting();
            _lightingApplied = true;
            var time = Mathf.Repeat(hour, 24f) / 24f;
            var night = Mathf.Clamp01(settings.SkyboxBlend?.Evaluate(time) ?? 0f);
            var tint = settings.DaySkyboxTint?.Evaluate(time) ?? Color.white;
            var referenceTime = Mathf.Repeat(settings.SceneSunHour, 24f) / 24f;
            var orbit = EvaluateSunOrbit(time) - EvaluateSunOrbit(referenceTime);
            var referenceRotation = sun != null ? _initialSunRotation : Quaternion.Euler(60f, 0f, 0f);
            var sunRotation = Quaternion.AngleAxis(settings.SunAzimuth, Vector3.up) *
                referenceRotation * Quaternion.Euler(orbit, 0f, 0f);
            var sunAltitude = (sunRotation * Vector3.back).y;
            if (sun != null)
            {
                sun.useColorTemperature = false;
                sun.transform.rotation = sunRotation;
                sun.intensity = settings.SunMaximumIntensity * Mathf.Max(0f,
                    settings.SunIntensity?.Evaluate(time) ?? 0f) * HorizonLight(sunAltitude);
                sun.color = settings.SunColor?.Evaluate(time) ?? Color.white;
                sun.enabled = true;
            }
            if (moon != null)
            {
                moon.transform.rotation = sunRotation * Quaternion.Euler(180f, 0f, 0f);
                moon.useColorTemperature = false;
                moon.color = settings.MoonColor;
                moon.intensity = settings.MoonMaximumIntensity * night * HorizonLight(-sunAltitude);
                moon.enabled = true;
            }
            // URP's main light also drives water highlights and shadows. Hand over only
            // at the horizon, where both opposing lights have faded to zero.
            RenderSettings.sun = moon != null && (sun == null || sunAltitude < 0f) ? moon : sun;
            UpdateSkybox(night, tint);
            var ambientSky = Color.Lerp(settings.DayAmbient, settings.NightAmbient, night);
            var ambientEquator = Color.Lerp(settings.DayEquator, settings.NightEquator, night);
            var ambientGround = Color.Lerp(settings.DayGround, settings.NightGround, night);
            RenderSettings.ambientSkyColor = ambientSky;
            RenderSettings.ambientEquatorColor = ambientEquator;
            RenderSettings.ambientGroundColor = ambientGround;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.fogColor = Color.Lerp(settings.DayFog * tint, settings.NightFog, night);
            RenderSettings.reflectionIntensity = Mathf.Lerp(_initialReflection,
                settings.NightReflectionIntensity, night);
            if (nightMaterials != null && nightMaterials.isActiveAndEnabled)
                nightMaterials.ApplyNight(night);
        }

        private float EvaluateSunOrbit(float time)
        {
            var curve = settings.SunRotation;
            if (curve == null || curve.length < 2) return time * 360f;
            var start = curve.Evaluate(0f);
            var span = curve.Evaluate(1f) - start;
            // A partial turn in the authored curve used to teleport the sun at midnight.
            return Mathf.Abs(span) < 0.001f ? time * 360f :
                (curve.Evaluate(time) - start) * (360f / span);
        }

        private static float HorizonLight(float altitude) =>
            Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, 0.1f, altitude));

        private void UpdateSkybox(float night, Color tint)
        {
            var day = settings.DaySkybox;
            var dark = settings.NightSkybox;
            var blend = settings.BlendedSkybox;
            if (day == null || dark == null || blend == null || !day.HasProperty("_Tex") ||
                !dark.HasProperty("_Tex") || !blend.HasProperty("_CubemapTransition")) return;
            var celestialShader = settings.CelestialSkyboxShader;
            if (_runtimeSkybox == null || _skyboxSource != blend ||
                _celestialShaderSource != celestialShader)
            {
                if (_runtimeSkybox != null)
                {
                    if (Application.IsPlaying(gameObject)) Destroy(_runtimeSkybox);
                    else DestroyImmediate(_runtimeSkybox);
                }
                _skyboxSource = blend;
                _celestialShaderSource = celestialShader;
                _runtimeSkybox = new Material(blend)
                {
                    name = blend.name + " (day/night runtime)",
                    hideFlags = HideFlags.HideAndDontSave
                };
                if (celestialShader != null) _runtimeSkybox.shader = celestialShader;
            }
            _runtimeSkybox.SetTexture("_Tex", day.GetTexture("_Tex"));
            _runtimeSkybox.SetTexture("_Tex_Blend", dark.GetTexture("_Tex"));
            if (day.HasProperty("_Tex_HDR"))
                _runtimeSkybox.SetVector("_Tex_HDR", day.GetVector("_Tex_HDR"));
            if (dark.HasProperty("_Tex_HDR"))
                _runtimeSkybox.SetVector("_Tex_Blend_HDR", dark.GetVector("_Tex_HDR"));
            if (blend.HasProperty("_EnableFog") && day.HasProperty("_EnableFog"))
            {
                var fog = day.GetFloat("_EnableFog") > 0f;
                _runtimeSkybox.SetFloat("_EnableFog", fog ? 1f : 0f);
                if (fog) _runtimeSkybox.EnableKeyword("_ENABLEFOG_ON");
                else _runtimeSkybox.DisableKeyword("_ENABLEFOG_ON");
            }
            if (blend.HasProperty("_Rotation"))
                _runtimeSkybox.SetFloat("_Rotation", blend.GetFloat("_Rotation"));
            if (blend.HasProperty("_RotationSpeed"))
                _runtimeSkybox.SetFloat("_RotationSpeed", blend.GetFloat("_RotationSpeed"));
            _runtimeSkybox.SetFloat("_CubemapTransition", night);
            if (day.HasProperty("_Exposure") && dark.HasProperty("_Exposure"))
                _runtimeSkybox.SetFloat("_Exposure", Mathf.Lerp(day.GetFloat("_Exposure"),
                    dark.GetFloat("_Exposure"), night));
            if (day.HasProperty("_TintColor") && dark.HasProperty("_TintColor"))
                _runtimeSkybox.SetColor("_TintColor", Color.Lerp(day.GetColor("_TintColor") * tint,
                    dark.GetColor("_TintColor"), night));
            foreach (var property in SkyboxFloatProperties)
                if (day.HasProperty(property) && dark.HasProperty(property) && blend.HasProperty(property))
                    _runtimeSkybox.SetFloat(property, Mathf.Lerp(day.GetFloat(property),
                        dark.GetFloat(property), night));
            if (blend.HasProperty("_EnableRotation"))
            {
                var rotate = blend.GetFloat("_EnableRotation") > 0f;
                _runtimeSkybox.SetFloat("_EnableRotation", rotate ? 1f : 0f);
                if (rotate) _runtimeSkybox.EnableKeyword("_ENABLEROTATION_ON");
                else _runtimeSkybox.DisableKeyword("_ENABLEROTATION_ON");
            }
            UpdateCelestialSkybox(night);
            RenderSettings.skybox = _runtimeSkybox;
        }

        private void UpdateCelestialSkybox(float night)
        {
            if (_runtimeSkybox == null || !_runtimeSkybox.HasProperty("_SunDirection")) return;
            var sunDirection = sun != null ? -sun.transform.forward : Vector3.up;
            var moonDirection = moon != null ? -moon.transform.forward : Vector3.up;
            _runtimeSkybox.SetVector("_SunDirection", sunDirection);
            _runtimeSkybox.SetVector("_MoonDirection", moonDirection);
            _runtimeSkybox.SetFloat("_SunAngularRadius", settings.SunAngularRadius);
            _runtimeSkybox.SetFloat("_SunEdgeSoftness", settings.SunEdgeSoftness);
            _runtimeSkybox.SetFloat("_SunHaloRadius", settings.SunHaloRadius);
            _runtimeSkybox.SetFloat("_MoonAngularRadius", settings.MoonAngularRadius);
            _runtimeSkybox.SetFloat("_MoonEdgeSoftness", settings.MoonEdgeSoftness);
            _runtimeSkybox.SetFloat("_MoonHaloRadius", settings.MoonHaloRadius);
            SetSprite(_runtimeSkybox, _skyboxSource, "_SunSprite", "_SunSpriteRect", "_UseSunSprite", settings.SunSprite);
            SetSprite(_runtimeSkybox, _skyboxSource, "_MoonSprite", "_MoonSpriteRect", "_UseMoonSprite", settings.MoonSprite);
            _runtimeSkybox.SetColor("_SunColor", settings.SunDiskColor *
                Mathf.Max(0f, settings.SunDiskIntensity));
            _runtimeSkybox.SetColor("_MoonColor", settings.MoonDiskColor *
                Mathf.Max(0f, settings.MoonDiskIntensity));
            var sunAltitude = Vector3.Dot(sunDirection, Vector3.up);
            var moonAltitude = Vector3.Dot(moonDirection, Vector3.up);
            var sunVisibility = settings.ShowSun && sun != null
                ? Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.06f, 0.06f, sunAltitude))
                : 0f;
            var moonVisibility = settings.ShowMoon && moon != null
                ? Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.06f, 0.06f, moonAltitude)) * night
                : 0f;
            _runtimeSkybox.SetFloat("_SunVisibility", sunVisibility);
            _runtimeSkybox.SetFloat("_MoonVisibility", moonVisibility);
            _runtimeSkybox.SetFloat("_SunGlow", Mathf.Max(0f, settings.SunHaloStrength));
            _runtimeSkybox.SetFloat("_MoonGlow", Mathf.Max(0f, settings.MoonHaloStrength));
        }

        private static void SetSprite(Material material, Material source, string textureProperty, string rectProperty,
            string enabledProperty, Sprite sprite)
        {
            if (sprite == null)
            {
                if (source != null && source.HasProperty(enabledProperty))
                {
                    material.SetTexture(textureProperty, source.GetTexture(textureProperty));
                    material.SetVector(rectProperty, source.GetVector(rectProperty));
                    material.SetFloat(enabledProperty, source.GetFloat(enabledProperty));
                }
                else material.SetFloat(enabledProperty, 0f);
                return;
            }
            if (sprite.packed && sprite.packingMode == SpritePackingMode.Tight)
            {
                material.SetFloat(enabledProperty, 0f);
                return;
            }
            var texture = sprite.texture;
            var rect = sprite.textureRect;
            material.SetTexture(textureProperty, texture);
            material.SetVector(rectProperty, new Vector4(rect.x / texture.width, rect.y / texture.height,
                rect.width / texture.width, rect.height / texture.height));
            material.SetFloat(enabledProperty, 1f);
        }

    }
}
