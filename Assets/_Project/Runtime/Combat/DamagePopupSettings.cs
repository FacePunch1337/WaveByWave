using UnityEngine;

namespace WaveByWave.Combat
{
    [CreateAssetMenu(menuName = "Wave by Wave/Combat/Damage popup settings")]
    public sealed class DamagePopupSettings : ScriptableObject
    {
        public Font Font;
        public FontStyle FontStyle = FontStyle.Bold;
        [Range(16,128)] public int AtlasFontSize = 64;
        [Min(.01f)] public float WorldTextHeight = .42f;
        [Min(.1f)] public float Lifetime = 1.25f;
        public float RiseSpeed = 1.15f;
        [Range(0,1)] public float FadeStartsAt = .35f;
        [Range(0,2)] public int DecimalPlaces;
        public Color TextColor = new(1f,.86f,.48f,1f);
        public Color OutlineColor = new(.035f,.025f,.02f,.9f);
        [Range(0,3)] public float OutlinePixels = 1f;
        [Range(64,8192)] public int MaximumVisiblePopups = 2048;
        [Min(1f)] public float MaximumDistance = 130f;
        public Shader Shader;
    }
}
