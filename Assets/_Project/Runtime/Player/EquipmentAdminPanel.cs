using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using WaveByWave.UI;

namespace WaveByWave.Player
{
    [DefaultExecutionOrder(-200)]
    public sealed class EquipmentAdminPanel : MonoBehaviour
    {
        public static bool InputCaptured { get; private set; }
        private PlayerInventory _inventory;
        private GameObject _canvas, _panel;
        private Slider _stressCountSlider, _stressRadiusSlider;
        private Text _stressCountLabel, _stressRadiusLabel;
        private int _requestedStressCount, _appliedStressCount = -1;
        private float _requestedStressRadius = 15f, _appliedStressRadius = -1f, _stressApplyAt;
        private Slider _enemySlider;
        private Text _enemyLabel;
        private int _enemyTarget;
        private bool _enemyDirty;
        private float _enemyApplyAt, _enemyRadius = 15f;
        private Slider _shipCountSlider, _shipRadiusSlider;
        private Text _shipLabel, _shipRadiusLabel;
        private int _shipSpawnCount = 12;
        private float _shipSpawnRadius = 350f;
        public void Initialize(PlayerInventory inventory) => _inventory = inventory;
        private void Update()
        {
            if (_inventory == null || !_inventory.IsSpawned || !_inventory.IsOwner || !_inventory.IsHost) return;
            if (SessionMenuPresenter.InputCaptured && InputCaptured) SetOpen(false);
            if (!SessionMenuPresenter.InputCaptured &&
                !WaveByWave.Customization.CustomizationMenu.InputCaptured && Keyboard.current != null &&
                Keyboard.current.f2Key.wasPressedThisFrame)
            {
                if (_canvas == null) Build();
                SetOpen(!InputCaptured);
            }
            if (InputCaptured && Time.unscaledTime >= _stressApplyAt &&
                (_requestedStressCount != _appliedStressCount ||
                 !Mathf.Approximately(_requestedStressRadius, _appliedStressRadius)))
            {
                var applied = _inventory.SetAdminStressItems(_requestedStressCount, _requestedStressRadius);
                if (applied)
                {
                    _appliedStressCount = _requestedStressCount;
                    _appliedStressRadius = _requestedStressRadius;
                }
                _stressApplyAt = applied ? float.PositiveInfinity : Time.unscaledTime + 1f;
            }
            if (InputCaptured && _enemyDirty && Time.unscaledTime >= _enemyApplyAt)
            {
                var runtime = WaveByWave.Enemies.DotsEnemyRuntime.EnsureInstance();
                _enemyDirty = runtime == null || !runtime.SetStressTarget(_enemyTarget, _inventory.transform.position, _enemyRadius);
                _enemyApplyAt = Time.unscaledTime + 1f;
            }
            if (InputCaptured && _enemyLabel != null)
            {
                var runtime = WaveByWave.Enemies.DotsEnemyRuntime.Instance;
                _enemyLabel.text = $"Скелеты: {runtime?.StressCount ?? 0} • цель {_enemyTarget} / 3000";
            }
            if (InputCaptured && _shipLabel != null)
            {
                var ships = WaveByWave.Enemies.DotsEnemyShipRuntime.Instance;
                _shipLabel.text = $"Корабли: {ships?.AliveCount ?? 0} • заспавнить {_shipSpawnCount}";
            }
        }
        private void Build()
        {
            _canvas = new GameObject("Admin item spawner", typeof(RectTransform), typeof(Canvas),
                typeof(CanvasScaler), typeof(GraphicRaycaster));
            DontDestroyOnLoad(_canvas);
            _canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.GetComponent<Canvas>().sortingOrder = 200;
            var scaler = _canvas.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            _panel = new GameObject("Items", typeof(RectTransform), typeof(Image));
            _panel.transform.SetParent(_canvas.transform, false);
            var rect = _panel.GetComponent<RectTransform>(); rect.anchorMin = rect.anchorMax = Vector2.one * 0.5f;
            rect.sizeDelta = new Vector2(660f, 960f); _panel.GetComponent<Image>().color = new Color(0.035f, 0.055f, 0.075f, 0.98f);
            Label(_panel.transform, "Админ-панель • F2", new Vector2(0, 390), new Vector2(610, 40), Color.white);

            Label(_panel.transform, "DOTS / Steam нагрузочный тест", new Vector2(0, 345), new Vector2(600, 32),
                new Color(0.4f, 0.85f, 1f));
            _stressCountLabel = Label(_panel.transform, "Предметы: 0 / 3000", new Vector2(0, 310),
                new Vector2(570, 28), Color.white);
            _stressCountLabel.fontSize = 17;
            _stressCountSlider = CreateSlider(_panel.transform, new Vector2(0, 278), 0f, 3000f, 0f, true);
            _stressCountSlider.onValueChanged.AddListener(value =>
            {
                _requestedStressCount = Mathf.RoundToInt(value);
                _stressCountLabel.text = $"Предметы: {_requestedStressCount} / 3000";
                ScheduleStressApply();
            });

            _stressRadiusLabel = Label(_panel.transform, "Радиус: 15 м", new Vector2(0, 240),
                new Vector2(570, 28), Color.white);
            _stressRadiusLabel.fontSize = 17;
            _stressRadiusSlider = CreateSlider(_panel.transform, new Vector2(0, 208), 10f, 20f, 15f, false);
            _stressRadiusSlider.onValueChanged.AddListener(value =>
            {
                _requestedStressRadius = value;
                _stressRadiusLabel.text = $"Радиус: {value:0.0} м";
                ScheduleStressApply();
            });

            var scrollObject = new GameObject("Catalog", typeof(RectTransform), typeof(Image), typeof(Mask), typeof(ScrollRect));
            scrollObject.transform.SetParent(_panel.transform, false);
            var scrollRect = scrollObject.GetComponent<RectTransform>();
            scrollRect.sizeDelta = new Vector2(590f, 240f);
            scrollRect.anchoredPosition = new Vector2(0f, -285f);
            scrollObject.GetComponent<Image>().color = new Color(0, 0, 0, 0.1f);
            scrollObject.GetComponent<Mask>().showMaskGraphic = false;
            var contentObject = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            contentObject.transform.SetParent(scrollObject.transform, false);
            var content = contentObject.GetComponent<RectTransform>(); content.anchorMin = new Vector2(0, 1);
            content.anchorMax = Vector2.one; content.pivot = new Vector2(0.5f, 1); content.sizeDelta = Vector2.zero;
            var layout = contentObject.GetComponent<VerticalLayoutGroup>(); layout.spacing = 6f;
            layout.padding = new RectOffset(8, 8, 8, 8); layout.childControlHeight = true;
            layout.childForceExpandHeight = false; layout.childControlWidth = true;
            contentObject.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var scroll = scrollObject.GetComponent<ScrollRect>(); scroll.content = content; scroll.viewport = scrollRect;
            scroll.horizontal = false; scroll.scrollSensitivity = 30f;
            if (_inventory.Catalog != null)
                foreach (var item in _inventory.Catalog.Items)
                {
                    if (item == null) continue;
                    var id = item.Id;
                    var button = new GameObject(id, typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
                    button.transform.SetParent(content, false); button.GetComponent<LayoutElement>().preferredHeight = 42f;
                    button.GetComponent<Image>().color = new Color(0.1f, 0.15f, 0.19f, 1f);
                    button.GetComponent<Button>().targetGraphic = button.GetComponent<Image>();
                    button.GetComponent<Button>().onClick.AddListener(() => _inventory.SpawnAdminItem(id));
                    var label = Label(button.transform, item.DisplayName + "  •  " + item.Category + " / " + item.Rarity,
                        Vector2.zero, new Vector2(530, 38), item.RarityColor);
                    label.fontSize = 17;
                }
            var close = new GameObject("Close", typeof(RectTransform), typeof(Image), typeof(Button));
            close.transform.SetParent(_panel.transform, false); close.GetComponent<RectTransform>().sizeDelta = new Vector2(170, 35);
            close.GetComponent<RectTransform>().anchoredPosition = new Vector2(105, -455);
            close.GetComponent<Image>().color = new Color(0.25f, 0.32f, 0.37f);
            close.GetComponent<Button>().targetGraphic = close.GetComponent<Image>();
            close.GetComponent<Button>().onClick.AddListener(() => SetOpen(false));
            Label(close.transform, "Закрыть", Vector2.zero, new Vector2(160, 30), Color.white);

            var clear = new GameObject("Clear stress items", typeof(RectTransform), typeof(Image), typeof(Button));
            clear.transform.SetParent(_panel.transform, false); clear.GetComponent<RectTransform>().sizeDelta = new Vector2(190, 35);
            clear.GetComponent<RectTransform>().anchoredPosition = new Vector2(-105, -455);
            clear.GetComponent<Image>().color = new Color(0.45f, 0.17f, 0.15f);
            clear.GetComponent<Button>().targetGraphic = clear.GetComponent<Image>();
            clear.GetComponent<Button>().onClick.AddListener(() => _stressCountSlider.value = 0f);
            Label(clear.transform, "Очистить тест", Vector2.zero, new Vector2(180, 30), Color.white);
            _enemyLabel = Label(_panel.transform, "Скелеты: 0 / 3000", new Vector2(0, 160), new Vector2(570, 28),
                new Color(1f, 0.8f, 0.35f));
            _enemyLabel.fontSize = 17;
            _enemySlider = CreateSlider(_panel.transform, new Vector2(0, 130), 0, 3000, 0, true);
            _enemySlider.onValueChanged.AddListener(value =>
            {
                _enemyTarget = Mathf.RoundToInt(value); _enemyDirty = true; _enemyApplyAt = Time.unscaledTime + 0.2f;
            });
            var enemyRadiusLabel = Label(_panel.transform, "Радиус скелетов: 15 м", new Vector2(0, 90),
                new Vector2(570, 28), Color.white);
            enemyRadiusLabel.fontSize = 17;
            var enemyRadius = CreateSlider(_panel.transform, new Vector2(0, 58), 5, 80, 15, false);
            enemyRadius.onValueChanged.AddListener(value =>
            {
                _enemyRadius = value; enemyRadiusLabel.text = $"Радиус скелетов: {value:0.0} м";
            });
            clear.GetComponent<Button>().onClick.AddListener(() => { if (_enemySlider != null) _enemySlider.value = 0; });

            _shipLabel = Label(_panel.transform, "Корабли: 0 • заспавнить 12", new Vector2(0, 18),
                new Vector2(570, 28), new Color(1f, 0.45f, 0.28f));
            _shipLabel.fontSize = 17;
            _shipCountSlider = CreateSlider(_panel.transform, new Vector2(0, -12), 1, 1000, _shipSpawnCount, true);
            _shipCountSlider.onValueChanged.AddListener(value => _shipSpawnCount = Mathf.RoundToInt(value));
            _shipRadiusLabel = Label(_panel.transform, "Радиус кораблей: 350 м", new Vector2(0, -50),
                new Vector2(570, 28), Color.white);
            _shipRadiusLabel.fontSize = 17;
            _shipRadiusSlider = CreateSlider(_panel.transform, new Vector2(0, -80), 35, 1000,
                _shipSpawnRadius, false);
            _shipRadiusSlider.onValueChanged.AddListener(value =>
            {
                _shipSpawnRadius = value;
                _shipRadiusLabel.text = $"Радиус кораблей: {value:0} м";
            });
            var spawnShips = new GameObject("Spawn enemy ships", typeof(RectTransform), typeof(Image), typeof(Button));
            spawnShips.transform.SetParent(_panel.transform, false);
            spawnShips.GetComponent<RectTransform>().sizeDelta = new Vector2(260, 36);
            spawnShips.GetComponent<RectTransform>().anchoredPosition = new Vector2(0, -125);
            spawnShips.GetComponent<Image>().color = new Color(0.62f, 0.2f, 0.12f);
            spawnShips.GetComponent<Button>().targetGraphic = spawnShips.GetComponent<Image>();
            spawnShips.GetComponent<Button>().onClick.AddListener(SpawnEnemyShips);
            Label(spawnShips.transform, "Заспавнить корабли", Vector2.zero, new Vector2(250, 32), Color.white);
        }

        private void SpawnEnemyShips()
        {
            var runtime = WaveByWave.Enemies.DotsEnemyShipRuntime.EnsureInstance();
            if (runtime == null || !runtime.SpawnAt(_inventory.transform.position, _shipSpawnCount,
                    _shipSpawnRadius, (uint)Random.Range(1, int.MaxValue)))
                Debug.LogWarning("[Admin] Вражеские корабли не заспавнены: сервер не готов или достигнут лимит.");
        }

        private void ScheduleStressApply() => _stressApplyAt = Time.unscaledTime + 0.12f;

        private static Slider CreateSlider(Transform parent, Vector2 position, float minimum, float maximum,
            float value, bool wholeNumbers)
        {
            var root = new GameObject("Slider", typeof(RectTransform), typeof(Slider));
            root.transform.SetParent(parent, false);
            var rect = root.GetComponent<RectTransform>(); rect.sizeDelta = new Vector2(540f, 28f); rect.anchoredPosition = position;

            var background = new GameObject("Background", typeof(RectTransform), typeof(Image));
            background.transform.SetParent(root.transform, false);
            var backgroundRect = background.GetComponent<RectTransform>(); backgroundRect.anchorMin = Vector2.zero;
            backgroundRect.anchorMax = Vector2.one; backgroundRect.offsetMin = new Vector2(0, 8); backgroundRect.offsetMax = new Vector2(0, -8);
            background.GetComponent<Image>().color = new Color(0.08f, 0.11f, 0.14f, 1f);

            var fillArea = new GameObject("Fill Area", typeof(RectTransform)); fillArea.transform.SetParent(root.transform, false);
            var fillAreaRect = fillArea.GetComponent<RectTransform>(); fillAreaRect.anchorMin = Vector2.zero;
            fillAreaRect.anchorMax = Vector2.one; fillAreaRect.offsetMin = new Vector2(5, 8); fillAreaRect.offsetMax = new Vector2(-5, -8);
            var fill = new GameObject("Fill", typeof(RectTransform), typeof(Image)); fill.transform.SetParent(fillArea.transform, false);
            var fillRect = fill.GetComponent<RectTransform>(); fillRect.anchorMin = Vector2.zero; fillRect.anchorMax = Vector2.one;
            fillRect.offsetMin = fillRect.offsetMax = Vector2.zero; fill.GetComponent<Image>().color = new Color(0.16f, 0.67f, 0.9f);

            var handleArea = new GameObject("Handle Slide Area", typeof(RectTransform)); handleArea.transform.SetParent(root.transform, false);
            var handleAreaRect = handleArea.GetComponent<RectTransform>(); handleAreaRect.anchorMin = Vector2.zero;
            handleAreaRect.anchorMax = Vector2.one; handleAreaRect.offsetMin = new Vector2(8, 0); handleAreaRect.offsetMax = new Vector2(-8, 0);
            var handle = new GameObject("Handle", typeof(RectTransform), typeof(Image)); handle.transform.SetParent(handleArea.transform, false);
            var handleRect = handle.GetComponent<RectTransform>(); handleRect.sizeDelta = new Vector2(20f, 28f);
            handle.GetComponent<Image>().color = Color.white;

            var slider = root.GetComponent<Slider>(); slider.minValue = minimum; slider.maxValue = maximum;
            slider.wholeNumbers = wholeNumbers; slider.fillRect = fillRect; slider.handleRect = handleRect;
            slider.targetGraphic = handle.GetComponent<Image>(); slider.direction = Slider.Direction.LeftToRight; slider.value = value;
            return slider;
        }
        private static Text Label(Transform parent, string value, Vector2 position, Vector2 size, Color color)
        {
            var go = new GameObject("Label", typeof(RectTransform), typeof(Text)); go.transform.SetParent(parent, false);
            go.GetComponent<RectTransform>().sizeDelta = size; go.GetComponent<RectTransform>().anchoredPosition = position;
            var text = go.GetComponent<Text>(); text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = 21; text.alignment = TextAnchor.MiddleCenter; text.text = value; text.color = color;
            text.raycastTarget = false; return text;
        }
        private void SetOpen(bool open)
        {
            InputCaptured = open; if (_canvas != null) _canvas.SetActive(open);
            var unlocked = open || SessionMenuPresenter.InputCaptured;
            Cursor.visible = unlocked; Cursor.lockState = unlocked ? CursorLockMode.None : CursorLockMode.Locked;
        }
        private void OnDestroy()
        {
            if (InputCaptured) SetOpen(false);
            if (_canvas != null) Destroy(_canvas);
        }
    }
}
