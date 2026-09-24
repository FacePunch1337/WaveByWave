using UnityEngine;
using UnityEngine.UI;
using WaveByWave.Items;

namespace WaveByWave.UI
{
    // Only contents and selection change at runtime. Layout, sprites and colors belong to the prefab.
    public sealed class InventorySlotView : MonoBehaviour
    {
        [SerializeField] private Image icon;
        [SerializeField] private Text shortcut;
        [SerializeField] private Text stackCount;
        [SerializeField] private Text missingIconLabel;
        [SerializeField] private RectTransform visuals;
        [SerializeField, Min(1f)] private float selectedScale = 1.14f;
        [SerializeField, Min(0f)] private float scaleSpeed = 16f;
        private bool _selected;

        public void SetSlot(int index, ItemDefinition item, int amount, bool selected)
        {
            if (shortcut != null) shortcut.text = index == 9 ? "0" : (index + 1).ToString();
            if (icon != null)
            {
                icon.sprite = item != null ? item.Icon : null;
                icon.enabled = icon.sprite != null;
            }
            if (stackCount != null) stackCount.text = item != null && amount > 1 ? amount.ToString() : "";
            if (missingIconLabel != null)
                missingIconLabel.text = item != null && item.Icon == null ? "?" : "";
            _selected = selected;
        }

        private void Update()
        {
            if (visuals == null) return;
            var target = Vector3.one * (_selected ? selectedScale : 1f);
            if ((visuals.localScale - target).sqrMagnitude < .000001f)
            {
                if (visuals.localScale != target) visuals.localScale = target;
                return;
            }
            visuals.localScale = scaleSpeed <= 0f ? target :
                Vector3.Lerp(visuals.localScale, target, 1f - Mathf.Exp(-scaleSpeed * Time.unscaledDeltaTime));
        }
    }
}
