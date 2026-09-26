using UnityEngine;
using UnityEngine.UI;
using WaveByWave.Generation;
using WaveByWave.Ships;

namespace WaveByWave.UI
{
    public sealed class VoyageHud : MonoBehaviour
    {
        private ShipCannonBattery _ship;
        private ShipFlooding _flood;
        private GameObject _hud, _result;
        private float _nextSearch;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void StartHud()
        {
            var go=new GameObject("Voyage UI presenter");
            go.AddComponent<VoyageHud>();DontDestroyOnLoad(go);
        }
        private void Update()
        {
            if(_ship==null && Time.unscaledTime>=_nextSearch)
            {
                _nextSearch=Time.unscaledTime+.5f;
                _ship=FindFirstObjectByType<ShipCannonBattery>();
                _flood=_ship!=null?_ship.GetComponent<ShipFlooding>():null;
            }
            var active=_ship!=null&&_ship.IsSpawned&&_ship.IsClient;
            if(!active) {if(_hud!=null)_hud.SetActive(false);if(_result!=null)_result.SetActive(false);return;}
            if(_hud==null) _hud=GameUiPrefabs.Create("HUD/Voyage", owner: this)??BuildHud();
            _hud.SetActive(!_ship.VoyageEnded);
            var waveLabel=_ship.Phase switch
            {
                VoyagePhase.Day=>$"День {_ship.WaveNumber}",VoyagePhase.Night=>$"Ночная волна {_ship.WaveNumber}",
                VoyagePhase.Sunset=>"Наступает ночь",_=>"Рассвет"
            };
            if(_ship.Phase==VoyagePhase.Night&&_ship.WaveEnemiesTotal>0)
                waveLabel+=$"  ·  {_ship.WaveEnemiesDefeated}/{_ship.WaveEnemiesTotal}";
            GameUiPrefabs.Find<Text>(_hud,"Day/Label").text=waveLabel;
            GameUiPrefabs.Find<RectTransform>(_hud,"Day/Progress").anchorMax=new Vector2(_ship.DayProgress,0);
            if(_flood!=null)
            {
                GameUiPrefabs.Find<Text>(_hud,"Flood/Label").text=$"Вода: {_flood.Fill:P0}  ·  Пробоины: {_flood.HoleCount}  ·  +{_flood.Inflow:0.#} л/с";
                GameUiPrefabs.Find<RectTransform>(_hud,"Flood/Progress").anchorMax=new Vector2(_flood.Fill,0);
            }
            var wave=NightWaveController.Active;
            GameUiPrefabs.Find<Text>(_hud,"Warning").text=NightBattlefieldController.Active!=null?
                NightBattlefieldController.Active.BoundaryWarning:"";
            var announcement=GameUiPrefabs.Find<Text>(_hud,"Announcement");
            announcement.text=wave!=null?wave.Announcement:"";
            var color=announcement.color;color.a=wave!=null?wave.AnnouncementAlpha:0;announcement.color=color;
            if(!_ship.VoyageEnded){if(_result!=null)_result.SetActive(false);return;}
            if(_result==null)_result=GameUiPrefabs.Create("Menus/VoyageResult", owner: this)??BuildResult();
            _result.SetActive(true);
            var victory=_ship.Phase==VoyagePhase.Victory;
            GameUiPrefabs.Find<Text>(_result,"Title").text=victory?"ПОБЕДА":"КОРАБЛЬ ЗАТОНУЛ";
            GameUiPrefabs.Find<Text>(_result,"Detail").text=(victory?"Все волны отражены. Пора возвращаться в порт.":"Вода заполнила трюм. Плавание окончено.")+
                $"\nВозвращение в PORT через {Mathf.CeilToInt(_ship.ReturnToPortIn)} с";
        }
        public static GameObject BuildHud()
        {
            var root=UiDefaults.Canvas("Voyage HUD",80);
            var day=UiDefaults.Image("Day",root.transform,new Color(.02f,.04f,.06f,.85f));
            UiDefaults.Rect(day.rectTransform,new Vector2(340,52),new Vector2(0,-42));day.rectTransform.anchorMin=day.rectTransform.anchorMax=new Vector2(.5f,1);
            UiDefaults.Text("Label",day.transform,"День 1",22,new Vector2(330,32),new Vector2(0,7));Progress(day.transform,new Color(1,.78f,.25f));
            var flood=UiDefaults.Image("Flood",root.transform,new Color(.02f,.04f,.06f,.85f));
            UiDefaults.Rect(flood.rectTransform,new Vector2(550,55),new Vector2(0,-110));flood.rectTransform.anchorMin=flood.rectTransform.anchorMax=new Vector2(.5f,1);
            UiDefaults.Text("Label",flood.transform,"Вода: 0%",20,new Vector2(530,32),new Vector2(0,6));Progress(flood.transform,new Color(.15f,.65f,.8f));
            var warning=UiDefaults.Text("Warning",root.transform,"",22,new Vector2(1100,40),new Vector2(0,-164));
            warning.color=new Color(1,.58f,.4f);warning.rectTransform.anchorMin=warning.rectTransform.anchorMax=new Vector2(.5f,1);
            var announcement=UiDefaults.Text("Announcement",root.transform,"",34,new Vector2(1200,80),new Vector2(0,-240));
            announcement.color=new Color(1,.86f,.55f);announcement.rectTransform.anchorMin=announcement.rectTransform.anchorMax=new Vector2(.5f,1);
            return root;
        }
        public static void Progress(Transform parent,Color color)
        {
            var fill=UiDefaults.Image("Progress",parent,color);fill.raycastTarget=false;
            var r=fill.rectTransform;r.anchorMin=Vector2.zero;r.anchorMax=new Vector2(1,0);r.pivot=Vector2.zero;
            r.offsetMin=new Vector2(5,5);r.offsetMax=new Vector2(-5,11);
        }
        public static GameObject BuildResult()
        {
            var root=UiDefaults.Canvas("Voyage result",400);
            UiDefaults.Stretch(UiDefaults.Image("Shade",root.transform,new Color(.015f,.025f,.04f,.9f)).rectTransform);
            UiDefaults.Text("Title",root.transform,"ПОБЕДА",52,new Vector2(1000,100),new Vector2(0,130));
            UiDefaults.Text("Detail",root.transform,"Возвращение в PORT",28,new Vector2(1200,160),new Vector2(0,-20));
            return root;
        }
        public static GameObject BuildTreasureLevel()
        {
            var root=UiDefaults.Canvas("Treasure level",30);root.GetComponent<Canvas>().renderMode=RenderMode.WorldSpace;
            ((RectTransform)root.transform).sizeDelta=new Vector2(220,48);root.transform.localScale=Vector3.one*.006f;
            UiDefaults.Stretch(UiDefaults.Image("Background",root.transform,new Color(.02f,.04f,.06f,.8f)).rectTransform);
            UiDefaults.Text("Label",root.transform,"Уровень 1",22,new Vector2(210,32),new Vector2(0,7));Progress(root.transform,new Color(1,.78f,.24f));
            return root;
        }
        private void OnDestroy(){if(_hud!=null)GameUiPrefabs.Release(_hud, this);if(_result!=null)GameUiPrefabs.Release(_result, this);}
    }
}
