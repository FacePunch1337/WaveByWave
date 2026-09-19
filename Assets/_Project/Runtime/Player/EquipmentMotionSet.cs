using UnityEngine;

namespace WaveByWave.Player
{
    public enum EquipmentAction : byte { None, SwordSwing, SwordBlock, MusketShot, MusketReload, MusketAim,
        HookCharge, HookThrow, HookReel, BucketScoop, BucketSplash, ShovelDig }

    [CreateAssetMenu(menuName = "Wave by Wave/Player/Equipment motions")]
    public sealed class EquipmentMotionSet : ScriptableObject
    {
        [Tooltip("Клипы анимируют локальную позицию/поворот корня Motion. Руки и предмет — его дочерние объекты.")]
        public AnimationClip swordSwing, swordBlock, musketShot, musketReload, musketAim,
            hookCharge, hookThrow, hookReel, bucketScoop, bucketSplash, shovelDig;

        public AnimationClip Get(EquipmentAction action) => action switch
        {
            EquipmentAction.SwordSwing => swordSwing, EquipmentAction.SwordBlock => swordBlock,
            EquipmentAction.MusketShot => musketShot, EquipmentAction.MusketReload => musketReload,
            EquipmentAction.MusketAim => musketAim, EquipmentAction.HookCharge => hookCharge,
            EquipmentAction.HookThrow => hookThrow, EquipmentAction.HookReel => hookReel,
            EquipmentAction.BucketScoop => bucketScoop, EquipmentAction.BucketSplash => bucketSplash,
            EquipmentAction.ShovelDig => shovelDig, _ => null
        };
    }
}
