using UnityEngine;

namespace CelesteBenchmark
{
    public sealed class BenchmarkCameraFollow : MonoBehaviour
    {
        public Transform target;
        public Vector3 offset = new Vector3(0f, 1.5f, -10f);
        public float followSpeed = 8f;
        public float orthographicSize = 6f;

        Camera cameraComponent;

        void Awake()
        {
            cameraComponent = GetComponent<Camera>();
            if (cameraComponent)
                cameraComponent.orthographicSize = orthographicSize;
        }

        void LateUpdate()
        {
            if (!target)
                return;

            Vector3 desired = target.position + offset;
            transform.position = Vector3.Lerp(transform.position, desired, 1f - Mathf.Exp(-followSpeed * Time.deltaTime));
        }
    }
}
