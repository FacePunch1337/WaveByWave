using UnityEngine;

namespace WaveByWave.Player
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class ClimbableLadder : MonoBehaviour, IPlayerInteractable
    {
        [Header("Размер")]
        [SerializeField, Min(0.5f)] private float length = 4f;
        [SerializeField, Min(0.1f)] private float width = 0.8f;
        [SerializeField, Min(0.02f)] private float depth = 0.12f;
        [SerializeField] private Transform visual;
        [SerializeField] private BoxCollider interactionVolume;

        [Header("Положение игрока")]
        [SerializeField] private Vector3 playerLocalOffset = new(0f, 0f, -0.55f);
        [SerializeField] private Vector3 playerLocalEuler;
        [SerializeField, Min(0f)] private float bottomPadding = 0.15f;
        [SerializeField, Min(0f)] private float topPadding = 0.15f;

        [Header("Движение")]
        [SerializeField, Min(0.1f)] private float climbSpeed = 2.5f;
        [SerializeField, Min(0.1f)] private float jumpAwaySpeed = 3.5f;

        public float Length => length;
        public float ClimbSpeed => climbSpeed;
        public float JumpAwaySpeed => jumpAwaySpeed;
        public Vector3 AwayDirection => -transform.forward;

        public string GetInteractionPrompt(NetworkPlayerController player) =>
            player != null && player.ActiveLadder == this ? "Слезть с лестницы [E]" : "Залезть на лестницу [E]";

        public void Interact(NetworkPlayerController player)
        {
            if (player != null)
                player.ToggleLadder(this);
        }

        public float ClosestProgress(Vector3 worldPosition)
        {
            var local = transform.InverseTransformPoint(worldPosition);
            var minimum = Mathf.Min(bottomPadding, length * 0.45f);
            var maximum = Mathf.Max(minimum, length - Mathf.Min(topPadding, length * 0.45f));
            return Mathf.Clamp(local.y, minimum, maximum);
        }

        public float MoveProgress(float progress, float input, float deltaTime)
        {
            var minimum = Mathf.Min(bottomPadding, length * 0.45f);
            var maximum = Mathf.Max(minimum, length - Mathf.Min(topPadding, length * 0.45f));
            return Mathf.Clamp(progress + input * climbSpeed * deltaTime, minimum, maximum);
        }

        public void GetLocalPose(float progress, out Vector3 position, out Quaternion rotation)
        {
            position = playerLocalOffset + Vector3.up * progress;
            rotation = Quaternion.Euler(playerLocalEuler);
        }

        private void Reset()
        {
            visual = transform.Find("Visual");
            interactionVolume = GetComponent<BoxCollider>();
            RefreshAuthoring();
        }

        private void OnValidate() => RefreshAuthoring();

        private void RefreshAuthoring()
        {
            length = Mathf.Max(0.5f, length);
            width = Mathf.Max(0.1f, width);
            depth = Mathf.Max(0.02f, depth);
            if (visual != null)
            {
                visual.localPosition = Vector3.up * (length * 0.5f);
                visual.localRotation = Quaternion.identity;
                visual.localScale = new Vector3(width, length, depth);
            }
            if (interactionVolume != null)
            {
                interactionVolume.isTrigger = true;
                interactionVolume.center = new Vector3(0f, length * 0.5f, playerLocalOffset.z * 0.5f);
                interactionVolume.size = new Vector3(width + 0.8f, length + 0.6f,
                    Mathf.Abs(playerLocalOffset.z) + depth + 0.9f);
            }
        }

        private void OnDrawGizmosSelected()
        {
            var previous = Gizmos.matrix;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = new Color(0.2f, 0.9f, 1f, 0.8f);
            GetLocalPose(ClosestProgress(transform.position + transform.up * length * 0.5f),
                out var position, out _);
            Gizmos.DrawWireSphere(position, 0.12f);
            Gizmos.DrawLine(playerLocalOffset + Vector3.up * bottomPadding,
                playerLocalOffset + Vector3.up * Mathf.Max(bottomPadding, length - topPadding));
            Gizmos.matrix = previous;
        }
    }
}
