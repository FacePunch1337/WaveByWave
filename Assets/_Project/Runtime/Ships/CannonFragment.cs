using UnityEngine;

namespace WaveByWave.Ships
{
    public sealed class CannonFragment : MonoBehaviour
    {
        public Vector3 Velocity;
        private void Update()
        {
            Velocity += Physics.gravity * Time.deltaTime;
            transform.position += Velocity * Time.deltaTime;
            transform.Rotate(new Vector3(150f, 90f, 220f) * Time.deltaTime);
        }
    }
}
