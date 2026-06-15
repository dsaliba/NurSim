using UnityEngine;

public class PinObjectOffset : MonoBehaviour
{
    [Header("Target Object")]
    public GameObject objectToPin;

    [Header("Offsets")]
    public Vector3 positionOffset;
    public Vector3 rotationOffsetEuler;

    void LateUpdate()
    {
        if (objectToPin == null)
            return;

        // Position offset relative to this object's transform 
        objectToPin.transform.position =
            transform.position + transform.rotation * positionOffset;

        // Rotation offset relative to this object's rotation
        objectToPin.transform.rotation =
            transform.rotation * Quaternion.Euler(rotationOffsetEuler);
    }
}
