using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Std; // StringMsg

/// <summary>
/// Subscribes to a ROS topic. On every message received, translates "objectToMove"
/// (assumed to be a parent/grandparent/etc. of the GameObject this script is on)
/// so that THIS object's world position ends up matching "targetTransform"'s world position.
///
/// Works regardless of how many levels of hierarchy or what rotation/scale sit between
/// objectToMove and this transform: translating an ancestor's world position by a delta
/// shifts every descendant's world position by that exact same delta.
/// </summary>
public class MoveAncestorToMatchTarget : MonoBehaviour
{
    [Header("ROS")]
    [SerializeField]
    [Tooltip("ROS topic to listen on. Any message received triggers the move (message content is ignored).")]
    private string topicName = "/trigger_move";

    [Header("Hierarchy")]
    [Tooltip("Ancestor (parent, grandparent, etc.) of THIS object. This is the object that actually gets moved.")]
    public Transform objectToMove;

    [Header("Target")]
    [SerializeField]
    [Tooltip("THIS object (the attached object) will end up at this transform's position after the move.")]
    private Transform targetTransform;

    [Header("Dashboard")]
    [SerializeField]
    [Tooltip("Text shown on the HTTPDash button that manually triggers calibration (the same move).")]
    private string calibrationButtonName = "Calibrate";

    private ROSConnection ros;

    private void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();
        ros.Subscribe<StringMsg>(topicName, OnMessageReceived);

        if (HTTPDash.Instance != null)
        {
            HTTPDash.Instance.RegisterButton("Calibration", calibrationButtonName, OnCalibrationButtonPressed);
        }
        else
        {
            Debug.LogWarning($"{nameof(MoveAncestorToMatchTarget)} on '{name}': HTTPDash.Instance is null, calibration button was not registered.");
        }
    }

    private void OnMessageReceived(StringMsg msg)
    {
        MoveToMatchTarget();
    }

    private void OnCalibrationButtonPressed(string content)
    {
        MoveToMatchTarget();
    }

    private void MoveToMatchTarget()
{
    if (objectToMove == null)
    {
        Debug.LogWarning($"{nameof(MoveAncestorToMatchTarget)} on '{name}': objectToMove is not assigned.");
        return;
    }

    if (targetTransform == null)
    {
        Debug.LogWarning($"{nameof(MoveAncestorToMatchTarget)} on '{name}': targetTransform is not assigned.");
        return;
    }

    // TrackedPoseDriver writes the XR device's pose into this transform's LOCAL
    // position and rotation every frame.  We must NOT touch this transform —
    // instead we solve for the objectToMove world transform that makes the
    // tracker's resulting WORLD pose equal to targetTransform's world pose.

    // Current XR device output (TPD-driven local pose relative to objectToMove).
    Vector3    localPos = transform.localPosition;
    Quaternion localRot = transform.localRotation;

    // Solve for objectToMove.rotation:
    //   objectToMove.rotation * localRot == targetTransform.rotation
    Quaternion newWorldRot = targetTransform.rotation * Quaternion.Inverse(localRot);

    // Solve for objectToMove.position:
    //   objectToMove.position + objectToMove.rotation * localPos == targetTransform.position
    Vector3 newWorldPos = targetTransform.position - newWorldRot * localPos;

    objectToMove.SetPositionAndRotation(newWorldPos, newWorldRot);

    // Sanity checks (this transform, not objectToMove, should now match target).
    float posErr = (transform.position - targetTransform.position).magnitude;
    float rotErr = Quaternion.Angle(transform.rotation, targetTransform.rotation);

    if (posErr > 0.001f || rotErr > 0.1f)
    {
        Debug.LogWarning(
            $"{nameof(MoveAncestorToMatchTarget)} on '{name}': post-calibration error — " +
            $"pos {posErr * 100f:F1} cm, rot {rotErr:F2}°. " +
            $"Check objectToMove has no non-uniform scale.");
    }
    else if (HTTPDash.Instance != null)
    {
        HTTPDash.Instance.SendNotification(
            "Calibration",
            $"'{name}' calibrated to '{targetTransform.name}'. pos err {posErr * 1000f:F1} mm, rot err {rotErr:F2}°.",
            "#2e7d32");
    }
}

    private void OnDestroy()
    {
        if (ros != null)
        {
            ros.Unsubscribe(topicName);
        }
    }
}
