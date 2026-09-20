using UnityEngine;
using UnityEngine.SceneManagement;
using WaveByWave.Player;

namespace WaveByWave.Customization
{
    [DisallowMultipleComponent]
    public sealed class CustomizationStation : MonoBehaviour, IPlayerInteractable
    {
        [SerializeField] private Transform station;
        [SerializeField] private Transform cameraPose;

        public Transform Station => station != null ? station : transform;
        public Transform CameraPose => cameraPose != null ? cameraPose : transform;

        private Material _markerMaterial;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void RegisterPortFallback()
        {
            SceneManager.sceneLoaded -= EnsurePortStation;
            SceneManager.sceneLoaded += EnsurePortStation;
        }

        private static void EnsurePortStation(Scene scene, LoadSceneMode mode)
        {
            if (scene.name != "Port" || FindFirstObjectByType<CustomizationStation>() != null)
                return;

            var root = new GameObject("Pirate Customization Wardrobe (Runtime Fallback)");
            root.transform.SetPositionAndRotation(new Vector3(-5f, 0.75f, 5f), Quaternion.identity);
            var collider = root.AddComponent<BoxCollider>();
            collider.size = new Vector3(1.8f, 2.2f, 0.6f);

            var playerStation = new GameObject("Player Station").transform;
            playerStation.SetParent(root.transform, false);
            playerStation.localPosition = new Vector3(0f, -0.75f, -1.5f);
            playerStation.localRotation = Quaternion.Euler(0f, 180f, 0f);

            var view = new GameObject("Customization Camera Pose").transform;
            view.SetParent(root.transform, false);
            view.localPosition = new Vector3(0f, 0.7f, -4f);

            var component = root.AddComponent<CustomizationStation>();
            component.station = playerStation;
            component.cameraPose = view;
        }

        private void Awake() => BuildVisibleMarker();

        private void BuildVisibleMarker()
        {
            if (transform.Find("Runtime Wardrobe Marker") != null)
                return;
            var marker = new GameObject("Runtime Wardrobe Marker").transform;
            marker.SetParent(transform, false);

            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            if (shader != null)
            {
                _markerMaterial = new Material(shader)
                {
                    name = "Wardrobe Marker (Runtime)",
                    color = new Color(0.05f, 0.65f, 0.85f)
                };
                _markerMaterial.SetColor("_BaseColor", new Color(0.05f, 0.65f, 0.85f));
                _markerMaterial.SetColor("_EmissionColor", new Color(0.05f, 0.8f, 1f) * 1.5f);
                _markerMaterial.EnableKeyword("_EMISSION");
            }

            CreateMarkerCube(marker, "Wardrobe Back", new Vector3(0f, 0.3f, 0.05f),
                new Vector3(1.8f, 2.1f, 0.3f));
            CreateMarkerCube(marker, "Wardrobe Sign", new Vector3(0f, 1.6f, -0.12f),
                new Vector3(2.25f, 0.48f, 0.18f));
            CreateMarkerCube(marker, "Left Post", new Vector3(-1.05f, 0.25f, 0f),
                new Vector3(0.16f, 2.65f, 0.16f));
            CreateMarkerCube(marker, "Right Post", new Vector3(1.05f, 0.25f, 0f),
                new Vector3(0.16f, 2.65f, 0.16f));

            var label = new GameObject("Wardrobe Label", typeof(TextMesh));
            label.transform.SetParent(marker, false);
            label.transform.localPosition = new Vector3(0f, 1.6f, -0.23f);
            label.transform.localRotation = Quaternion.identity;
            var text = label.GetComponent<TextMesh>();
            text.text = "CUSTOMIZE  [E]";
            text.anchor = TextAnchor.MiddleCenter;
            text.alignment = TextAlignment.Center;
            text.fontSize = 64;
            text.characterSize = 0.08f;
            text.color = Color.white;
        }

        private void CreateMarkerCube(Transform parent, string name, Vector3 position, Vector3 scale)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = name;
            cube.transform.SetParent(parent, false);
            cube.transform.localPosition = position;
            cube.transform.localScale = scale;
            var collider = cube.GetComponent<Collider>();
            if (collider != null)
            {
                collider.enabled = false;
                Destroy(collider);
            }
            if (_markerMaterial != null)
                cube.GetComponent<Renderer>().sharedMaterial = _markerMaterial;
        }

        public string GetInteractionPrompt(NetworkPlayerController player) =>
            player != null && player.ActiveCustomizationStation == this
                ? "Закрыть гардероб [E]"
                : "Изменить внешний вид [E]";

        public void Interact(NetworkPlayerController player)
        {
            if (player != null)
                player.ToggleCustomization(this);
        }

        private void OnDrawGizmosSelected()
        {
            if (station != null)
            {
                Gizmos.color = new Color(0.15f, 0.9f, 1f);
                Gizmos.DrawWireSphere(station.position, 0.18f);
                Gizmos.DrawLine(station.position, station.position + station.forward);
            }
            if (cameraPose != null)
            {
                Gizmos.color = Color.yellow;
                Gizmos.DrawWireSphere(cameraPose.position, 0.12f);
                Gizmos.DrawLine(cameraPose.position, cameraPose.position + cameraPose.forward);
            }
        }

        private void OnDestroy()
        {
            if (_markerMaterial != null)
                Destroy(_markerMaterial);
        }
    }
}
