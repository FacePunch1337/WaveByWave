using UnityEngine;
using UnityEngine.UI;

namespace WaveByWave.Generation
{
    [DisallowMultipleComponent]
    public sealed class OceanLoadingCurtain : MonoBehaviour
    {
        [SerializeField] private CanvasGroup canvasGroup;
        [SerializeField] private Text progressLabel;
        [SerializeField, Min(0.1f)] private float fadeSpeed = 5f;

        private bool _shown;
        public static bool InputCaptured { get; private set; }

        private void Awake()
        {
            canvasGroup ??= GetComponent<CanvasGroup>();
            ApplyImmediate(false);
        }

        public void Show(int ready, int total)
        {
            _shown = true;
            InputCaptured = true;
            gameObject.SetActive(true);
            if (progressLabel != null)
                progressLabel.text = total > 0
                    ? $"Подготавливаем океан...\nОстрова {ready}/{total}"
                    : "Подготавливаем океан...";
        }

        public void Hide()
        {
            _shown = false;
            InputCaptured = false;
        }

        public void HideImmediate() => ApplyImmediate(false);

        private void Update()
        {
            if (canvasGroup == null)
                return;
            canvasGroup.alpha = Mathf.MoveTowards(canvasGroup.alpha, _shown ? 1f : 0f,
                fadeSpeed * Time.unscaledDeltaTime);
            canvasGroup.blocksRaycasts = _shown;
            canvasGroup.interactable = _shown;
            if (!_shown && canvasGroup.alpha <= 0f)
                gameObject.SetActive(false);
        }

        private void ApplyImmediate(bool shown)
        {
            _shown = shown;
            InputCaptured = shown;
            if (canvasGroup != null)
            {
                canvasGroup.alpha = shown ? 1f : 0f;
                canvasGroup.blocksRaycasts = shown;
                canvasGroup.interactable = shown;
            }
            gameObject.SetActive(shown);
        }

        private void OnDisable()
        {
            if (!_shown)
                InputCaptured = false;
        }

        private void OnDestroy() => InputCaptured = false;
    }
}
