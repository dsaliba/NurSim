using RosMessageTypes.Geometry;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;
using GazeData.EyeTracking;

namespace GazeData.Ros
{
    /// <summary>
    /// A World Space Canvas that has a Collider sized to match its RectTransform
    /// (e.g. a BoxCollider covering the panel) — required because the gaze ray is a physics
    /// raycast (see ViveOpenXrGazeRecorder), not a UI/EventSystem raycast, so the canvas needs
    /// something for Physics.Raycast to actually hit.
    ///
    /// While the gaze ray hits that collider, publishes the hit point as a normalized (0-1,
    /// 0-1) 2D coordinate on the canvas's rect — geometry_msgs/Point with x=u, y=v, z=0 — every
    /// frame the hit continues (no rate limiting; add a throttle here if this floods your topic).
    /// </summary>
    public class GazeCanvasRosPublisher : MonoBehaviour
    {
        [Tooltip("ROS topic to publish the normalized (u, v) hit coordinate to.")]
        [SerializeField] private string rosTopic = "/gaze/canvas_hit";

        [Tooltip("Collider to raycast against. Leave empty to use this GameObject's own " +
                  "Collider (the common case: drop this script directly on the canvas).")]
        [SerializeField] private Collider targetCollider;

        [Tooltip("Source of the gaze raycast - ViveOpenXrGazeRecorder (real headset) or MockGazeSource (mouse-driven testing). Auto-found in the scene if left empty.")]
        [SerializeField] private GazeSource gazeRecorder;

        private RectTransform _rectTransform;

        private void Awake()
        {
            if (targetCollider == null) targetCollider = GetComponent<Collider>();
            if (gazeRecorder == null) gazeRecorder = FindObjectOfType<GazeSource>();
            _rectTransform = GetComponent<RectTransform>();
        }

        private void Start()
        {
            ROSConnection.GetOrCreateInstance().RegisterPublisher<PointMsg>(rosTopic);
        }

        private void Update()
        {
            if (targetCollider == null || gazeRecorder == null || _rectTransform == null) return;
            if (!gazeRecorder.HasValidGaze) return;
            if (gazeRecorder.CurrentGazeHitObject != targetCollider.gameObject) return;

            // Re-raycast against just this collider (rather than reusing the shared recorder's
            // hit) to get the precise world-space hit point needed for the UV conversion below.
            var ray = new Ray(gazeRecorder.CurrentGazeWorldOrigin, gazeRecorder.CurrentGazeWorldDirection);
            if (!targetCollider.Raycast(ray, out RaycastHit hit, Mathf.Infinity)) return;

            Vector3 localPoint = _rectTransform.InverseTransformPoint(hit.point);
            Rect rect = _rectTransform.rect;
            float u = Mathf.InverseLerp(rect.xMin, rect.xMax, localPoint.x);
            float v = Mathf.InverseLerp(rect.yMin, rect.yMax, localPoint.y);

            ROSConnection.GetOrCreateInstance().Publish(rosTopic, new PointMsg(u, v, 0.0));
        }
    }
}
