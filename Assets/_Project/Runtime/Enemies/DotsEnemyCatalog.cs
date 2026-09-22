using System;
using System.Collections.Generic;
using UnityEngine;
using StylizedWater3;
using WaveByWave.Items;

namespace WaveByWave.Enemies
{
    public enum EnemyCombatType : byte
    {
        Melee,
        Pistol,
        Rifle,
        Random,
        Ranged
    }

    public enum EnemySpawnMode : byte
    {
        WhenPointAppears,
        WhenPlayerEntersRadius
    }

    public enum EnemyAnimationState : byte
    {
        Idle,
        Run,
        MeleeAttack,
        PistolAttack,
        RifleAttack,
        Stunned
    }

    public enum EnemyBakedPartCategory : byte
    {
        Body,
        Bandana,
        Hat,
        Coat,
        GloveLeft,
        GloveRight,
        EyePatch,
        Earring,
        BootLeft,
        BootRight,
        WoodenLegLeft,
        WoodenLegRight,
        MeleeWeapon,
        PistolWeapon,
        RifleWeapon,
        HookLeft,
        HookRight
    }

    [Serializable]
    public struct EnemyAnimationFrames
    {
        public int FirstRow;
        public int Count;
        public float Duration;
    }

    [Serializable]
    public sealed class EnemyBakedPart
    {
        public string Name;
        public EnemyBakedPartCategory Category;
        [Tooltip("Pair number for left/right gloves, boots and wooden legs. Zero means unpaired.")]
        public int Pair;
        public int SourceIndex;
        public string BodySlot;
        public Mesh Mesh;
        public Texture2D Positions;
        public Texture2D Normals;
        public Material[] Materials = Array.Empty<Material>();
        public EnemyAnimationFrames[] Clips = Array.Empty<EnemyAnimationFrames>();
    }

    [Serializable]
    public sealed class EnemyPartSource
    {
        public EnemyBakedPartCategory Category;
        public GameObject Prefab;
        [Min(0)] public int Pair;
        public string AttachBone;
        public Vector3 LocalPosition;
        public Vector3 LocalEulerAngles;
        public Vector3 LocalScale = Vector3.one;
    }

    [CreateAssetMenu(menuName = "Wave By Wave/Enemies/Skeleton enemy catalog",
        fileName = "SkeletonEnemyCatalog")]
    public sealed class DotsEnemyCatalog : ScriptableObject
    {
        [Header("Editor baking sources")]
        [Tooltip("A clean skeleton prefab supplies the shared humanoid rig used while baking clips.")]
        public GameObject BakingRigPrefab;
        public List<EnemyPartSource> PartSources = new();
        public Material[] SkeletonMaterials = Array.Empty<Material>();
        public AnimationClip IdleClip;
        public AnimationClip RunClip;
        public AnimationClip MeleeAttackClip;
        public AnimationClip PistolAttackClip;
        public AnimationClip RifleAttackClip;
        [Range(4, 30)] public int BakeFramesPerSecond = 12;

        [Header("Runtime presentation")]
        public List<EnemyBakedPart> BakedParts = new();
        [HideInInspector] public string BakeSourceHash;
        public GameObject SpawnSmokePrefab;
        public GameObject DeathSmokePrefab;
        public GameObject StunEffectPrefab;
        [Min(0.1f)] public float VisualScale = 1.7f;

        [Header("Health and combat")]
        [Min(1f)] public float MaximumHealth = 60f;
        [Min(0.1f)] public float MoveSpeed = 3.8f;
        [Min(0.1f)] public float MeleeRange = 1.45f;
        [Min(0.1f)] public float RangedMinimumRange = 5f;
        [Min(1f)] public float RangedMaximumRange = 28f;
        [Min(0f)] public float MeleeDamage = 18f;
        [Min(0f)] public float PistolDamage = 14f;
        [Min(0f)] public float RifleDamage = 22f;
        [Min(0.1f)] public float MeleeCooldown = 1.15f;
        [Min(0.1f)] public float PistolCooldown = 1.75f;
        [Min(0.1f)] public float RifleCooldown = 2.4f;
        [Range(0.05f, 0.95f)] public float AttackHitNormalizedTime = 0.52f;
        [Range(0.05f, 0.95f)] public float PistolHitNormalizedTime = 0.15f;
        [Range(0.05f, 0.95f)] public float RifleHitNormalizedTime = 0.5f;
        [Min(0f)] public float DamageKnockback = 2.8f;
        [Min(0.05f)] public float DamageFlashDuration = 0.12f;
        [Min(0.1f)] public float ParryStunDuration = 2f;
        [Min(0f)] public float ParryKnockback = 2.25f;

        [Header("Surface movement (no NavMesh)")]
        [Range(1f, 70f)] public float MaximumSlope = 48f;
        [Min(0.05f), Tooltip("Maximum ledge height an enemy can run onto without jumping.")]
        public float StepHeight = 0.85f;
        [Min(0.1f), Tooltip("Maximum downward height change accepted in one surface probe.")]
        public float MaximumDrop = 1.5f;
        [Min(0.1f)] public float BodyRadius = 0.38f;
        [Min(0.2f)] public float BodyHeight = 1.7f;
        [Min(0f), Tooltip("Preferred minimum center-to-center distance between enemies. Uses a DOTS spatial grid, not physics colliders.")]
        public float CrowdSeparationRadius = 0.82f;
        [Range(0f, 2f), Tooltip("How strongly nearby enemies move apart while still pursuing their target.")]
        public float CrowdSeparationStrength = 0.9f;
        [Range(16, 2048)] public int SurfaceProbesPerFrame = 256;
        [Min(0f), Tooltip("Ignore static props up to this horizontal width for ground probes and enemy sight. Island decorations are always ignored; terrain and moving platforms are preserved. Zero disables size-based filtering.")]
        public float IgnoredObstacleWidth = 3f;
        public LayerMask SurfaceLayers = ~0;
        public WaveProfile WaterProfile;

        [Range(0f, 5f), Tooltip("Maximum water/air gap that can be crossed with a straight walking step, without a jump. Zero requires continuous ground.")]
        public float MaximumSurfaceGap = 1f;
        [Min(0.05f), Tooltip("Maximum height difference for a walking transfer between separated surfaces.")]
        public float SurfaceTransferHeight = 1f;
        [Range(1, 128), Tooltip("Maximum edge/adjacent-surface searches per frame, shared fairly between enemies.")]
        public int EdgeSearchesPerFrame = 32;

        [Header("Network for Entities")]
        [Range(1, 512)] public int SpawnsPerFrame = 32;
        [Range(1, 10000)] public int MaximumEnemies = 6000;

        [Header("One random DOTS loot drop on death")]
        public ItemDefinition[] LootDrops = Array.Empty<ItemDefinition>();

        public bool IsBaked => BakedParts.Count > 0 && BakedParts[0].Mesh != null;
        public float Duration(EnemyAnimationState state) => Mathf.Max(0.1f, Clip(state) != null ? Clip(state).length : 1f);

        public AnimationClip Clip(EnemyAnimationState state) => state switch
        {
            EnemyAnimationState.Run => RunClip,
            EnemyAnimationState.MeleeAttack => MeleeAttackClip,
            EnemyAnimationState.PistolAttack => PistolAttackClip,
            EnemyAnimationState.RifleAttack => RifleAttackClip,
            _ => IdleClip
        };

        public float AttackDamage(EnemyCombatType type) => type switch
        {
            EnemyCombatType.Pistol => PistolDamage,
            EnemyCombatType.Rifle => RifleDamage,
            _ => MeleeDamage
        };

        public float AttackHitTime(EnemyCombatType type) => type switch
        {
            EnemyCombatType.Pistol => PistolHitNormalizedTime,
            EnemyCombatType.Rifle => RifleHitNormalizedTime,
            _ => AttackHitNormalizedTime
        };

        public float AttackCooldown(EnemyCombatType type) => type switch
        {
            EnemyCombatType.Pistol => PistolCooldown,
            EnemyCombatType.Rifle => RifleCooldown,
            _ => MeleeCooldown
        };

        public EnemyAnimationState AttackAnimation(EnemyCombatType type) => type switch
        {
            EnemyCombatType.Pistol => EnemyAnimationState.PistolAttack,
            EnemyCombatType.Rifle => EnemyAnimationState.RifleAttack,
            _ => EnemyAnimationState.MeleeAttack
        };
    }
}
