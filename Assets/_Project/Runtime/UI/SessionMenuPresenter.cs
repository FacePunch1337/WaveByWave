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
        private GameObject _prefabView;

        public void Inject(IServiceResolver services)
        {
            _session = services.Resolve<NetworkSessionCoordinator>();
            Bind();
            Refresh();
        }

        private void Awake()
        {
            _prefabView=GameUiPrefabs.Create("Menus/Session", owner: this);
            if(_prefabView!=null)
            {
                var oldCanvas=GetComponentInParent<Canvas>();if(oldCanvas!=null)oldCanvas.enabled=false;
                panel=_prefabView.transform.Find("Panel").gameObject;
                title=GameUiPrefabs.Find<Text>(panel,"Title");status=GameUiPrefabs.Find<Text>(panel,"Status");
                hint=GameUiPrefabs.Find<Text>(panel,"Hint");
                inviteButton=GameUiPrefabs.Find<Button>(panel,"Invite");startButton=GameUiPrefabs.Find<Button>(panel,"Start");
                localHostButton=GameUiPrefabs.Find<Button>(panel,"LocalHost");localClientButton=GameUiPrefabs.Find<Button>(panel,"LocalClient");
                returnToPortButton=GameUiPrefabs.Find<Button>(panel,"ReturnToPort");closeButton=GameUiPrefabs.Find<Button>(panel,"Close");
            }
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
            if (!PlayerProgressionUI.MenuOpen && PlayerProgressionUI.ClosedOnFrame != Time.frameCount &&
                !WaveByWave.Player.NetworkPlayerController.RingChoiceOpen &&
                !WaveByWave.Customization.CustomizationMenu.InputCaptured &&
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

        public static GameObject BuildTemplate()
        {
            var root=UiDefaults.Canvas("Session menu",160);
            var prompt=UiDefaults.Text("Menu hint",root.transform,"ESC — меню     TAB — кольца",20,new Vector2(420,38),new Vector2(230,-30));
            prompt.rectTransform.anchorMin=prompt.rectTransform.anchorMax=new Vector2(0,1);
            var panel=UiDefaults.Image("Panel",root.transform,new Color(.025f,.06f,.08f,.96f));
            UiDefaults.Rect(panel.rectTransform,new Vector2(680,800),Vector2.zero);
            UiDefaults.Text("Title",panel.transform,"ПОРТ",38,new Vector2(630,60),new Vector2(0,335));
            UiDefaults.Text("Status",panel.transform,"",22,new Vector2(620,64),new Vector2(0,262));
            UiDefaults.Text("Hint",panel.transform,"",18,new Vector2(620,80),new Vector2(0,-320));
            var names=new[]{"Invite","Start","ReturnToPort","LocalHost","LocalClient","Close"};
            var labels=new[]{"Пригласить друзей Steam","Начать плавание","Вернуться в порт","Локальный Host (тест)","Локальный Client (тест)","Продолжить"};
            for(var i=0;i<names.Length;i++)UiDefaults.Button(names[i],panel.transform,labels[i],new Vector2(600,62),new Vector2(0,180-i*74));
            return root;
        }
        private void OnDestroy()
        {
            var ownsView = GameUiPrefabs.IsOwnedBy(_prefabView, this);
            if(_prefabView!=null)GameUiPrefabs.Release(_prefabView, this);
            if (_session != null)
                _session.StateChanged -= Refresh;
            if (ownsView) InputCaptured = false;
        }
    }
}
