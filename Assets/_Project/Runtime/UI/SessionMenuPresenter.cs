using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using WaveByWave.Core;
using WaveByWave.Networking;

namespace WaveByWave.UI
{
    public sealed class SessionMenuPresenter : MonoBehaviour, IServiceConsumer
    {
        public static bool InputCaptured { get; private set; }

        [SerializeField] private GameObject panel;
        [SerializeField] private Text title;
        [SerializeField] private Text status;
        [SerializeField] private Text hint;
        [SerializeField] private Button inviteButton;
        [SerializeField] private Button startButton;
        [SerializeField] private Button localHostButton;
        [SerializeField] private Button localClientButton;
        [SerializeField] private Button returnToPortButton;
        [SerializeField] private Button closeButton;

        private NetworkSessionCoordinator _session;

        public void Inject(IServiceResolver services)
        {
            _session = services.Resolve<NetworkSessionCoordinator>();
            Bind();
            Refresh();
        }

        private void Awake()
        {
            inviteButton?.onClick.AddListener(() => _session?.OpenInviteOverlay());
            startButton?.onClick.AddListener(() => _session?.StartVoyage());
            localHostButton?.onClick.AddListener(() => _session?.StartLocalHost());
            localClientButton?.onClick.AddListener(() => _session?.StartLocalClient());
            returnToPortButton?.onClick.AddListener(() => _session?.ReturnToPort());
            closeButton?.onClick.AddListener(() => SetOpen(false));
            SetOpen(false);
        }

        private void Start()
        {
            if (_session == null && NetworkSessionCoordinator.Instance != null)
            {
                _session = NetworkSessionCoordinator.Instance;
                Bind();
                Refresh();
            }
        }

        private void Bind()
        {
            if (_session == null)
                return;

            _session.StateChanged -= Refresh;
            _session.StateChanged += Refresh;
        }

        private void Update()
        {
            if (!WaveByWave.Customization.CustomizationMenu.InputCaptured &&
                Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
                SetOpen(panel != null && !panel.activeSelf);

            Refresh();
        }

        private void Refresh()
        {
            if (_session == null)
                return;

            var inPort = SceneManager.GetActiveScene().name == GameScenes.Port;
            if (title != null)
                title.text = inPort ? "ПОРТ" : "МЕНЮ ПЛАВАНИЯ";
            if (status != null)
                status.text = _session.Status;
            if (hint != null)
                hint.text = inPort
                    ? "WASD — движение  •  Shift — спринт  •  Space — прыжок  •  E — взаимодействие"
                    : "Штурвал: A/D  •  Парус: W свернуть, S развернуть  •  Мачта: A/D  •  E — взаимодействие";

            var manager = NetworkManager.Singleton;
            var isHost = manager != null && manager.IsServer;
            if (inviteButton != null)
                inviteButton.gameObject.SetActive(inPort && _session.SteamAvailable);
            if (startButton != null)
                startButton.gameObject.SetActive(inPort && isHost);
            if (localHostButton != null)
                localHostButton.gameObject.SetActive(inPort);
            if (localClientButton != null)
                localClientButton.gameObject.SetActive(inPort);
            if (returnToPortButton != null)
                returnToPortButton.gameObject.SetActive(!inPort && isHost);
        }

        private void SetOpen(bool open)
        {
            if (panel != null)
                panel.SetActive(open);

            InputCaptured = open;
            Cursor.visible = open;
            Cursor.lockState = open ? CursorLockMode.None : CursorLockMode.Locked;
        }

        private void OnDestroy()
        {
            if (_session != null)
                _session.StateChanged -= Refresh;
            InputCaptured = false;
        }
    }
}
