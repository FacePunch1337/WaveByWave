using UnityEngine;

namespace WaveByWave.Ships
{
    public sealed class WindDirectionFlag : MonoBehaviour
    {
        [SerializeField] private NetworkWindController wind;
        [SerializeField] private Transform directionPivot;
        [SerializeField, Min(1f)] private float rotationSpeed = 120f;

        private void Awake()
        {
            wind ??= GetComponentInParent<NetworkWindController>();
            directionPivot ??= transform;
        }

        private void Update()
        {
            if (wind == null || directionPivot == null || directionPivot.parent == null)
                return;

            var localDirection = directionPivot.parent.InverseTransformDirection(wind.Direction);
            localDirection.y = 0f;
            if (localDirection.sqrMagnitude < 0.001f)
                return;

            var targetRotation = Quaternion.LookRotation(localDirection.normalized, Vector3.up);
            directionPivot.localRotation = Quaternion.RotateTowards(directionPivot.localRotation, targetRotation,
                rotationSpeed * Time.deltaTime);
        }
    }
}
