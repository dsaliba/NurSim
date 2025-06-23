using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Geometry;

[RequireComponent(typeof(TrackedPoseDriver))]
public class TrackerPoseBinder : MonoBehaviour
{
    [Header("Tracker Index (e.g., 0, 1, 2)")]
    public int trackerIndex = 0;

    [Header("ROS Publishing")]
    public string rosTopicName = "/tracker_pose";
    public float publishFrequency = 30.0f;

    private InputAction positionAction;
    private InputAction rotationAction;

    private TrackedPoseDriver trackedPoseDriver;
    private ROSConnection ros;
    private float publishTimer = 0f;

    void Awake()
    {
        // Setup ROS
        ros = ROSConnection.GetOrCreateInstance();
        ros.RegisterPublisher<PoseMsg>(rosTopicName);

        // Get or add the TrackedPoseDriver
        trackedPoseDriver = GetComponent<TrackedPoseDriver>();

        // Construct Vive XR Tracker input paths
        string trackerName = $"Ultimate Tracker {trackerIndex}";
        string positionPath = $"<ViveXRTracker>{{{trackerName}}}/devicePose/position";
        string rotationPath = $"<ViveXRTracker>{{{trackerName}}}/devicePose/rotation";

        // Create InputActions
        positionAction = new InputAction("tracker_pos", InputActionType.Value, positionPath, expectedControlType: "Vector3");
        rotationAction = new InputAction("tracker_rot", InputActionType.Value, rotationPath, expectedControlType: "Quaternion");

        // Enable the actions (required if not using action assets)
        positionAction.Enable();
        rotationAction.Enable();

        // Assign InputActionProperty to TrackedPoseDriver
        trackedPoseDriver.positionInput = new InputActionProperty(positionAction);
        trackedPoseDriver.rotationInput = new InputActionProperty(rotationAction);
        trackedPoseDriver.trackingType = TrackedPoseDriver.TrackingType.RotationAndPosition;
        trackedPoseDriver.updateType = TrackedPoseDriver.UpdateType.UpdateAndBeforeRender;
    }

    void Update()
    {
        publishTimer += Time.deltaTime;
        if (publishTimer >= 1f / publishFrequency)
        {
            publishTimer = 0f;

            PoseMsg pose = new PoseMsg
            {
                position = new PointMsg(
                    transform.position.x,
                    transform.position.y,
                    transform.position.z),
                orientation = new QuaternionMsg(
                    transform.rotation.x,
                    transform.rotation.y,
                    transform.rotation.z,
                    transform.rotation.w)
            };

            ros.Publish(rosTopicName, pose);
        }
    }

    void OnDestroy()
    {
        positionAction?.Dispose();
        rotationAction?.Dispose();
    }
}
