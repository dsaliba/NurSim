using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Std;
using UnityEngine;
using GazeData.EyeTracking;

namespace GazeData.Ros
{
    /// <summary>
    /// Drop on any GameObject with a Collider that should count as a "gaze-publishable"
    /// target. While the shared gaze recorder's raycast is hitting this object's collider,
    /// publishes <see cref="key"/> as a std_msgs/String on <see cref="rosTopic"/>, at most
    /// every 1/<see cref="publishRateHz"/> seconds.
    ///
    /// Multiple instances (one per interactable object) are meant to share the same
    /// <see cref="rosTopic"/> value and rely on <see cref="key"/> to identify which object
    /// was hit; each instance registers independently but ROSConnection de-duplicates
    /// repeat RegisterPublisher calls for the same topic.
    /// </summary>
    public class GazeColliderRosPublisher : MonoBehaviour
    {
        [Tooltip("ROS topic to publish on. Typically the same value across every instance of " +
                  "this script in the scene, with 'key' distinguishing which object was gazed at.")]
        [SerializeField] private string rosTopic = "/gaze/hit";

        [Tooltip("Value published to the topic when this object's collider is gazed at.")]
        [SerializeField] private string key = "";

        [Tooltip("Maximum publish frequency in Hz while the gaze stays on this object. " +
                  "E.g. 10 = at most one message every 100ms.")]
        [SerializeField] private float publishRateHz = 10f;

        [Tooltip("Collider to watch for gaze hits. Leave empty to use this GameObject's own " +
                  "Collider (the common case: drop this script directly on the target object).")]
        [SerializeField] private Collider targetCollider;

        [Tooltip("Source of the gaze raycast - ViveOpenXrGazeRecorder (real headset) or MockGazeSource (mouse-driven testing). Auto-found in the scene if left empty.")]
        [SerializeField] private GazeSource gazeRecorder;

        private float _lastPublishTime = float.NegativeInfinity;

        /// <summary>Sets the topic/key/rate from code (e.g. a spawner script building several
        /// targets at once) instead of the Inspector.</summary>
        public void Configure(string topic, string keyValue, float rateHz)
        {
            rosTopic = topic;
            key = keyValue;
            publishRateHz = rateHz;
        }

        private void Awake()
        {
            if (targetCollider == null) targetCollider = GetComponent<Collider>();
            if (gazeRecorder == null) gazeRecorder = FindObjectOfType<GazeSource>();
        }

        private void Start()
        {
            ROSConnection.GetOrCreateInstance().RegisterPublisher<StringMsg>(rosTopic);
        }

        private void Update()
        {
            if (targetCollider == null || gazeRecorder == null) return;
            if (!gazeRecorder.HasValidGaze) return;
            if (gazeRecorder.CurrentGazeHitObject != targetCollider.gameObject) return;

            float minInterval = publishRateHz > 0f ? 1f / publishRateHz : 0f;
            if (Time.unscaledTime - _lastPublishTime < minInterval) return;

            _lastPublishTime = Time.unscaledTime;
            ROSConnection.GetOrCreateInstance().Publish(rosTopic, new StringMsg(key));
        }
    }
}
