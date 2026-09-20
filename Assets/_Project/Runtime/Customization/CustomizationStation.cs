using UnityEngine;
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
    }
}
