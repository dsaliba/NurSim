using UnityEngine;

/// <summary>
/// Eye interactable that contributes a string key to the shared GazeRegistry.
/// When this object is gazed at, "/unity/gazed_object" publishes its key.
/// When gaze leaves, it publishes "none" (unless another keyed object is now gazed).
///
/// Add this component alongside any collider. Set the Key field in the Inspector.
/// Multiple instances all share the same static GazeRegistry and single ROS topic.
/// </summary>
public class KeyEyeInteractable : MonoBehaviour, EyeRayInterface
{
    [SerializeField]
    public string Key = "";

    public virtual void isHit(RaycastHit hitInfo)
    {
        GazeRegistry.SetGazed(Key);
    }

    public virtual void isSelected(RaycastHit hitInfo)
    {
        GazeRegistry.SetGazed(Key);
    }

    public virtual void isUnselected()
    {
        GazeRegistry.ClearGazed(Key);
    }
}
