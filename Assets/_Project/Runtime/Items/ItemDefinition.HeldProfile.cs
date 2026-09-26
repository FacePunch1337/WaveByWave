using UnityEngine;

namespace WaveByWave.Items
{
    public sealed partial class ItemDefinition
    {
        [SerializeField, Tooltip("Предмет-источник настроек удержания, блока/прицеливания и IK рук. Изменения источника применяются ко всем связанным предметам. Пусто — собственные настройки.")]
        private ItemDefinition heldAndIkProfile;

        public ItemDefinition HeldAndIkProfile => heldAndIkProfile;
        public ItemDefinition HeldAndIkSource
        {
            get { TryResolveHeldAndIkSource(out var source); return source; }
        }
        public GameObject HandGripPrefab
        {
            get
            {
                var prefab = HeldAndIkSource.worldVisualPrefab;
                return prefab != null ? prefab : worldVisualPrefab;
            }
        }

        // Allocation-free traversal, including protection against invalid serialized cycles.
        // Resolve live so changes to a base item also affect an already equipped variant.
        public bool TryResolveHeldAndIkSource(out ItemDefinition source)
        {
            source = this;
            if (heldAndIkProfile == null) return true;
            var slow = this;
            var fast = this;
            while (fast != null && fast.heldAndIkProfile != null)
            {
                slow = slow.heldAndIkProfile;
                fast = fast.heldAndIkProfile.heldAndIkProfile;
                if (slow == fast) return false;
            }
            while (source.heldAndIkProfile != null) source = source.heldAndIkProfile;
            return true;
        }

        public bool CanUseHeldAndIkProfile(ItemDefinition profile)
        {
            if (profile == null) return true;
            if (!profile.TryResolveHeldAndIkSource(out _)) return false;
            for (var node = profile; node != null; node = node.heldAndIkProfile)
                if (node == this) return false;
            return true;
        }

        public bool TrySetHeldAndIkProfile(ItemDefinition profile)
        {
            if (!CanUseHeldAndIkProfile(profile)) return false;
            heldAndIkProfile = profile;
            return true;
        }
    }
}
