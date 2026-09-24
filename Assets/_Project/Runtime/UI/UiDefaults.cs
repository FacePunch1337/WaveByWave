using UnityEngine;
using UnityEngine.UI;

namespace WaveByWave.UI
{
    public static class UiDefaults
    {
        public static GameObject Canvas(string name, int order)
        {
            var root = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            root.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            root.GetComponent<Canvas>().sortingOrder = order;
            var scaler = root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920,1080); scaler.matchWidthOrHeight = .5f;
            return root;
        }
        public static Image Image(string name, Transform parent, Color color)
        {
            var go = new GameObject(name,typeof(RectTransform),typeof(Image));
            go.transform.SetParent(parent,false); var image = go.GetComponent<Image>(); image.color = color;
            return image;
        }
        public static Text Text(string name, Transform parent, string text, int size, Vector2 dimensions, Vector2 position)
        {
            var go = new GameObject(name,typeof(RectTransform),typeof(Text)); go.transform.SetParent(parent,false);
            var label=go.GetComponent<Text>(); label.text=text; label.font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.fontSize=size; label.color=Color.white; label.alignment=TextAnchor.MiddleCenter; label.raycastTarget=false;
            Rect(label.rectTransform,dimensions,position); return label;
        }
        public static Button Button(string name, Transform parent, string text, Vector2 dimensions, Vector2 position)
        {
            var image=Image(name,parent,new Color(.12f,.23f,.29f,1)); Rect(image.rectTransform,dimensions,position);
            var button=image.gameObject.AddComponent<Button>(); button.targetGraphic=image;
            Stretch(Text("Label",image.transform,text,20,dimensions,Vector2.zero).rectTransform); return button;
        }
        public static void Rect(RectTransform r,Vector2 size,Vector2 position)
        { r.anchorMin=r.anchorMax=r.pivot=Vector2.one*.5f;r.sizeDelta=size;r.anchoredPosition=position; }
        public static void Stretch(RectTransform r,float inset=0)
        { r.anchorMin=Vector2.zero;r.anchorMax=Vector2.one;r.offsetMin=Vector2.one*inset;r.offsetMax=Vector2.one*-inset; }
    }
}
