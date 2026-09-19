using UnityEngine;

namespace WaveByWave.Items
{
    public enum ItemCategory : byte
    {
        Weapon,
        Tool,
        Treasure,
        ShipUpgrade,
        Supply
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
        [SerializeField] private ShipUpgradeStat upgradeStat;
        [SerializeField, Range(0f, 1f)] private float upgradeBonus = 0.1f;
        [SerializeField] private GameObject worldVisualPrefab;
        [Header("Торговля")]
        [SerializeField, Tooltip("Разрешить заказывать предмет через письменный стол.")]
        private bool orderable;
        [SerializeField, Min(0), Tooltip("Цена одной единицы. Ноль использует цену по умолчанию.")]
        private int purchasePrice;
        [SerializeField, Tooltip("Поворот лежащей модели относительно поверхности, в градусах.")]
        private Vector3 restingEulerAngles;
        [Header("Предмет в руках")]
        [SerializeField] private ItemEquipmentKind equipmentKind;
        [SerializeField] private bool overrideHeldPose;
        [SerializeField] private Vector3 heldPosition = new(0.34f, -0.32f, 0.65f);
        [SerializeField] private Vector3 heldEulerAngles;
        [SerializeField, Min(0.01f)] private float heldScale = 0.65f;

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
        public Vector3 HeldEulerAngles => overrideHeldPose ? heldEulerAngles : EquipmentKind switch
        {
            ItemEquipmentKind.Musket => new Vector3(90f, 0f, 0f),
            ItemEquipmentKind.Sword => new Vector3(-10f, 0f, -20f),
            ItemEquipmentKind.Shovel => new Vector3(20f, 0f, -15f),
            _ => Vector3.zero
        };
        public float HeldScale => overrideHeldPose ? Mathf.Max(0.01f, heldScale) :
            EquipmentKind == ItemEquipmentKind.Carry ? 0.4f : 0.65f;

        public string Id => id;
        public string DisplayName => displayName;
        public string Description => description;
        public ItemCategory Category => category;
        public ItemRarity Rarity => rarity;
        public Sprite Icon => icon;
        public int MaximumStack => Mathf.Clamp(maximumStack, 1, ushort.MaxValue);
        public SupplyKind SupplyKind => supplyKind;
        public float Potency => Mathf.Max(0f, potency);
        public int TreasureExperience => Mathf.Max(0, treasureExperience);
        public ShipUpgradeStat UpgradeStat => upgradeStat;
        public float UpgradeBonus => upgradeBonus;
        public GameObject WorldVisualPrefab => worldVisualPrefab;
        public bool CanBeOrdered => orderable || category == ItemCategory.Supply;
        public int PurchasePrice => purchasePrice > 0 ? purchasePrice : category switch
        {
            ItemCategory.Weapon => 120,
            ItemCategory.Tool => 65,
            ItemCategory.ShipUpgrade => 250,
            ItemCategory.Supply when supplyKind == SupplyKind.Cannonball => 8,
            ItemCategory.Supply when supplyKind == SupplyKind.Plank => 15,
            ItemCategory.Supply when supplyKind == SupplyKind.Food => 10,
            _ => 25
        };
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
