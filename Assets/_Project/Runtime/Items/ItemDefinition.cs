using UnityEngine;

namespace WaveByWave.Items
{
    public enum ItemCategory : byte
    {
        Weapon,
        Tool,
        Treasure,
        ShipUpgrade,
        Supply,
        Chest
    }

    public enum ItemRarity : byte
    {
        Common,
        Uncommon,
        Rare,
        Epic,
        Legendary
    }

    public enum SupplyKind : byte { None, Cannonball, Plank, Food }
    public enum ItemEquipmentKind : byte { Automatic, Carry, Sword, Musket, Hook, Bucket, Shovel }
    public enum ShipUpgradeStat : byte { CannonDamage, Armor, Speed, Maneuverability }

    [System.Serializable]
    public struct ItemHandGripPose
    {
        [SerializeField, Tooltip("Должна ли эта рука участвовать в IK для предмета.")]
        private bool enabled;
        [SerializeField, Tooltip("Позиция ладони относительно корня prefab предмета.")]
        private Vector3 localPosition;
        [SerializeField, Tooltip("Поворот ладони относительно корня prefab предмета, в градусах.")]
        private Vector3 localEulerAngles;

        public bool Enabled => enabled;
        public Vector3 LocalPosition => localPosition;
        public Quaternion LocalRotation => Quaternion.Euler(localEulerAngles);
    }

    [CreateAssetMenu(menuName = "Wave by Wave/Items/Item Definition", fileName = "Item_")]
    public sealed class ItemDefinition : ScriptableObject
    {
        [SerializeField] private string id;
        [SerializeField] private string displayName;
        [SerializeField, TextArea] private string description;
        [SerializeField] private ItemCategory category;
        [SerializeField] private ItemRarity rarity;
        [SerializeField] private Sprite icon;
        [SerializeField, Min(1)] private int maximumStack = 1;
        [SerializeField] private SupplyKind supplyKind;
        [SerializeField, Min(0f)] private float potency = 40f;
        [SerializeField, Min(0)] private int treasureExperience = 25;
        [SerializeField, Tooltip("Luck increases this reward's coin amount when it drops from a chest.")]
        private bool coinReward;
        [SerializeField] private ShipUpgradeStat upgradeStat;
        [SerializeField, Range(0f, 1f)] private float upgradeBonus = 0.1f;
        [SerializeField, Tooltip("Полный prefab предмета с одним MeshFilter. Его меш и материалы используются в мире, в руках и DOTS-спавне.")]
        private GameObject worldVisualPrefab;
        [SerializeField, Tooltip("Индивидуальный поворот выброшенной или заспавненной модели относительно поверхности, в градусах.")]
        private Vector3 restingEulerAngles;
        [Header("Сундук")]
        [SerializeField] private ChestLootTable chestLoot;
        [Header("Предмет в руках")]
        [SerializeField] private ItemEquipmentKind equipmentKind;
        [SerializeField] private bool overrideHeldPose;
        [SerializeField] private Vector3 heldPosition = new(0.34f, -0.32f, 0.65f);
        [SerializeField] private Vector3 heldEulerAngles;
        [SerializeField, Min(0.01f)] private float heldScale = 0.65f;
        [Header("ПКМ: блок / прицеливание")]
        [SerializeField, Tooltip("Использовать отдельную позицию и поворот предмета во время действия на ПКМ.")]
        private bool overrideSecondaryHeldPose;
        [SerializeField] private Vector3 secondaryHeldPosition;
        [SerializeField] private Vector3 secondaryHeldEulerAngles;
        [Header("IK рук")]
        [SerializeField, Tooltip("Использовать точки из этого ItemDefinition вместо компонентов ItemHandGripPoint в prefab.")]
        private bool overrideHandGripPoints;
        [SerializeField] private ItemHandGripPose rightHandGrip;
        [SerializeField] private ItemHandGripPose leftHandGrip;

        public ItemEquipmentKind EquipmentKind => equipmentKind != ItemEquipmentKind.Automatic ? equipmentKind :
            id != null && id.StartsWith("cutlass", System.StringComparison.Ordinal) ? ItemEquipmentKind.Sword :
            id != null && id.StartsWith("musket", System.StringComparison.Ordinal) ? ItemEquipmentKind.Musket :
            id == "hook" ? ItemEquipmentKind.Hook : id == "bucket" ? ItemEquipmentKind.Bucket :
            id == "shovel" ? ItemEquipmentKind.Shovel : ItemEquipmentKind.Carry;
        public Vector3 HeldPosition => overrideHeldPose ? heldPosition : EquipmentKind switch
        {
            ItemEquipmentKind.Musket => new Vector3(0.26f, -0.28f, 0.63f),
            ItemEquipmentKind.Bucket => new Vector3(0.32f, -0.43f, 0.7f),
            ItemEquipmentKind.Shovel => new Vector3(0.32f, -0.5f, 0.75f),
            ItemEquipmentKind.Carry => new Vector3(0.25f, -0.35f, 0.65f),
            ItemEquipmentKind.Sword => new Vector3(0.32f, -0.12f, 0.78f),
            _ => new Vector3(0.34f, -0.32f, 0.65f)
        };
        // Rotation and scale come from the prefab. These optional values are only an
        // additional hand-pose offset for exceptional items.
        public Vector3 HeldEulerAngles => overrideHeldPose ? heldEulerAngles : Vector3.zero;
        public float HeldScale => overrideHeldPose ? Mathf.Max(0.01f, heldScale) : 1f;
        public Vector3 SecondaryHeldPosition => overrideSecondaryHeldPose ? secondaryHeldPosition :
            EquipmentKind == ItemEquipmentKind.Musket ? new Vector3(0f, -0.13f, 0.7f) : HeldPosition;
        public Quaternion SecondaryHeldRotation => Quaternion.Euler(
            overrideSecondaryHeldPose ? secondaryHeldEulerAngles : HeldEulerAngles);
        public bool OverridesHandGripPoints => overrideHandGripPoints;

        public bool TryGetHandGrip(ItemGripHand hand, out ItemHandGripPose grip)
        {
            grip = hand == ItemGripHand.Right ? rightHandGrip : leftHandGrip;
            return overrideHandGripPoints && grip.Enabled;
        }

        public string Id => id;
        public string DisplayName => displayName;
        public string Description => description;
        public ItemCategory Category => category;
        public bool IsChest => category == ItemCategory.Chest && chestLoot != null;
        public ChestLootTable ChestLoot => chestLoot;
        public ItemRarity Rarity => rarity;
        public Sprite Icon => icon;
        public int MaximumStack => Mathf.Clamp(maximumStack, 1, ushort.MaxValue);
        public SupplyKind SupplyKind => supplyKind;
        public float Potency => Mathf.Max(0f, potency);
        public int TreasureExperience => Mathf.Max(0, treasureExperience);
        public bool IsCoinReward => coinReward || id != null &&
            id.Contains("coin", System.StringComparison.OrdinalIgnoreCase);
        public ShipUpgradeStat UpgradeStat => upgradeStat;
        public float UpgradeBonus => upgradeBonus;
        public GameObject WorldVisualPrefab => worldVisualPrefab;
        public Quaternion RestingRotation => Quaternion.Euler(restingEulerAngles);
        public Color RarityColor => rarity switch
        {
            ItemRarity.Uncommon => new Color(0.3f, 1f, 0.4f),
            ItemRarity.Rare => new Color(0.15f, 0.55f, 1f),
            ItemRarity.Epic => new Color(0.8f, 0.25f, 1f),
            ItemRarity.Legendary => new Color(1f, 0.65f, 0.12f),
            _ => new Color(0.8f, 0.9f, 1f)
        };
    }
}
