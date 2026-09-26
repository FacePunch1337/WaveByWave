using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;
using StylizedWater3;
using WaveByWave.Items;

namespace WaveByWave.Enemies
{
    public enum EnemyKind : byte { Skeleton, Troll, Shark, Amphibian }
    public enum EnemyHabitat : byte { Land, Water, Amphibious }
    public enum EnemyHealthBarMode : byte { Profile, Show, Hide }
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
        Stunned,
        Swim
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
    public sealed class EnemyBakedVariant
    {
        public EnemyCombatType CombatType;
        public uint Seed;
        public EnemyBakedPart Visual;
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
        // Runtime, baking and editor tools must address the same authored profile.
        public const string SkeletonResourcePath = "Enemies/SkeletonEnemyCatalog";
        public const float MaximumTransferGap = 20f;
        [Header("Species")]
        public EnemyKind Kind;
        public string DisplayName = "Скелеты";
        public EnemyHabitat Habitat;
        [Tooltip("Off uses every baked body part with its authored material, without clothing or weapons.")]
        public bool RandomizeAppearance = true;
        [Min(0.1f)] public float SwimSpeed = 4f;
        [Min(0.1f), Tooltip("Depth of the model origin below the ocean surface while swimming.")]
        public float SwimmingDepth = 1.1f;
        [Min(0f)] public float MinimumWaterDepth = 1.2f;
        [FormerlySerializedAs("AmphibiousBoardingHeight")]
        [Min(0f), Tooltip("Maximum vertical distance this enemy may climb onto a player ship. Set to zero to disable ship boarding.")]
        public float ShipBoardingHeight = 5f;
        public AnimationClip SwimClip;
        [Tooltip("Optional local capsule endpoints for a horizontal animal. Values are in world metres before rotation.")]
        public bool CustomHitCapsule;
        public Vector3 HitCapsuleStart = new(0, 0.4f, -0.8f);
        public Vector3 HitCapsuleEnd = new(0, 0.4f, 0.8f);

        [Header("Automatic ocean encounter (Shark only)")]
        [Tooltip("One shared encounter for the crew. Starts with one shark while any living player is in the ocean; each defeated group increases the next group by one.")]
        public bool SpawnWhenPlayerEntersOcean = true;
        [Min(1), Tooltip("Maximum sharks in an automatic encounter group. At this limit, subsequent groups keep the same size. Resets for a new voyage.")]
        public int OceanEncounterMaximumSharks = 5;
        [Min(0f), Tooltip("Seconds between defeating a group and spawning its replacement. A player must still be in the ocean.")]
        public float OceanEncounterRespawnDelay = 3f;
        [Min(0f), Tooltip("Closest horizontal distance from the entering player at which the shark may appear.")]
        public float OceanEncounterMinimumDistance = 8f;
        [Min(0.1f), Tooltip("Farthest horizontal distance from the entering player at which the shark may appear.")]
        public float OceanEncounterMaximumDistance = 16f;

        [Header("Health bars (optional)")]
        public bool ShowHealthBars;
        [Min(0f)] public float HealthBarHeight = 2.1f;
        public Vector2 HealthBarSize = new(0.9f, 0.1f);
        [Min(1f)] public float HealthBarDistance = 45f;
        public Material HealthBarMaterial;
        public Mesh HealthBarMesh;

        [Header("Enabled skeleton types (new spawns only)")]
        [Tooltip("Allow melee skeletons in new spawns, including the admin panel and ship crews. Existing skeletons are unaffected.")]
        public bool EnableMeleeSpawns = true;
        [Tooltip("Allow pistol skeletons in new spawns. Disable both Pistol and Rifle to spawn only melee skeletons.")]
        public bool EnablePistolSpawns = true;
        [Tooltip("Allow rifle skeletons in new spawns. Explicit spawn points for a disabled type are skipped.")]
        public bool EnableRifleSpawns = true;

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
        [Tooltip("Use editor-baked whole skeletons. Falls back to modular parts until variants have been baked.")]
        public bool UseCombinedVariants = true;
        [Range(1, 32)] public int CombinedVariantsPerType = 8;
        public List<EnemyBakedVariant> CombinedVariants = new();
        [HideInInspector] public string CombinedSourceHash;
        public List<EnemyBakedPart> BakedParts = new();
        [HideInInspector] public string BakeSourceHash;
        public GameObject SpawnSmokePrefab;
        public GameObject DeathSmokePrefab;
        public GameObject StunEffectPrefab;
        [Min(0.1f)] public float VisualScale = 1.7f;
        [Header("DOTS skeleton lighting")]
        [Range(0f, 1f), Tooltip("Minimum linear brightness on the unlit side in daylight. Applied to new DOTS skeleton materials without rebaking.")]
        public float DayMinimumLight = 0.65f;
        [Range(0f, 1f), Tooltip("Minimum linear brightness on the unlit side at night. Applied to new DOTS skeleton materials without rebaking.")]
        public float NightMinimumLight = 0.25f;

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
        [Min(0.1f), Tooltip("Maximum vertical feet adjustment per second. Horizontal movement remains independent.")]
        public float SurfaceVerticalSpeed = 4f;
        [Min(0.1f)] public float BodyRadius = 0.38f;
        [Min(0.2f)] public float BodyHeight = 1.7f;
        [Min(0.1f), Tooltip("Радиус попадания пуль по скелетам. Не меняет радиус движения и обход препятствий.")]
        public float ProjectileHitRadius = 0.42f;
        [Min(0f), Tooltip("Preferred minimum center-to-center distance between enemies. Uses a DOTS spatial grid, not physics colliders.")]
        public float CrowdSeparationRadius = 0.82f;
        [Range(0f, 2f), Tooltip("How strongly nearby enemies move apart while still pursuing their target.")]
        public float CrowdSeparationStrength = 0.9f;
        [Range(0.3f, 4f), Tooltip("Distance in metres used to anticipate a blocked crowd corridor. Larger values start flanking earlier; this is not a target detection radius.")]
        public float CrowdAvoidanceLookAhead = 1.5f;
        [Range(16, 2048)] public int SurfaceProbesPerFrame = 256;
        [Min(0f), Tooltip("Ignore static props up to this horizontal width for ground probes and enemy sight. Island decorations are always ignored; terrain and moving platforms are preserved. Zero disables size-based filtering.")]
        public float IgnoredObstacleWidth = 3f;
        public LayerMask SurfaceLayers = ~0;
        public WaveProfile WaterProfile;

        [Range(0f, MaximumTransferGap), Tooltip("Maximum horizontal water/air gap between surface edges, in metres. Independent of Step Height and Maximum Drop. Zero disables gap transfers. Search samples and per-frame search count remain bounded at long distances.")]
        public float MaximumSurfaceGap = 1f;
        [Min(0.05f), Tooltip("Maximum height difference for a walking transfer between separated surfaces.")]
        public float SurfaceTransferHeight = 1f;
        [Range(1, 128), Tooltip("Maximum edge/adjacent-surface searches per frame, shared fairly between enemies.")]
        public int EdgeSearchesPerFrame = 32;

        [Header("Ship surface movement")]
        [Tooltip("On: use and bake ship-local deck maps. Off: skip deck-map baking and move along the ship's current colliders using ground probes. Existing maps are kept for switching back. Applies live on the server; collider movement requires a physical ship view.")]
        public bool UseBakedDeckNavigation;

        [Header("Performance diagnostics (live toggles)")]
        [Tooltip("Give every skeleton a stable point in a spiral around its player target. This only changes desired movement: it has no neighbour search and does not prevent physical overlap by itself. Applies live.")]
        public bool EnableTargetSlots;
        [Range(0.2f, 2f), Tooltip("Approximate distance between stable target slots around the player, in world metres. Applies live; no deck rebake is required.")]
        public float TargetSlotSpacing = 0.35f;
        [Tooltip("Choose a persistent flank around nearby enemies. Off skips avoidance decisions. Does not disable pursuit or change target detection.")]
        public bool EnableCrowdAvoidance = true;
        [Tooltip("Calculate soft neighbour repulsion. Independent of hard contacts and avoidance. The neighbour search is already disabled when Crowd Separation Radius is zero.")]
        public bool EnableCrowdSeparation = true;
        [Tooltip("Use an incremental surface-local index to apply smoothly filtered personal-space pressure. There is no hard stop or collision sweep; only moved bots update the index and at most 32 occupants of a cell are sampled. Off skips this pressure.")]
        public bool EnableCrowdCollisions = true;
        [Tooltip("Check intermediate ground samples on the collider/PhysX movement path. Off still checks the destination, but can allow cutting across water/gaps. Baked deck connectivity is unaffected.")]
        public bool EnableSurfaceContinuityChecks = true;
        [Tooltip("Search lateral directions along a blocked surface edge, on both baked decks and colliders. Off stops this search; crowd avoidance is a separate toggle.")]
        public bool EnableSurfaceEdgeFollowing = true;
        [Tooltip("Allow a blocked bot to detour sideways along a railing even when that step temporarily moves away from the player. Off restores only distance-reducing edge steps and skips the extra detour probes. Applies live on the server; requires Enable Surface Edge Following and collider-based movement.")]
        public bool EnableSurfaceEdgeDetours = true;
        [Tooltip("Search for walking transfers across water/air gaps. Off skips new searches; bots already crossing return to their departure surface without teleporting.")]
        public bool EnableSurfaceTransfers = true;
        [Tooltip("Allow enemy attacks, including their visibility checks and damage. Off cancels an ongoing attack. Incoming damage, stuns, pursuit and surface movement still work.")]
        public bool EnableCombat = true;

        [Header("Network for Entities")]
        [Range(1, 512)] public int SpawnsPerFrame = 32;
        [Range(1, 10000)] public int MaximumEnemies = 6000;

        [Header("One random DOTS loot drop on death")]
        public ItemDefinition[] LootDrops = Array.Empty<ItemDefinition>();

        public bool IsBaked => BakedParts.Count > 0 && BakedParts[0].Mesh != null;
        public float Duration(EnemyAnimationState state) => Mathf.Max(0.1f, Clip(state) != null ? Clip(state).length : 1f);

        private int SpawnTypeMask(EnemyCombatType requested)
        {
            var enabled = (EnableMeleeSpawns ? 1 : 0) | (EnablePistolSpawns ? 2 : 0) |
                (EnableRifleSpawns ? 4 : 0);
            var allowed = requested switch
            {
                EnemyCombatType.Melee => 1,
                EnemyCombatType.Pistol => 2,
                EnemyCombatType.Rifle => 4,
                EnemyCombatType.Random => 7,
                EnemyCombatType.Ranged => 6,
                _ => 0
            };
            return enabled & allowed;
        }

        public bool CanSpawnType(EnemyCombatType requested) => SpawnTypeMask(requested) != 0;

        public bool TrySelectSpawnType(EnemyCombatType requested, ref Unity.Mathematics.Random random,
            out EnemyCombatType type)
        {
            type = default;
            var mask = SpawnTypeMask(requested);
            var count = (mask & 1) + ((mask >> 1) & 1) + ((mask >> 2) & 1);
            if (count == 0) return false;
            var selected = count == 1 ? 0 : random.NextInt(count);
            for (var i = 0; i < 3; i++)
            {
                if ((mask & (1 << i)) == 0 || selected-- != 0) continue;
                type = (EnemyCombatType)i;
                return true;
            }
            return false;
        }

        public AnimationClip Clip(EnemyAnimationState state) => state switch
        {
            EnemyAnimationState.Run => RunClip,
            EnemyAnimationState.MeleeAttack => MeleeAttackClip,
            EnemyAnimationState.PistolAttack => PistolAttackClip,
            EnemyAnimationState.RifleAttack => RifleAttackClip,
            EnemyAnimationState.Swim => SwimClip != null ? SwimClip : RunClip,
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
