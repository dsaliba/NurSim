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

        // Pure position-only math — rotation is never read or written here, on either
        // this transform, objectToMove, or targetTransform.
        //
        // Goal: after this call, THIS object's world position == targetTransform's
        // world position. objectToMove is only the means to get there (it is dragged
        // along by whatever delta is needed) — objectToMove itself is NOT meant to end
        // up at targetTransform's position.
        Vector3 attachedObjectCurrentPos = transform.position;
        Vector3 targetPos = targetTransform.position;
        Vector3 worldDelta = targetPos - attachedObjectCurrentPos;

        // Translate objectToMove by worldDelta along world axes. Using Space.World here
        // is what guarantees rotation is not a factor: the move is the same raw vector
        // regardless of objectToMove's own rotation or any rotation elsewhere in the
        // chain between objectToMove and this object.
        objectToMove.Translate(worldDelta, Space.World);

        // Sanity check: this object (not objectToMove) should now be on target.
        if ((transform.position - targetPos).sqrMagnitude > 0.0001f)
        {
            Debug.LogWarning($"{nameof(MoveAncestorToMatchTarget)} on '{name}': post-move position does not match target as expected.");
        }

        if (HTTPDash.Instance != null)
        {
            HTTPDash.Instance.SendNotification("Calibration", $"'{name}' calibrated to '{targetTransform.name}'.", "#2e7d32");
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
