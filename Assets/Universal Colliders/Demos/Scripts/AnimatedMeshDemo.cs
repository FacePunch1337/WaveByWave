using UnityEngine;
using UColliders;

namespace UColliders.Demos
{
    /// <summary>
    /// Demonstrates animated mesh colliders following skeletal animation.
    /// Spawns small objects that bounce off the animated character's colliders.
    ///
    /// Setup: Attach to a GameObject with SkinnedMeshRenderer + Animator.
    /// The script adds UCollidersRoot automatically.
    /// </summary>
    [RequireComponent(typeof(SkinnedMeshRenderer))]
    public class AnimatedMeshDemo : MonoBehaviour
    {
        [Header("Projectile Settings")]
        [Tooltip("Small primitive to bounce off the animated mesh. If null, uses default spheres.")]
        public GameObject projectilePrefab;

        [Tooltip("How many projectiles to maintain.")]
        public int projectileCount = 15;

        [Tooltip("Seconds between projectile launches.")]
        public float launchInterval = 0.5f;

        [Tooltip("Launch speed.")]
        public float launchSpeed = 5f;

        [Tooltip("Distance from character to launch projectiles.")]
        public float launchDistance = 3f;

        [Tooltip("Height range for launches.")]
        public float launchHeight = 2f;

        [Header("Collider Settings")]
        [Tooltip("OBB recursion level for the animated mesh.")]
        public int recursionLevel = 4;

        [Tooltip("Shape for colliders on the animated mesh.")]
        public Shape colliderShape = Shape.Box;

        int launched;

        void Start()
        {
            // Add and configure UCollidersRoot
            UCollidersRoot root = GetComponent<UCollidersRoot>();
            if (root == null)
                root = gameObject.AddComponent<UCollidersRoot>();
            root.recursionLevel = recursionLevel;
            root.shape = colliderShape;
            root.previewColor = UCollidersRoot.CollidersPreviewColor.Random;

            InvokeRepeating(nameof(LaunchProjectile), 1f, launchInterval);
        }

        void LaunchProjectile()
        {
            if (launched >= projectileCount) return;

            // Random position around the character
            float angle = Random.Range(0f, 360f) * Mathf.Deg2Rad;
            float height = Random.Range(0.5f, launchHeight);
            Vector3 origin = transform.position
                + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * launchDistance
                + Vector3.up * height;

            // Aim toward character center
            Vector3 direction = (transform.position + Vector3.up * (launchHeight * 0.5f) - origin).normalized;

            GameObject proj;
            if (projectilePrefab != null) {
                proj = Instantiate(projectilePrefab, origin, Quaternion.identity);
            } else {
                proj = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                proj.transform.position = origin;
                proj.transform.localScale = Vector3.one * 0.2f;
            }
            proj.name = $"Projectile {launched}";

            Rigidbody rb = proj.GetComponent<Rigidbody>();
            if (rb == null)
                rb = proj.AddComponent<Rigidbody>();
            rb.linearVelocity = direction * launchSpeed;
            rb.useGravity = true;

            launched++;
            Destroy(proj, 10f);
            Invoke(nameof(DecrementCount), 10f);
        }

        void DecrementCount() => launched--;
    }
}
