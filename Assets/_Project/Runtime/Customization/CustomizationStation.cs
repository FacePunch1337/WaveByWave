using UnityEngine;
using WaveByWave.Player;

namespace WaveByWave.Customization
{
    [DisallowMultipleComponent]
    public sealed class CustomizationStation : MonoBehaviour, IPlayerInteractable
    {
        [SerializeField] private Transform station;
        [SerializeField] private Camera viewCamera;

        public Transform Station => station != null ? station : transform;
        public Camera ViewCamera => viewCamera;

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
            if (viewCamera != null)
            {
                Gizmos.color = Color.yellow;
                Gizmos.DrawWireSphere(viewCamera.transform.position, 0.12f);
                Gizmos.DrawLine(viewCamera.transform.position,
                    viewCamera.transform.position + viewCamera.transform.forward);
            }
        }
    }
}
