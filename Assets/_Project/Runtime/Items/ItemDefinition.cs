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
        [SerializeField, Tooltip("Поворот лежащей модели относительно поверхности, в градусах.")]
        private Vector3 restingEulerAngles;

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
