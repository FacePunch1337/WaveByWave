using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using WaveByWave.Enemies;
using WaveByWave.Generation;
using WaveByWave.Items;
using WaveByWave.UI;

namespace WaveByWave.Player
{
    [DefaultExecutionOrder(-200)]
    public sealed class EquipmentAdminPanel : MonoBehaviour
    {
        private enum Section { Waves, Enemies, Items, Rings, Stress }
        private sealed class EnemySpawnControls
        {
            public EnemyKind Kind;
            public bool IsShip;
            public int Count = 1;
            public float Radius = 15f;
            public bool HealthBars;
            public Text CountLabel;
            public Text RadiusLabel;
            public Text HealthLabel;
        }
        private sealed class RingControls
        {
            public Image Background;
            public Text Title;
            public Text Detail;
            public Text Level;
        }

        private const int MaximumSpawnCount = 3000;
        private const int AdminShipGroup = -900001;
        public static bool InputCaptured { get; private set; }

        private readonly Dictionary<Section, GameObject> _sections = new();
        private readonly Dictionary<Section, Image> _tabs = new();
        private readonly Dictionary<EnemyKind, EnemySpawnControls> _enemyControls = new();
        private readonly Dictionary<PlayerRingStat, RingControls> _ringControls = new();
        private PlayerInventory _inventory;
        private NetworkPlayerController _player;
        private GameObject _canvas, _panel;
        private Transform _sectionHost;
        private Text _status, _ringSummary;
        private Section _activeSection;
        private float _nextRingRefresh;
        private int _stressItemCount;
        private float _stressItemRadius = 30f;

        public void Initialize(PlayerInventory inventory)
        {
            _inventory = inventory;
            _player = inventory != null ? inventory.GetComponent<NetworkPlayerController>() : null;
        }

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
            if (InputCaptured && _activeSection == Section.Rings && Time.unscaledTime >= _nextRingRefresh)
            {
                _nextRingRefresh = Time.unscaledTime + 0.15f;
                RefreshRings();
            }
        }

        private void Build()
        {
            _canvas = GameUiPrefabs.Create("Menus/Admin", owner: this) ?? BuildFallbackShell();
            GameUiPrefabs.Persist(_canvas);
            _panel = _canvas.transform.Find("Items")?.gameObject;
            if (_panel == null)
            {
                _panel = CreateImage("Items", _canvas.transform, new Color(0.025f, 0.04f, 0.06f, 0.985f)).gameObject;
                UiDefaults.Rect(_panel.GetComponent<RectTransform>(), new Vector2(1600f, 940f), Vector2.zero);
            }
            PrepareShell();
            BuildSections();
            ShowSection(Section.Waves);
        }

        private GameObject BuildFallbackShell()
        {
            var root = UiDefaults.Canvas("Admin", 200);
            if (Application.isPlaying) DontDestroyOnLoad(root);
            var panel = CreateImage("Items", root.transform, new Color(0.025f, 0.04f, 0.06f, 0.985f));
            UiDefaults.Rect(panel.rectTransform, new Vector2(1600f, 940f), Vector2.zero);
            return root;
        }

        private void PrepareShell()
        {
            var panelRect = _panel.GetComponent<RectTransform>();
            panelRect.sizeDelta = new Vector2(Mathf.Max(1500f, panelRect.sizeDelta.x),
                Mathf.Max(900f, panelRect.sizeDelta.y));
            var body = _panel.transform.Find("Body");
            if (body == null)
            {
                foreach (Transform child in _panel.transform)
                {
                    child.name = "Obsolete " + child.GetSiblingIndex();
                    child.gameObject.SetActive(false);
                    Destroy(child.gameObject);
                }
                var title = CreateText("Title", _panel.transform, "АДМИН-ПАНЕЛЬ · F2", 30, Color.white,
                    TextAnchor.MiddleLeft);
                SetAnchors(title.rectTransform, new Vector2(0f, 1f), Vector2.one,
                    new Vector2(32f, -68f), new Vector2(-100f, -18f));
                var close = CreateButton("Close", _panel.transform, "×", new Color(0.38f, 0.13f, 0.13f, 1f));
                var closeRect = close.GetComponent<RectTransform>();
                closeRect.anchorMin = closeRect.anchorMax = Vector2.one;
                closeRect.pivot = Vector2.one;
                closeRect.anchoredPosition = new Vector2(-20f, -18f);
                closeRect.sizeDelta = new Vector2(54f, 50f);
                body = new GameObject("Body", typeof(RectTransform)).transform;
                body.SetParent(_panel.transform, false);
                SetAnchors((RectTransform)body, Vector2.zero, Vector2.one,
                    new Vector2(24f, 54f), new Vector2(-24f, -78f));
            }
            var closeButton = _panel.transform.Find("Close")?.GetComponent<Button>();
            if (closeButton != null)
            {
                closeButton.onClick.RemoveAllListeners();
                closeButton.onClick.AddListener(() => SetOpen(false));
            }
            foreach (Transform child in body)
            {
                child.gameObject.SetActive(false);
                Destroy(child.gameObject);
            }
            BuildBody(body);
        }

        private void BuildBody(Transform body)
        {
            var navigation = CreateImage("Navigation", body, new Color(0.035f, 0.075f, 0.1f, 1f));
            SetAnchors(navigation.rectTransform, Vector2.zero, new Vector2(0f, 1f), Vector2.zero,
                new Vector2(220f, 0f));
            var content = CreateImage("Content", body, new Color(0.018f, 0.032f, 0.048f, 0.88f));
            SetAnchors(content.rectTransform, Vector2.zero, Vector2.one,
                new Vector2(236f, 38f), Vector2.zero);
            var sectionHost = new GameObject("Sections", typeof(RectTransform));
            sectionHost.transform.SetParent(content.transform, false);
            SetAnchors(sectionHost.GetComponent<RectTransform>(), Vector2.zero, Vector2.one,
                new Vector2(12f, 12f), new Vector2(-12f, -12f));

            var tabs = new[]
            {
                (Section.Waves, "ВОЛНЫ"), (Section.Enemies, "ВРАГИ"),
                (Section.Items, "ПРЕДМЕТЫ"), (Section.Rings, "КОЛЬЦА И СТАТЫ"),
                (Section.Stress, "СТРЕСС-ТЕСТ")
            };
            for (var i = 0; i < tabs.Length; i++)
            {
                var captured = tabs[i].Item1;
                var button = CreateButton(captured.ToString(), navigation.transform, tabs[i].Item2,
                    new Color(0.07f, 0.14f, 0.19f, 1f));
                var rect = button.GetComponent<RectTransform>();
                rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
                rect.pivot = new Vector2(0.5f, 1f);
                rect.sizeDelta = new Vector2(196f, 62f);
                rect.anchoredPosition = new Vector2(0f, -14f - i * 72f);
                button.onClick.AddListener(() => ShowSection(captured));
                _tabs[captured] = button.GetComponent<Image>();
            }
            var hint = CreateText("Hint", navigation.transform,
                "Изменения применяются\nна сервере сразу", 15, new Color(0.6f, 0.72f, 0.78f),
                TextAnchor.LowerCenter);
            SetAnchors(hint.rectTransform, Vector2.zero, Vector2.one,
                new Vector2(8f, 12f), new Vector2(-8f, -330f));
            _status = CreateText("Status", body, "", 16, new Color(0.55f, 0.85f, 1f), TextAnchor.MiddleLeft);
            SetAnchors(_status.rectTransform, Vector2.zero, new Vector2(1f, 0f),
                new Vector2(246f, 2f), new Vector2(-12f, 34f));
            _sectionHost = sectionHost.transform;
        }

        private void BuildSections()
        {
            BuildWavesSection();
            BuildEnemiesSection();
            BuildItemsSection();
            BuildRingsSection();
            BuildStressSection();
        }

        private Transform CreateSection(Section key, string title, bool grid = false)
        {
            var root = new GameObject(key.ToString(), typeof(RectTransform));
            root.transform.SetParent(_sectionHost, false);
            UiDefaults.Stretch(root.GetComponent<RectTransform>());
            var heading = CreateText("Heading", root.transform, title, 27, Color.white, TextAnchor.MiddleLeft);
            SetAnchors(heading.rectTransform, new Vector2(0f, 1f), Vector2.one,
                new Vector2(8f, -54f), new Vector2(-8f, -4f));
            var scrollRoot = CreateImage("Scroll", root.transform, new Color(0f, 0f, 0f, 0.12f));
            SetAnchors(scrollRoot.rectTransform, Vector2.zero, Vector2.one,
                new Vector2(4f, 4f), new Vector2(-4f, -62f));
            scrollRoot.gameObject.AddComponent<RectMask2D>();
            var scroll = scrollRoot.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.scrollSensitivity = 35f;
            var content = new GameObject("Content", typeof(RectTransform));
            content.transform.SetParent(scrollRoot.transform, false);
            var contentRect = content.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0f, 1f);
            contentRect.anchorMax = Vector2.one;
            contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.sizeDelta = Vector2.zero;
            if (grid)
            {
                var layout = content.AddComponent<GridLayoutGroup>();
                layout.padding = new RectOffset(12, 12, 12, 12);
                layout.spacing = new Vector2(12f, 12f);
                layout.cellSize = new Vector2(188f, 152f);
                layout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
                layout.constraintCount = 6;
                layout.childAlignment = TextAnchor.UpperLeft;
            }
            else
            {
                var layout = content.AddComponent<VerticalLayoutGroup>();
                layout.padding = new RectOffset(10, 10, 10, 10);
                layout.spacing = 10f;
                layout.childControlWidth = true;
                layout.childControlHeight = true;
                layout.childForceExpandWidth = true;
                layout.childForceExpandHeight = false;
            }
            content.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.viewport = scrollRoot.rectTransform;
            scroll.content = contentRect;
            _sections[key] = root;
            return content.transform;
        }

        private void BuildWavesSection()
        {
            var content = CreateSection(Section.Waves, "НОЧНЫЕ ВОЛНЫ");
            var settings = Resources.Load<NightWaveSettings>("NightWaveSettings");
            if (settings?.Waves == null || settings.Waves.Length == 0)
            {
                CreateInfoRow(content, "NightWaveSettings не содержит волн.", 70f);
                return;
            }
            for (var i = 0; i < settings.Waves.Length; i++)
            {
                var wave = settings.Waves[i];
                if (wave == null) continue;
                var index = i;
                var row = CreateRow(content, $"Wave {i + 1}", 98f);
                var name = string.IsNullOrWhiteSpace(wave.Name) ? $"Wave {i + 1}" : wave.Name;
                var title = CreateText("Title", row.transform, $"{i + 1}. {name}", 23, Color.white, TextAnchor.MiddleLeft);
                SetAnchors(title.rectTransform, Vector2.zero, Vector2.one,
                    new Vector2(18f, 46f), new Vector2(-210f, -8f));
                var entries = 0;
                foreach (var fragment in wave.Fragments ?? Array.Empty<NightWaveFragment>())
                    entries += fragment?.Enemies?.Length ?? 0;
                var detail = CreateText("Detail", row.transform,
                    $"Поле боя: {wave.BattlefieldRadius:0} м   •   Фрагментов: {wave.Fragments?.Length ?? 0}   •   Групп врагов: {entries}",
                    17, new Color(0.65f, 0.78f, 0.84f), TextAnchor.MiddleLeft);
                SetAnchors(detail.rectTransform, Vector2.zero, Vector2.one,
                    new Vector2(18f, 10f), new Vector2(-210f, -50f));
                var start = CreateButton("Start", row.transform, "ЗАПУСТИТЬ", new Color(0.12f, 0.42f, 0.55f, 1f));
                SetRightButton(start.GetComponent<RectTransform>(), 174f, 48f, 16f);
                start.onClick.AddListener(() => StartNightWave(index));
            }
        }

        private void BuildEnemiesSection()
        {
            var content = CreateSection(Section.Enemies, "СПАВН ВРАГОВ");
            var clearRow = CreateRow(content, "Actions", 58f);
            var clear = CreateButton("Clear", clearRow.transform, "УДАЛИТЬ ТЕСТОВЫХ ВРАГОВ", new Color(0.47f, 0.12f, 0.12f, 1f));
            UiDefaults.Rect(clear.GetComponent<RectTransform>(), new Vector2(330f, 40f), Vector2.zero);
            clear.onClick.AddListener(ClearAdminEnemies);
            var runtime = DotsEnemyRuntime.EnsureInstance();
            foreach (EnemyKind kind in Enum.GetValues(typeof(EnemyKind)))
            {
                var catalog = runtime.GetCatalog(kind);
                if (catalog == null) continue;
                var controls = new EnemySpawnControls
                {
                    Kind = kind,
                    Count = 1,
                    Radius = kind == EnemyKind.Shark ? 30f : 15f,
                    HealthBars = catalog.ShowHealthBars
                };
                _enemyControls[kind] = controls;
                BuildEnemyRow(content, controls, catalog.DisplayName);
            }
            BuildEnemyRow(content, new EnemySpawnControls { IsShip = true, Count = 1, Radius = 600f }, "Enemy ships");
        }

        private void BuildEnemyRow(Transform content, EnemySpawnControls controls, string displayName)
        {
            var row = CreateRow(content, displayName, 178f);
            var title = CreateText("Title", row.transform, displayName, 22,
                controls.IsShip ? new Color(1f, 0.55f, 0.28f) : Color.white, TextAnchor.MiddleLeft);
            SetAnchors(title.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(18f, -42f), new Vector2(-360f, -8f));
            controls.CountLabel = CreateText("CountLabel", row.transform, "Количество: 1", 16, Color.white, TextAnchor.MiddleLeft);
            UiDefaults.Rect(controls.CountLabel.rectTransform, new Vector2(185f, 28f), new Vector2(-480f, 34f));
            var countSlider = CreateSlider(row.transform, new Vector2(-180f, 34f), new Vector2(410f, 28f),
                1f, MaximumSpawnCount, controls.Count, true);
            countSlider.onValueChanged.AddListener(value =>
            {
                controls.Count = Mathf.RoundToInt(value);
                controls.CountLabel.text = $"Количество: {controls.Count}";
            });
            controls.RadiusLabel = CreateText("RadiusLabel", row.transform,
                $"Радиус: {controls.Radius:0} м", 16, Color.white, TextAnchor.MiddleLeft);
            UiDefaults.Rect(controls.RadiusLabel.rectTransform, new Vector2(185f, 28f), new Vector2(-480f, -10f));
            var radiusSlider = CreateSlider(row.transform, new Vector2(-180f, -10f), new Vector2(410f, 28f),
                2f, 1000f, controls.Radius, false);
            radiusSlider.onValueChanged.AddListener(value =>
            {
                controls.Radius = value;
                controls.RadiusLabel.text = $"Радиус: {value:0} м";
            });
            var spawn = CreateButton("Spawn", row.transform, "ЗАСПАВНИТЬ", new Color(0.12f, 0.42f, 0.55f, 1f));
            UiDefaults.Rect(spawn.GetComponent<RectTransform>(), new Vector2(205f, 48f), new Vector2(500f, 24f));
            spawn.onClick.AddListener(() => SpawnEnemy(controls, displayName));
            if (!controls.IsShip)
            {
                var health = CreateButton("HealthBars", row.transform, "", new Color(0.16f, 0.22f, 0.25f, 1f));
                UiDefaults.Rect(health.GetComponent<RectTransform>(), new Vector2(205f, 38f), new Vector2(500f, -34f));
                controls.HealthLabel = health.GetComponentInChildren<Text>();
                void RefreshHealth() => controls.HealthLabel.text = $"HP-бары: {(controls.HealthBars ? "ВКЛ" : "ВЫКЛ")}";
                RefreshHealth();
                health.onClick.AddListener(() =>
                {
                    controls.HealthBars = !controls.HealthBars;
                    DotsEnemyRuntime.Instance?.SetSpeciesHealthBars(controls.Kind, controls.HealthBars);
                    RefreshHealth();
                });
            }
        }

        private void BuildItemsSection()
        {
            var content = CreateSection(Section.Items, "ВСЕ ПРЕДМЕТЫ · НАЖМИ ДЛЯ СПАВНА", true);
            if (_inventory.Catalog == null) return;
            foreach (var item in _inventory.Catalog.Items)
            {
                if (item == null) continue;
                var card = CreateImage(item.Id, content, new Color(0.055f, 0.09f, 0.12f, 1f));
                var button = card.gameObject.AddComponent<Button>();
                button.targetGraphic = card;
                var itemId = item.Id;
                button.onClick.AddListener(() =>
                {
                    _inventory.SpawnAdminItem(itemId);
                    SetStatus($"Создан предмет: {item.DisplayName}");
                });
                var iconBack = CreateImage("IconBackground", card.transform, new Color(0.02f, 0.03f, 0.04f, 0.7f));
                UiDefaults.Rect(iconBack.rectTransform, new Vector2(82f, 82f), new Vector2(0f, 25f));
                var icon = CreateImage("Icon", iconBack.transform, Color.white);
                UiDefaults.Stretch(icon.rectTransform, 5f);
                icon.sprite = item.Icon;
                icon.preserveAspect = true;
                icon.enabled = item.Icon != null;
                var title = CreateText("Title", card.transform, item.DisplayName, 15, item.RarityColor, TextAnchor.MiddleCenter);
                SetAnchors(title.rectTransform, Vector2.zero, new Vector2(1f, 0f),
                    new Vector2(5f, 24f), new Vector2(-5f, 54f));
                var rarity = CreateText("Rarity", card.transform, item.Rarity.ToString(), 12,
                    new Color(item.RarityColor.r, item.RarityColor.g, item.RarityColor.b, 0.8f), TextAnchor.MiddleCenter);
                SetAnchors(rarity.rectTransform, Vector2.zero, new Vector2(1f, 0f),
                    new Vector2(5f, 4f), new Vector2(-5f, 25f));
            }
        }

        private void BuildRingsSection()
        {
            var content = CreateSection(Section.Rings, "ХАРАКТЕРИСТИКИ И КОЛЬЦА");
            _ringSummary = CreateInfoRow(content, "", 64f);
            var catalog = _player != null ? _player.RingCatalog : Resources.Load<PlayerRingCatalog>("PlayerRingCatalog");
            if (catalog?.Rings == null) return;
            foreach (var ring in catalog.Rings)
            {
                if (ring.BaseBonus <= 0f || _ringControls.ContainsKey(ring.Stat)) continue;
                BuildRingRow(content, ring);
            }
            RefreshRings();
        }

        private void BuildStressSection()
        {
            var content = CreateSection(Section.Stress, "СТРЕСС-ТЕСТ ПРЕДМЕТОВ");
            CreateInfoRow(content,
                "Создаёт выбранное количество сетевых предметов вокруг игрока. Максимум: 3000 предметов в радиусе 30 м.",
                70f);
            var row = CreateRow(content, "Stress settings", 190f);
            var countLabel = CreateText("CountLabel", row.transform, "Предметы: 0 / 3000", 18,
                Color.white, TextAnchor.MiddleLeft);
            UiDefaults.Rect(countLabel.rectTransform, new Vector2(250f, 30f), new Vector2(-430f, 48f));
            var count = CreateSlider(row.transform, new Vector2(-90f, 48f), new Vector2(610f, 30f),
                0f, MaximumSpawnCount, 0f, true);
            count.onValueChanged.AddListener(value =>
            {
                _stressItemCount = Mathf.RoundToInt(value);
                countLabel.text = $"Предметы: {_stressItemCount} / {MaximumSpawnCount}";
            });
            var radiusLabel = CreateText("RadiusLabel", row.transform, "Радиус: 30 м", 18,
                Color.white, TextAnchor.MiddleLeft);
            UiDefaults.Rect(radiusLabel.rectTransform, new Vector2(250f, 30f), new Vector2(-430f, 2f));
            var radius = CreateSlider(row.transform, new Vector2(-90f, 2f), new Vector2(610f, 30f),
                2f, 30f, 30f, false);
            radius.onValueChanged.AddListener(value =>
            {
                _stressItemRadius = value;
                radiusLabel.text = $"Радиус: {value:0} м";
            });
            var spawn = CreateButton("Apply", row.transform, "ЗАПУСТИТЬ", new Color(0.12f, 0.42f, 0.55f, 1f));
            UiDefaults.Rect(spawn.GetComponent<RectTransform>(), new Vector2(220f, 48f), new Vector2(465f, 30f));
            spawn.onClick.AddListener(ApplyStressItems);
            var clear = CreateButton("Clear", row.transform, "ОЧИСТИТЬ", new Color(0.47f, 0.12f, 0.12f, 1f));
            UiDefaults.Rect(clear.GetComponent<RectTransform>(), new Vector2(220f, 42f), new Vector2(465f, -32f));
            clear.onClick.AddListener(() =>
            {
                _stressItemCount = 0;
                count.SetValueWithoutNotify(0f);
                countLabel.text = $"Предметы: 0 / {MaximumSpawnCount}";
                ApplyStressItems();
            });
        }

        private void ApplyStressItems()
        {
            if (!_inventory.SetAdminStressItems(_stressItemCount, _stressItemRadius))
            {
                SetStatus("Стресс-тест не запущен: сервер предметов ещё не готов.", true);
                return;
            }
            SetStatus(_stressItemCount > 0
                ? $"Стресс-тест: {_stressItemCount} предметов, радиус {_stressItemRadius:0} м."
                : "Предметы стресс-теста удалены.");
        }

        private void BuildRingRow(Transform content, PlayerRingDefinition definition)
        {
            var row = CreateRow(content, definition.Stat.ToString(), 112f);
            var select = new GameObject("Select", typeof(RectTransform), typeof(Image), typeof(Button));
            select.transform.SetParent(row.transform, false);
            SetAnchors(select.GetComponent<RectTransform>(), Vector2.zero, Vector2.one,
                new Vector2(8f, 8f), new Vector2(-340f, -8f));
            var background = select.GetComponent<Image>();
            background.color = new Color(0.055f, 0.1f, 0.13f, 1f);
            select.GetComponent<Button>().targetGraphic = background;
            var iconBack = CreateImage("Badge", select.transform, new Color(0.17f, 0.22f, 0.25f, 1f));
            var badgeRect = iconBack.rectTransform;
            badgeRect.anchorMin = badgeRect.anchorMax = new Vector2(0f, 0.5f);
            badgeRect.pivot = new Vector2(0f, 0.5f);
            badgeRect.anchoredPosition = new Vector2(10f, 0f);
            badgeRect.sizeDelta = new Vector2(78f, 78f);
            var icon = CreateImage("Icon", iconBack.transform, Color.white);
            UiDefaults.Stretch(icon.rectTransform, 7f);
            icon.sprite = definition.Icon;
            icon.preserveAspect = true;
            icon.enabled = definition.Icon != null;
            var title = CreateText("Title", select.transform, definition.DisplayName, 21, Color.white, TextAnchor.MiddleLeft);
            SetAnchors(title.rectTransform, Vector2.zero, Vector2.one,
                new Vector2(104f, 48f), new Vector2(-10f, -8f));
            var detail = CreateText("Detail", select.transform, "", 16,
                new Color(0.65f, 0.76f, 0.82f), TextAnchor.MiddleLeft);
            SetAnchors(detail.rectTransform, Vector2.zero, Vector2.one,
                new Vector2(104f, 10f), new Vector2(-10f, -50f));
            var minus = CreateButton("Decrease", row.transform, "−", new Color(0.34f, 0.14f, 0.14f, 1f));
            UiDefaults.Rect(minus.GetComponent<RectTransform>(), new Vector2(58f, 58f), new Vector2(400f, 0f));
            var level = CreateText("Level", row.transform, "0", 21, Color.white, TextAnchor.MiddleCenter);
            UiDefaults.Rect(level.rectTransform, new Vector2(120f, 58f), new Vector2(490f, 0f));
            var plus = CreateButton("Increase", row.transform, "+", new Color(0.12f, 0.36f, 0.22f, 1f));
            UiDefaults.Rect(plus.GetComponent<RectTransform>(), new Vector2(58f, 58f), new Vector2(580f, 0f));
            _ringControls[definition.Stat] = new RingControls
            {
                Background = background, Title = title, Detail = detail, Level = level
            };
            select.GetComponent<Button>().onClick.AddListener(() => ToggleRing(definition.Stat));
            minus.onClick.AddListener(() => ChangeRingLevel(definition.Stat, -1));
            plus.onClick.AddListener(() => ChangeRingLevel(definition.Stat, 1));
        }

        private void ShowSection(Section section)
        {
            _activeSection = section;
            foreach (var pair in _sections) pair.Value.SetActive(pair.Key == section);
            foreach (var pair in _tabs)
                pair.Value.color = pair.Key == section
                    ? new Color(0.12f, 0.45f, 0.57f, 1f)
                    : new Color(0.07f, 0.14f, 0.19f, 1f);
            if (section == Section.Rings) RefreshRings();
        }

        private void SpawnEnemy(EnemySpawnControls controls, string displayName)
        {
            if (controls.IsShip)
            {
                var ships = DotsEnemyShipRuntime.EnsureInstance();
                if (ships == null || !ships.SpawnAt(_inventory.transform.position, controls.Count,
                        controls.Radius, (uint)UnityEngine.Random.Range(1, int.MaxValue), AdminShipGroup))
                {
                    SetStatus("Корабли не созданы: достигнут лимит или сервер не готов.", true);
                    return;
                }
            }
            else
            {
                var runtime = DotsEnemyRuntime.EnsureInstance();
                var health = controls.HealthBars ? EnemyHealthBarMode.Show : EnemyHealthBarMode.Hide;
                if (runtime == null || !runtime.SpawnAdminSpecies(controls.Kind, _inventory.transform.position,
                        controls.Count, controls.Radius, health))
                {
                    SetStatus($"{displayName}: не удалось создать врагов. Проверьте bake и лимит.", true);
                    return;
                }
            }
            SetStatus($"{displayName}: запрошено {controls.Count}, радиус {controls.Radius:0} м.");
        }

        private void ClearAdminEnemies()
        {
            DotsEnemyRuntime.Instance?.ClearAdminSpecies();
            DotsEnemyShipRuntime.Instance?.DespawnGroup(AdminShipGroup);
            SetStatus("Тестовые враги и корабли удалены.");
        }

        private void StartNightWave(int index)
        {
            var controller = NightWaveController.Active ?? FindFirstObjectByType<NightWaveController>();
            if (controller == null || !controller.StartAdminWaveServer(index))
            {
                SetStatus($"Не удалось запустить волну {index + 1}.", true);
                return;
            }
            SetOpen(false);
        }

        private void ToggleRing(PlayerRingStat stat)
        {
            if (_player == null) return;
            var target = _player.RingLevel(stat) > 0 ? 0 : 1;
            if (!_player.AdminSetRingLevelServer(stat, target))
                SetStatus("Нельзя надеть кольцо: все четыре слота заняты.", true);
            else SetStatus(target > 0 ? "Кольцо надето." : "Кольцо снято.");
            RefreshRings();
        }

        private void ChangeRingLevel(PlayerRingStat stat, int delta)
        {
            if (_player == null) return;
            var target = Mathf.Max(0, _player.RingLevel(stat) + delta);
            if (!_player.AdminSetRingLevelServer(stat, target))
                SetStatus("Нельзя добавить кольцо: все четыре слота заняты.", true);
            RefreshRings();
        }

        private void RefreshRings()
        {
            if (_player == null || _ringSummary == null) return;
            var catalog = _player.RingCatalog;
            _ringSummary.text = $"Надето: {_player.DistinctRingCount} / {catalog.MaximumDistinctRings}   •   " +
                $"Атака ×{_player.AttackSpeedMultiplier:0.##}   Перезарядка ×{_player.ReloadSpeedMultiplier:0.##}   " +
                $"Область ×{_player.MeleeAreaMultiplier:0.##}   Снарядов: {_player.ProjectileCount}";
            foreach (var pair in _ringControls)
            {
                var level = _player.RingLevel(pair.Key);
                var value = _player.RingValue(pair.Key);
                var equipped = level > 0;
                var controls = pair.Value;
                controls.Background.color = equipped
                    ? new Color(0.06f, 0.28f, 0.16f, 1f)
                    : new Color(0.055f, 0.1f, 0.13f, 1f);
                controls.Title.color = equipped ? new Color(0.28f, 1f, 0.48f) : Color.white;
                controls.Level.text = $"Ур. {level}";
                controls.Detail.text = equipped
                    ? $"Бонус: {PlayerRingCatalog.FormatBonus(pair.Key, value)} · нажми, чтобы снять"
                    : $"За уровень: {PlayerRingCatalog.FormatBonus(pair.Key, catalog.Bonus(pair.Key, ItemRarity.Common))} · нажми, чтобы надеть";
            }
        }

        private void SetStatus(string message, bool error = false)
        {
            if (_status == null) return;
            _status.text = message;
            _status.color = error ? new Color(1f, 0.42f, 0.32f) : new Color(0.55f, 0.85f, 1f);
        }

        private static Image CreateRow(Transform parent, string name, float height)
        {
            var row = CreateImage(name, parent, new Color(0.045f, 0.075f, 0.1f, 1f));
            row.gameObject.AddComponent<LayoutElement>().preferredHeight = height;
            return row;
        }

        private static Text CreateInfoRow(Transform parent, string text, float height)
        {
            var row = CreateRow(parent, "Info", height);
            var label = CreateText("Label", row.transform, text, 17,
                new Color(0.65f, 0.8f, 0.87f), TextAnchor.MiddleCenter);
            UiDefaults.Stretch(label.rectTransform, 8f);
            return label;
        }

        private static Image CreateImage(string name, Transform parent, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var image = go.GetComponent<Image>();
            image.color = color;
            return image;
        }

        private static Text CreateText(string name, Transform parent, string value, int size, Color color,
            TextAnchor alignment)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var text = go.GetComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = size;
            text.alignment = alignment;
            text.text = value;
            text.color = color;
            text.raycastTarget = false;
            return text;
        }

        private static Button CreateButton(string name, Transform parent, string value, Color color)
        {
            var image = CreateImage(name, parent, color);
            var button = image.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            var label = CreateText("Label", image.transform, value, 18, Color.white, TextAnchor.MiddleCenter);
            UiDefaults.Stretch(label.rectTransform, 3f);
            return button;
        }

        private static Slider CreateSlider(Transform parent, Vector2 position, Vector2 size,
            float minimum, float maximum, float value, bool wholeNumbers)
        {
            var root = new GameObject("Slider", typeof(RectTransform), typeof(Slider));
            root.transform.SetParent(parent, false);
            UiDefaults.Rect(root.GetComponent<RectTransform>(), size, position);
            var background = CreateImage("Background", root.transform, new Color(0.015f, 0.025f, 0.035f, 1f));
            SetAnchors(background.rectTransform, Vector2.zero, Vector2.one, new Vector2(0f, 9f), new Vector2(0f, -9f));
            var fillArea = new GameObject("Fill Area", typeof(RectTransform));
            fillArea.transform.SetParent(root.transform, false);
            SetAnchors(fillArea.GetComponent<RectTransform>(), Vector2.zero, Vector2.one,
                new Vector2(5f, 9f), new Vector2(-5f, -9f));
            var fill = CreateImage("Fill", fillArea.transform, new Color(0.12f, 0.6f, 0.82f, 1f));
            UiDefaults.Stretch(fill.rectTransform);
            var handleArea = new GameObject("Handle Slide Area", typeof(RectTransform));
            handleArea.transform.SetParent(root.transform, false);
            SetAnchors(handleArea.GetComponent<RectTransform>(), Vector2.zero, Vector2.one,
                new Vector2(9f, 0f), new Vector2(-9f, 0f));
            var handle = CreateImage("Handle", handleArea.transform, Color.white);
            handle.rectTransform.sizeDelta = new Vector2(18f, 28f);
            var slider = root.GetComponent<Slider>();
            slider.minValue = minimum;
            slider.maxValue = maximum;
            slider.wholeNumbers = wholeNumbers;
            slider.fillRect = fill.rectTransform;
            slider.handleRect = handle.rectTransform;
            slider.targetGraphic = handle;
            slider.value = value;
            return slider;
        }

        private static void SetAnchors(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax,
            Vector2 offsetMin, Vector2 offsetMax)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
        }

        private static void SetRightButton(RectTransform rect, float width, float height, float margin)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(1f, 0.5f);
            rect.pivot = new Vector2(1f, 0.5f);
            rect.anchoredPosition = new Vector2(-margin, 0f);
            rect.sizeDelta = new Vector2(width, height);
        }

        private void SetOpen(bool open)
        {
            InputCaptured = open;
            if (_canvas != null) _canvas.SetActive(open);
            if (open && _activeSection == Section.Rings) RefreshRings();
            var unlocked = open || SessionMenuPresenter.InputCaptured;
            Cursor.visible = unlocked;
            Cursor.lockState = unlocked ? CursorLockMode.None : CursorLockMode.Locked;
        }

        private void OnDestroy()
        {
            if (!Application.isPlaying) return;
            if (InputCaptured && GameUiPrefabs.IsOwnedBy(_canvas, this)) SetOpen(false);
            if (_canvas != null) GameUiPrefabs.Release(_canvas, this);
        }
    }
}
