using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using WaveByWave.Player;
using WaveByWave.Items;
using WaveByWave.Ships;

namespace WaveByWave.UI
{
    [DefaultExecutionOrder(-250)]
    public sealed class PlayerProgressionUI : MonoBehaviour
    {
        public static bool MenuOpen { get; private set; }
        public static int ClosedOnFrame { get; private set; } = -1;
        private NetworkPlayerController _player;
        private GameObject _choices, _rings;
        private readonly List<GameObject> _offerRows = new(), _ringRows = new();
        private int _shownLevel = -1;
        private float _nextRefresh;
        public void Initialize(NetworkPlayerController player) => _player = player;

        private void Update()
        {
            if (_player == null || !_player.IsSpawned || !_player.IsOwner) return;
            if (NetworkPlayerController.RingChoiceOpen)
            {
                if (MenuOpen) SetMenu(false);
                if (_choices == null) _choices = GameUiPrefabs.Create("Menus/RingUpgrade", owner: this) ?? BuildWindow(true);
                _choices.SetActive(true);
                if (_shownLevel != _player.RingChoiceLevel)
                {
                    _shownLevel = _player.RingChoiceLevel;
                    RebuildOffers();
                }
                GameUiPrefabs.Find<Text>(_choices, "Panel/Title").text = $"УРОВЕНЬ {_shownLevel} · ВЫБЕРИ КОЛЬЦО";
                GameUiPrefabs.Find<Text>(_choices, "Panel/Footer").text = _player.RingChoiceSubmitted
                    ? "Выбор принят. Ждём остальных игроков…" : $"Занято колец: {_player.DistinctRingCount} / {_player.RingCatalog.MaximumDistinctRings}";
                foreach (var row in _offerRows) row.GetComponent<Button>().interactable = !_player.RingChoiceSubmitted;
                if (_player.RingChoiceSubmitted && !ShipCannonBattery.AnyUpgradePaused)
                    _player.FinishLocalRingChoice();
            }
            else if (_choices != null) _choices.SetActive(false);

            if (NetworkPlayerController.RingChoiceOpen || ShipFlooding.VoyageOver ||
                SessionMenuPresenter.InputCaptured || EquipmentAdminPanel.InputCaptured ||
                Customization.CustomizationMenu.InputCaptured)
            { if (MenuOpen) SetMenu(false); return; }
            var keyboard = Keyboard.current;
            if (keyboard != null && (keyboard.tabKey.wasPressedThisFrame || MenuOpen && keyboard.escapeKey.wasPressedThisFrame))
                SetMenu(!MenuOpen);
            if (MenuOpen && Time.unscaledTime >= _nextRefresh)
            { _nextRefresh = Time.unscaledTime + .2f; RefreshRings(); }
        }

        private void SetMenu(bool open)
        {
            if (!open && MenuOpen) ClosedOnFrame = Time.frameCount;
            MenuOpen = open;
            if (open && _rings == null)
            {
                _rings = GameUiPrefabs.Create("Menus/PlayerRings", owner: this) ?? BuildWindow(false);
                GameUiPrefabs.Find<Button>(_rings, "Panel/Close").onClick.AddListener(() => SetMenu(false));
            }
            if (_rings != null) _rings.SetActive(open);
            if (open) RefreshRings();
            var captured = open || NetworkPlayerController.RingChoiceOpen || SessionMenuPresenter.InputCaptured ||
                EquipmentAdminPanel.InputCaptured || Customization.CustomizationMenu.InputCaptured;
            Cursor.visible = captured;
            Cursor.lockState = captured ? CursorLockMode.None : CursorLockMode.Locked;
        }
        private void RebuildOffers()
        {
            foreach (var row in _offerRows) Destroy(row);
            _offerRows.Clear();
            for (byte i = 0; i < _player.RingOfferCount; i++)
            {
                _player.GetRingOffer(i, out var stat, out var rarity);
                var row = NewRow(_choices);
                var index = i;
                row.GetComponent<Button>().onClick.AddListener(() => _player.SubmitRingChoice(index));
                BindRow(row, stat, rarity, _player.RingLevel(stat) + 1,
                    PlayerRingCatalog.FormatBonus(stat, _player.RingCatalog.Bonus(stat, rarity)) +
                    "  →  " + PlayerRingCatalog.FormatBonus(stat, _player.RingValue(stat) + _player.RingCatalog.Bonus(stat, rarity)));
                _offerRows.Add(row);
            }
        }
        private void RefreshRings()
        {
            var count = _player.DistinctRingCount;
            while (_ringRows.Count < count) _ringRows.Add(NewRow(_rings));
            for (var i = 0; i < _ringRows.Count; i++)
            {
                _ringRows[i].SetActive(i < count);
                if (i >= count) continue;
                var state = _player.GetRingState(i);
                _ringRows[i].GetComponent<Button>().interactable = false;
                BindRow(_ringRows[i], (PlayerRingStat)state.Stat, (ItemRarity)state.Rarity, state.Level,
                    PlayerRingCatalog.FormatBonus((PlayerRingStat)state.Stat, state.Value));
            }
            GameUiPrefabs.Find<Text>(_rings, "Panel/Title").text = $"КОЛЬЦА · {count} / {_player.RingCatalog.MaximumDistinctRings}";
            GameUiPrefabs.Find<Text>(_rings, "Panel/Footer").text =
                $"Атака ×{_player.AttackSpeedMultiplier:0.##}    Перезарядка ×{_player.ReloadSpeedMultiplier:0.##}\n" +
                $"Область ×{_player.MeleeAreaMultiplier:0.##}    Снарядов: {_player.ProjectileCount}    Ремонт ×{_player.RepairSpeed:0.##}";
            GameUiPrefabs.Find<Text>(_rings, "Panel/Empty").gameObject.SetActive(count == 0);
        }
        private static GameObject NewRow(GameObject window)
        {
            var parent = window.transform.Find("Panel/Scroll/Content");
            var row = GameUiPrefabs.Create("Elements/RingRow", parent) ?? BuildRow(parent);
            return row;
        }
        private void BindRow(GameObject row, PlayerRingStat stat, ItemRarity rarity, int level, string bonus)
        {
            var definition = _player.RingCatalog.Find(stat);
            var color = ItemDefinition.ColorForRarity(rarity);
            GameUiPrefabs.Find<Image>(row, "Badge").color = color;
            var icon = GameUiPrefabs.Find<Image>(row, "Badge/Icon");
            icon.sprite = definition.Icon; icon.enabled = definition.Icon != null;
            var name = GameUiPrefabs.Find<Text>(row, "Title");
            name.text = definition.DisplayName; name.color = color;
            GameUiPrefabs.Find<Text>(row, "Detail").text = $"Уровень {level} · {bonus}";
        }

        // Fallbacks for projects without the authored UI assets.
        public static GameObject BuildWindow(bool upgrade)
        {
            var root = UiDefaults.Canvas(upgrade ? "Ring upgrade" : "Player rings", 220);
            var shade = UiDefaults.Image("Shade", root.transform, new Color(.015f,.025f,.04f,.85f));
            UiDefaults.Stretch(shade.rectTransform);
            var panel = UiDefaults.Image("Panel", root.transform, new Color(.025f,.045f,.065f,.98f));
            UiDefaults.Rect(panel.rectTransform, new Vector2(760,720), Vector2.zero);
            UiDefaults.Text("Title", panel.transform, "КОЛЬЦА", 28, new Vector2(700,60), new Vector2(0,300));
            var scroll = UiDefaults.Image("Scroll", panel.transform, new Color(0,0,0,.12f));
            UiDefaults.Rect(scroll.rectTransform, new Vector2(704,490), new Vector2(0,15));
            scroll.gameObject.AddComponent<RectMask2D>();
            var scrollView = scroll.gameObject.AddComponent<ScrollRect>();
            scrollView.viewport = scroll.rectTransform; scrollView.horizontal = false; scrollView.scrollSensitivity = 35;
            var content = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            content.transform.SetParent(scroll.transform,false);
            var r = (RectTransform)content.transform;
            r.anchorMin = new Vector2(0,1); r.anchorMax = Vector2.one; r.pivot = new Vector2(.5f,1); r.sizeDelta = Vector2.zero;
            var layout = content.GetComponent<VerticalLayoutGroup>(); layout.spacing = 12; layout.padding = new RectOffset(8,8,8,8);
            layout.childControlWidth = layout.childControlHeight = true; layout.childForceExpandHeight = false;
            content.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scrollView.content = r;
            UiDefaults.Text("Footer", panel.transform, "", 20, new Vector2(700,72), new Vector2(0,-284));
            var empty = UiDefaults.Text("Empty", panel.transform, "Кольца появятся после повышения уровня команды", 21, new Vector2(650,80), Vector2.zero);
            empty.gameObject.SetActive(false);
            var close = UiDefaults.Button("Close", panel.transform, "×", new Vector2(42,42), new Vector2(348,332));
            close.gameObject.SetActive(!upgrade);
            return root;
        }
        public static GameObject BuildRow(Transform parent = null)
        {
            var row = UiDefaults.Image("Ring row", parent, new Color(.065f,.105f,.14f,1));
            UiDefaults.Rect(row.rectTransform, new Vector2(680,132), Vector2.zero);
            row.gameObject.AddComponent<LayoutElement>().preferredHeight = 132;
            var button = row.gameObject.AddComponent<Button>(); button.targetGraphic = row;
            var colors = button.colors; colors.disabledColor = Color.white; button.colors = colors;
            var badge = UiDefaults.Image("Badge", row.transform, Color.white);
            UiDefaults.Rect(badge.rectTransform, new Vector2(86,86), new Vector2(-272,0));
            var icon = UiDefaults.Image("Icon", badge.transform, Color.white); UiDefaults.Stretch(icon.rectTransform, 9);
            var title = UiDefaults.Text("Title", row.transform, "Название кольца", 26, new Vector2(480,46), new Vector2(60,24));
            title.alignment = TextAnchor.MiddleLeft;
            var detail = UiDefaults.Text("Detail", row.transform, "Уровень · бонус", 20, new Vector2(480,42), new Vector2(60,-24));
            detail.alignment = TextAnchor.MiddleLeft;
            return row.gameObject;
        }
        private void OnDestroy()
        {
            if (MenuOpen && GameUiPrefabs.IsOwnedBy(_rings, this)) SetMenu(false);
            foreach (var row in _offerRows) if (row != null) Destroy(row);
            foreach (var row in _ringRows) if (row != null) Destroy(row);
            if (_choices != null) GameUiPrefabs.Release(_choices, this);
            if (_rings != null) GameUiPrefabs.Release(_rings, this);
        }
    }
}
