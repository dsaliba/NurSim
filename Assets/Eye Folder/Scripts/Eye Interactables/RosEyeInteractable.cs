using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Geometry;

/// <summary>
/// Publishes normalized 2D hit coordinates on a Box Collider to a per-object ROS topic,
/// AND contributes a key to the shared GazeRegistry ("/unity/gazed_object").
///
/// - rosTopic     : topic for this object's normalized hit position (geometry_msgs/Point)
/// - gazeKey      : key published to /unity/gazed_object while this object is gazed at
///                  Leave gazeKey empty to opt out of the shared gaze registry.
/// </summary>
public class RosEyeInteractable : MonoBehaviour, EyeRayInterface
{
    [SerializeField]
    private string rosTopic = "/eye_hit_location";

    [SerializeField]
    public string gazeKey = "";

    private ROSConnection ros;
    private BoxCollider boxCollider;

    void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();
        ros.RegisterPublisher<PointMsg>(rosTopic);

        boxCollider = GetComponent<BoxCollider>();
        if (boxCollider == null)
        {
            Debug.LogWarning($"[RosEyeInteractable] No BoxCollider found on '{gameObject.name}'. Hit normalization will not work.");
        }
    }

    /// <summary>
    /// Converts a world-space hit point to normalized [0,1] 2D coordinates
    /// relative to the attached BoxCollider's local extents.
    /// Automatically detects which axis is the face normal (thinnest axis)
    /// and uses the remaining two axes as the 2D surface plane.
    /// </summary>
    private Vector2 GetNormalizedHitCoordinates(RaycastHit hitInfo)
    {
        if (boxCollider == null)
            return Vector2.zero;

        // Transform the hit point into the collider's local space
        Vector3 localHit = transform.InverseTransformPoint(hitInfo.point);

        Vector3 center = boxCollider.center;
        Vector3 size   = boxCollider.size;

        Vector3 localOffset = localHit - center;

        // Detect the face normal axis as the thinnest dimension, use the other two as the surface plane
        float normX, normY;

        if (size.z <= size.x && size.z <= size.y)
        {
            // Z is the thin axis — surface plane is XY
            normX = (localOffset.x / (size.x * 0.5f) + 1f) * 0.5f;
            normY = (localOffset.y / (size.y * 0.5f) + 1f) * 0.5f;
        }
        else if (size.y <= size.x && size.y <= size.z)
        {
            // Y is the thin axis — surface plane is XZ
            normX = (localOffset.x / (size.x * 0.5f) + 1f) * 0.5f;
            normY = (localOffset.z / (size.z * 0.5f) + 1f) * 0.5f;
        }
        else
        {
            // X is the thin axis — surface plane is YZ
            normX = (localOffset.y / (size.y * 0.5f) + 1f) * 0.5f;
            normY = (localOffset.z / (size.z * 0.5f) + 1f) * 0.5f;
        }

        return new Vector2(Mathf.Clamp01(normX), Mathf.Clamp01(normY));
    }

    private void PublishHit(RaycastHit hitInfo)
    {
        Vector2 normalized = GetNormalizedHitCoordinates(hitInfo);

        ros.Publish(rosTopic, new PointMsg(
            x: normalized.x,
            y: normalized.y,
            z: 0.0
        ));

        Debug.Log($"[RosEyeInteractable] Hit '{hitInfo.collider.gameObject.name}' " +
                  $"— normalized UV: ({normalized.x:F3}, {normalized.y:F3}) published to '{rosTopic}'");
    }

    public virtual void isHit(RaycastHit hitInfo)
    {
        PublishHit(hitInfo);

        if (!string.IsNullOrEmpty(gazeKey))
            GazeRegistry.SetGazed(gazeKey);
    }

    public virtual void isSelected(RaycastHit hitInfo)
    {
        PublishHit(hitInfo);

        if (!string.IsNullOrEmpty(gazeKey))
            GazeRegistry.SetGazed(gazeKey);
    }

    public virtual void isUnselected()
    {
        if (!string.IsNullOrEmpty(gazeKey))
            GazeRegistry.ClearGazed(gazeKey);
    }
}
