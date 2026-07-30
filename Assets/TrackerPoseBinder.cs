using System.Collections;
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
    private bool bindingsReady = false;

    void Awake()
    {
        // ROS setup can happen immediately
        ros = ROSConnection.GetOrCreateInstance();
        ros.RegisterPublisher<PoseMsg>(rosTopicName);

        trackedPoseDriver = GetComponent<TrackedPoseDriver>();

        // Suppress TPD immediately so it doesn't cache empty/unresolved bindings
        // during the dynamic scene load frame.
        trackedPoseDriver.enabled = false;
    }

    IEnumerator Start()
    {
        // Wait one frame for the XR Input System to fully enumerate
        // devices in the dynamically loaded scene context.
        yield return null;

        SetupBindings();
    }

    void SetupBindings()
    {
        // Dispose any previous actions (e.g. if SetupBindings is ever called again)
        positionAction?.Dispose();
        rotationAction?.Dispose();

        string trackerName = $"Ultimate Tracker {trackerIndex}";
        string positionPath = $"<ViveXRTracker>{{{trackerName}}}/devicePose/position";
        string rotationPath = $"<ViveXRTracker>{{{trackerName}}}/devicePose/rotation";

        positionAction = new InputAction(
            "tracker_pos",
            InputActionType.Value,
            positionPath,
            expectedControlType: "Vector3");

        rotationAction = new InputAction(
            "tracker_rot",
            InputActionType.Value,
            rotationPath,
            expectedControlType: "Quaternion");

        positionAction.Enable();
        rotationAction.Enable();

        // Assign bindings while TPD is disabled, then enable so
        // OnEnable() caches them against fully-registered devices.
        trackedPoseDriver.positionInput = new InputActionProperty(positionAction);
        trackedPoseDriver.rotationInput = new InputActionProperty(rotationAction);
        trackedPoseDriver.trackingType = TrackedPoseDriver.TrackingType.RotationAndPosition;
        trackedPoseDriver.updateType = TrackedPoseDriver.UpdateType.UpdateAndBeforeRender;
        trackedPoseDriver.enabled = true;

        bindingsReady = true;
    }

    void Update()
    {
        if (!bindingsReady) return;

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
