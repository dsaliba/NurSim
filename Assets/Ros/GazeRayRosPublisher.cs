using RosMessageTypes.Geometry;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;
using GazeData.EyeTracking;

namespace GazeData.Ros
{
    /// <summary>
    /// Continuously publishes the current world-space gaze ray (not tied to any particular
    /// collider) as a geometry_msgs/Pose — position is the ray origin, orientation is a
    /// rotation whose forward axis points along the gaze direction. Publishes at most every
    /// 1/<see cref="publishRateHz"/> seconds, and only while the gaze recorder reports valid
    /// gaze data (nothing is sent on frames with no eye-tracker reading).
    /// </summary>
    public class GazeRayRosPublisher : MonoBehaviour
    {
        [Tooltip("ROS topic to publish the gaze ray (position + orientation) to.")]
        [SerializeField] private string rosTopic = "/gaze/ray";

        [Tooltip("Maximum publish frequency in Hz. E.g. 30 = at most one message every ~33ms.")]
        [SerializeField] private float publishRateHz = 30f;

        [Tooltip("Source of the gaze raycast - ViveOpenXrGazeRecorder (real headset) or MockGazeSource (mouse-driven testing). Auto-found in the scene if left empty.")]
        [SerializeField] private GazeSource gazeRecorder;

        private float _lastPublishTime = float.NegativeInfinity;

        private void Awake()
        {
            if (gazeRecorder == null) gazeRecorder = FindObjectOfType<GazeSource>();
        }

        private void Start()
        {
            ROSConnection.GetOrCreateInstance().RegisterPublisher<PoseMsg>(rosTopic);
        }

        private void Update()
        {
            if (gazeRecorder == null || !gazeRecorder.HasValidGaze) return;

            float minInterval = publishRateHz > 0f ? 1f / publishRateHz : 0f;
            if (Time.unscaledTime - _lastPublishTime < minInterval) return;
            _lastPublishTime = Time.unscaledTime;

            Vector3 origin = gazeRecorder.CurrentGazeWorldOrigin;
            Quaternion rotation = Quaternion.LookRotation(gazeRecorder.CurrentGazeWorldDirection);

            var pose = new PoseMsg(
                new PointMsg(origin.x, origin.y, origin.z),
                new QuaternionMsg(rotation.x, rotation.y, rotation.z, rotation.w));

            ROSConnection.GetOrCreateInstance().Publish(rosTopic, pose);
        }
    }
}
